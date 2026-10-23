using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

public class PriorityRulesUnitTests
{
    [Theory]
    [InlineData("Low", 0, "Low")]
    [InlineData("Low", 1, "Normal")]
    [InlineData("Normal", 2, "Urgent")]
    [InlineData("High", 2, "Urgent")]   // risk lifts a priority but stops at urgent
    [InlineData("Urgent", 2, "Urgent")]
    public void Location_risk_lifts_the_category_default_up_to_urgent(string baseline, int risk, string expected)
    {
        var r = PriorityEngine.Compute(baseline, risk, safetyFlag: false);
        Assert.Equal(expected, r.Priority);
        Assert.False(r.RequiresPerson);
    }

    [Fact]
    public void A_safety_flag_always_wins_and_needs_a_person()
    {
        var r = PriorityEngine.Compute("Low", 0, safetyFlag: true);
        Assert.Equal(("Safety", true), (r.Priority, r.RequiresPerson));
    }

    [Theory]
    [InlineData("There is a live wire hanging near the park", true)]
    [InlineData("SEWAGE OVERFLOW in street 4", true)]
    [InlineData("کھلا مین ہول بچوں کے لیے خطرہ ہے", true)]
    [InlineData("گلی میں ننگی تار لٹک رہی ہے", true)]
    [InlineData("The park bench is broken", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Safety_terms_are_found_in_english_and_urdu(string? text, bool expected) => Assert.Equal(expected, SafetyTerms.IsSafety(text));

    [Theory]
    [InlineData(true, 0.9f, 0.95f, "High", false, false, true, null)]
    [InlineData(false, 0.9f, 0.99f, "High", false, false, true, "auto_route_off")]
    [InlineData(true, 0.9f, 0.89f, "High", false, false, true, "below_threshold")]
    [InlineData(true, 0.9f, null, "High", false, false, true, "no_confidence")]
    [InlineData(true, 0.9f, 0.99f, "High", false, true, true, "category_set_by_person")]
    [InlineData(true, 0.9f, 0.99f, "High", false, false, false, "no_matching_rule")]
    [InlineData(true, 0.9f, 0.99f, "Safety", true, false, true, "safety")]
    public void Auto_routing_needs_every_condition(bool on, float threshold, float? confidence, string priority, bool safety, bool byPerson, bool team, string? why) =>
        Assert.Equal(why, AutoRoutePolicy.WhyQueued(on, threshold, confidence, priority, safety, byPerson, team));

    [Fact]
    public void Elapsed_hours_deadline_is_a_plain_addition()
    {
        var start = new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero);
        Assert.Equal(start.AddHours(24), SlaCalculator.Deadline(start, 24, false, TimeZoneInfo.Utc));
    }

    [Fact]
    public void Business_hours_deadline_counts_only_working_time_in_the_organization_timezone()
    {
        var karachi = SlaCalculator.ZoneOrUtc("Asia/Karachi"); // UTC+5, no daylight saving
        // Friday 2026-03-06 16:00 local; 4 working hours = 2 left on Friday (to 18:00) + 2 on Saturday from 09:00 -> Saturday 11:00.
        var start = new DateTimeOffset(2026, 3, 6, 16, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 3, 7, 11, 0, 0, TimeSpan.FromHours(5)), SlaCalculator.Deadline(start, 4, true, karachi));

        // Saturday 17:00 local + 3 working hours skips Sunday: 1 on Saturday, then Monday 09:00 + 2 = 11:00.
        var saturday = new DateTimeOffset(2026, 3, 7, 17, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 11, 0, 0, TimeSpan.FromHours(5)), SlaCalculator.Deadline(saturday, 3, true, karachi));

        // Reported at night: the clock starts at 09:00.
        var night = new DateTimeOffset(2026, 3, 4, 23, 0, 0, TimeSpan.FromHours(5));
        Assert.Equal(new DateTimeOffset(2026, 3, 5, 11, 0, 0, TimeSpan.FromHours(5)), SlaCalculator.Deadline(night, 2, true, karachi));
    }

    [Theory]
    [InlineData(4, 24, "Urgent", null)]
    [InlineData(0, 24, "Urgent", "hours_must_be_positive")]
    [InlineData(30, 24, "Urgent", "respond_after_resolve")]
    [InlineData(4, 24, "Bogus", "invalid_priority")]
    public void Sla_policies_are_validated(int respond, int resolve, string priority, string? error) => Assert.Equal(error, RuleChecks.Sla(respond, resolve, priority));

    [Fact]
    public void Two_active_rules_with_the_same_scope_and_order_but_different_teams_are_ambiguous()
    {
        var cat = Guid.NewGuid();
        var teamA = Guid.NewGuid();
        var existing = new[] { new RuleChecks.RuleKey(Guid.NewGuid(), cat, null, teamA, 1, true) };
        Assert.Equal("ambiguous_rule", RuleChecks.Ambiguous(new(null, cat, null, Guid.NewGuid(), 1, true), existing));
        Assert.Null(RuleChecks.Ambiguous(new(null, cat, null, Guid.NewGuid(), 2, true), existing)); // different order
        Assert.Null(RuleChecks.Ambiguous(new(null, cat, null, Guid.NewGuid(), 1, false), existing)); // inactive
        Assert.Null(RuleChecks.Ambiguous(new(null, cat, null, teamA, 1, true), existing)); // same team, harmless
    }
}

[Collection("db")]
public class PriorityRulesTests(TestApp app)
{
    private static async Task<CreatedDto> Complain(Society s, string text, Guid? category = null) =>
        (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, category, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;

    private static Task<ComplaintDetailDto?> Detail(Society s, Guid id) => s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}");

    private async Task Enrich(Society s, Guid id)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>().RunCoreAsync(s.Org, id, 0, default);
    }

    private static async Task<TeamDto> NewTeam(Society s, string name)
    {
        var dept = (await s.Manager.Client.GetFromJsonAsync<List<DepartmentDto>>("/departments"))!.FirstOrDefault()
                   ?? (await (await s.Manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Works", null, null))).Content.ReadFromJsonAsync<DepartmentDto>())!;
        return (await (await s.Manager.Client.PostAsJsonAsync("/teams", new CreateTeam(dept.Id, name))).Content.ReadFromJsonAsync<TeamDto>())!;
    }

    [Fact]
    public async Task A_safety_complaint_is_flagged_at_once_and_alerts_staff_and_prompts_the_resident_even_with_the_ai_down()
    {
        var s = await Society.CreateAsync(app);
        var c = await Complain(s, "Live wire hanging in the street, children play here");

        var d = (await Detail(s, c.Id))!.Complaint;
        Assert.True(d.SafetyFlag);
        Assert.Equal("Safety", d.Priority);
        Assert.NotNull(d.DueAt);

        await using (var scope = app.Services.CreateAsyncScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<OutboxProcessor>();
            for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
        }
        var staff = await s.Supervisor.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications");
        var resident = await s.Resident.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications");
        Assert.Contains(staff!.Items, n => n.Template == NotificationTemplates.SafetyAlert);
        Assert.Contains(resident!.Items, n => n.Template == NotificationTemplates.SafetyPrompt && n.Body.Contains("1122"));
    }

    [Fact]
    public async Task A_safety_complaint_is_never_routed_automatically_even_when_the_category_allows_it()
    {
        var s = await Society.CreateAsync(app);
        var team = await NewTeam(s, "Crew");
        (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("all", null, null, team.Id, null, null))).EnsureSuccessStatusCode();
        (await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new SaveAutoRoute(true, 0.8f))).EnsureSuccessStatusCode();

        var c = await Complain(s, "Sparking transformer next to the school");
        await Enrich(s, c.Id);

        var d = (await Detail(s, c.Id))!.Complaint;
        Assert.Equal((ComplaintStatus.New, false, "Safety"), (d.Status, d.AutoRouted, d.Priority));
    }

    [Fact]
    public async Task A_confident_suggestion_in_an_auto_route_category_assigns_the_complaint_and_a_quiet_one_does_not()
    {
        var s = await Society.CreateAsync(app);
        var team = await NewTeam(s, "Water crew");
        (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("water", s.CategoryId, null, team.Id, null, null))).EnsureSuccessStatusCode();
        var ai = app.Services.GetRequiredService<FakeAiClient>();

        // Off by default: queued for a supervisor.
        var first = await Complain(s, "No water since morning");
        await Enrich(s, first.Id);
        Assert.Equal((ComplaintStatus.New, false), ((await Detail(s, first.Id))!.Complaint.Status, (await Detail(s, first.Id))!.Complaint.AutoRouted));

        (await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new SaveAutoRoute(true, 0.85f))).EnsureSuccessStatusCode();
        var second = await Complain(s, "No water since morning, again");
        await Enrich(s, second.Id);
        var routed = (await Detail(s, second.Id))!;
        Assert.Equal((ComplaintStatus.Assigned, true), (routed.Complaint.Status, routed.Complaint.AutoRouted));
        Assert.Equal(team.Id, Assert.Single(routed.Jobs).AssignedTeamId);
        Assert.Equal(["Routed automatically", "Job " + routed.Jobs[0].Number + " created automatically"], routed.History.Where(h => h.ActorUserId is null && h.To != ComplaintStatus.New).Select(h => h.Reason));

        var previous = ai.Reply;
        ai.Reply = previous with { Confidence = 0.6f };
        try
        {
            var third = await Complain(s, "No water, I think");
            await Enrich(s, third.Id);
            Assert.Equal(ComplaintStatus.New, (await Detail(s, third.Id))!.Complaint.Status);
        }
        finally { ai.Reply = previous; }
    }

    [Fact]
    public async Task A_category_chosen_by_a_person_is_not_auto_routed()
    {
        var s = await Society.CreateAsync(app);
        var team = await NewTeam(s, "Crew");
        (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("all", null, null, team.Id, null, null))).EnsureSuccessStatusCode();
        (await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new SaveAutoRoute(true, 0.8f))).EnsureSuccessStatusCode();

        var c = await Complain(s, "No water", s.CategoryId);
        await Enrich(s, c.Id);
        Assert.Equal(ComplaintStatus.New, (await Detail(s, c.Id))!.Complaint.Status);
    }

    [Fact]
    public async Task Location_risk_raises_priority_and_the_sla_policy_sets_the_deadline()
    {
        var s = await Society.CreateAsync(app);
        (await s.Manager.Client.PutAsJsonAsync($"/locations/{s.LocationId}/risk", new SaveRisk(2))).EnsureSuccessStatusCode();
        var policy = await s.Manager.Client.PostAsJsonAsync("/sla-policies", new SaveSla(null, "Urgent", 2, 6, false, null));
        Assert.Equal(HttpStatusCode.Created, policy.StatusCode);

        var c = await Complain(s, "Water leaking", s.CategoryId); // water defaults to High, +2 risk caps at Urgent
        var d = (await Detail(s, c.Id))!.Complaint;
        Assert.Equal("Urgent", d.Priority);
        Assert.Equal(6, Math.Round((d.DueAt!.Value - d.CreatedAt).TotalHours));
        Assert.Equal(2, Math.Round((d.RespondBy!.Value - d.CreatedAt).TotalHours));
    }

    [Fact]
    public async Task Rule_administration_rejects_invalid_and_ambiguous_settings_and_is_for_managers()
    {
        var s = await Society.CreateAsync(app);
        var a = await NewTeam(s, "A");
        var b = await NewTeam(s, "B");

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/sla-policies", new SaveSla(null, "Urgent", 10, 5, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.PostAsJsonAsync("/sla-policies", new SaveSla(null, "Urgent", 1, 5, null, null))).StatusCode);
        (await s.Manager.Client.PostAsJsonAsync("/sla-policies", new SaveSla(null, "Urgent", 1, 5, null, null))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Manager.Client.PostAsJsonAsync("/sla-policies", new SaveSla(null, "Urgent", 2, 8, null, null))).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new SaveAutoRoute(true, 0.2f))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync($"/locations/{s.LocationId}/risk", new SaveRisk(3))).StatusCode);

        (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("one", s.CategoryId, null, a.Id, 1, null))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("two", s.CategoryId, null, b.Id, 1, null))).StatusCode);
        (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("two", s.CategoryId, null, b.Id, 2, null))).EnsureSuccessStatusCode();
    }
}
