using System.Net;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.DashboardEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

public class RoutingRulesTests
{
    private static readonly Guid Water = Guid.NewGuid(), Roads = Guid.NewGuid();
    private static ScopedRule Rule(Guid? cat, string? path, int depth, int sort = 0) => new(Guid.NewGuid(), cat, path, depth, Guid.NewGuid(), sort);

    [Fact]
    public void Most_specific_rule_wins_deeper_location_then_named_category()
    {
        var any = Rule(null, null, 0);
        var byCategory = Rule(Water, null, 0);
        var bySector = Rule(null, "/s/", 0);
        var sectorAndCategory = Rule(Water, "/s/", 0);
        var street = Rule(null, "/s/t/", 1);
        var all = new[] { any, byCategory, bySector, sectorAndCategory, street };

        Assert.Equal(any.Id, RoutingEngine.Resolve(all, Roads, "/other/")!.Id);
        Assert.Equal(byCategory.Id, RoutingEngine.Resolve(all, Water, "/other/")!.Id);
        Assert.Equal(bySector.Id, RoutingEngine.Resolve(all, Roads, "/s/x/")!.Id);
        Assert.Equal(sectorAndCategory.Id, RoutingEngine.Resolve(all, Water, "/s/x/")!.Id);
        Assert.Equal(street.Id, RoutingEngine.Resolve(all, Roads, "/s/t/h/")!.Id); // a street rule is more specific than a sector rule
        Assert.Null(RoutingEngine.Resolve([byCategory, bySector], Roads, "/other/"));
        Assert.Null(RoutingEngine.Resolve([bySector], Roads, null)); // no location, no location-scoped rule
    }

    [Fact]
    public void Ties_go_to_the_lower_sort_order()
    {
        var late = Rule(Water, null, 0, sort: 5);
        var early = Rule(Water, null, 0, sort: 1);
        Assert.Equal(early.Id, RoutingEngine.Resolve([late, early], Water, null)!.Id);
    }

    [Fact]
    public void Location_prefix_must_match_whole_path_segments()
    {
        var rule = Rule(null, "/a1/", 0);
        Assert.Null(RoutingEngine.Resolve([rule], null, "/a12/b/")); // "/a12/" does not start with "/a1/"
    }

    [Theory]
    [InlineData("Urgent", 24)]
    [InlineData("High", 72)]
    [InlineData("Normal", 168)]
    [InlineData("Low", 336)]
    public void Sla_hours_by_priority(string priority, int hours)
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(Sla.IsOverdue(priority, now.AddHours(-hours + 1), now));
        Assert.True(Sla.IsOverdue(priority, now.AddHours(-hours - 1), now));
    }
}

[Collection("db")]
public class RoutingAndDashboardTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<TeamDto> NewTeam(Society s, string name)
    {
        var dept = (await s.Manager.Client.GetFromJsonAsync<List<DepartmentDto>>("/departments"))!.FirstOrDefault()
                   ?? await Ok<DepartmentDto>(s.Manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Works", null, null)));
        return await Ok<TeamDto>(s.Manager.Client.PostAsJsonAsync("/teams", new CreateTeam(dept.Id, name)));
    }

    private static async Task<Guid> NewComplaint(Society s, string text = "No water", bool withCategory = true)
    {
        var res = await Ok<CreatedDto>(s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, withCategory ? s.CategoryId : null, s.HouseId, null, null, null)));
        return res.Id;
    }

    private static Task<HttpResponseMessage> Review(Society s, Guid id) =>
        s.Office.Client.PostAsJsonAsync($"/complaints/{id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));

    // RT-01 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rule_management_is_for_managers_and_validates_its_references()
    {
        var s = await Society.CreateAsync(app);
        var team = await NewTeam(s, "Crew A");

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.PostAsJsonAsync("/routing-rules", new SaveRule("x", null, null, team.Id, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule(" ", null, null, team.Id, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("x", null, null, Guid.NewGuid(), null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("x", Guid.NewGuid(), null, team.Id, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("x", null, Guid.NewGuid(), team.Id, null, null))).StatusCode);

        var rule = await Ok<RuleDto>(s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("Water to A", s.CategoryId, null, team.Id, 3, null)));
        var edited = await Ok<RuleDto>(s.Manager.Client.PutAsJsonAsync($"/routing-rules/{rule.Id}", new SaveRule("Water crew", s.CategoryId, s.LocationId, team.Id, 1, false)));
        Assert.Equal(("Water crew", 1, false), (edited.Name, edited.SortOrder, edited.IsActive));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Manager.Client.PutAsJsonAsync($"/routing-rules/{Guid.NewGuid()}", new SaveRule("x", null, null, team.Id, null, null))).StatusCode);
        Assert.Single(await Ok<List<RuleDto>>(s.Supervisor.Client.GetAsync("/routing-rules")));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/routing-rules")).StatusCode);
    }

    [Fact]
    public async Task A_job_created_without_a_team_goes_to_the_team_the_most_specific_rule_names()
    {
        var s = await Society.CreateAsync(app);
        var general = await NewTeam(s, "General");
        var waterCrew = await NewTeam(s, "Water crew");
        var houseCrew = await NewTeam(s, "House 1 crew");
        async Task Add(string name, Guid? cat, Guid? loc, Guid team) =>
            (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule(name, cat, loc, team, null, null))).EnsureSuccessStatusCode();

        async Task<Guid?> JobTeam()
        {
            var id = await NewComplaint(s);
            await Review(s, id);
            return (await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/jobs", new CreateJob(null, null)))).AssignedTeamId;
        }

        Assert.Null(await JobTeam()); // no rules yet
        await Add("fallback", null, null, general.Id);
        Assert.Equal(general.Id, await JobTeam());
        await Add("water", s.CategoryId, null, waterCrew.Id);
        Assert.Equal(waterCrew.Id, await JobTeam());
        await Add("house 1", null, s.LocationId, houseCrew.Id);
        Assert.Equal(houseCrew.Id, await JobTeam()); // a location rule is more specific than a category-only rule

        // an explicit team always overrides the rules
        var id2 = await NewComplaint(s);
        await Review(s, id2);
        Assert.Equal(general.Id, (await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id2}/jobs", new CreateJob(null, general.Id)))).AssignedTeamId);
    }

    // RT-03 / AI-16 --------------------------------------------------------------------------------------------------------

    private async Task SetAi(Guid complaintId, string key, string language = "ur", float confidence = 0.9f) =>
        await Sql("UPDATE complaints SET ai_status = 'done', ai_category_key = @k, ai_summary = 'Sewage overflow', language = @l, ai_confidence = @c, ai_model = 'm1', ai_prompt_version = 'v3' WHERE id = @id",
            ("k", key), ("l", language), ("c", confidence), ("id", complaintId));

    [Fact]
    public async Task Review_queue_lists_new_complaints_urgent_first_with_ai_suggestions_and_a_suggested_team()
    {
        var s = await Society.CreateAsync(app);
        var crew = await NewTeam(s, "Sewer crew");
        var cats = await s.Manager.Client.GetFromJsonAsync<List<CategoryDto>>("/categories");
        var sewerage = cats!.Single(c => c.Key == "sewerage");
        await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("sewer", sewerage.Id, null, crew.Id, null, null));

        var older = await NewComplaint(s, "Tap dripping");
        var overflow = await NewComplaint(s, "Gutter overflowing", withCategory: false);
        await SetAi(overflow, "sewerage");
        await Sql("UPDATE complaints SET priority = 'Urgent' WHERE id = @id", ("id", overflow));

        var queue = await Ok<List<QueueItemDto>>(s.Supervisor.Client.GetAsync("/review-queue"));
        Assert.Equal([overflow, older], queue.Select(q => q.Complaint.Id));
        var top = queue[0];
        Assert.Equal((sewerage.Id, "High", crew.Id, "Sewer crew"), (top.SuggestedCategoryId, top.SuggestedPriority, top.SuggestedTeamId, top.SuggestedTeamName));
        Assert.Equal("Sewage overflow", top.Complaint.AiSummary);
        Assert.Null(queue[1].SuggestedTeamId);

        await Review(s, older);
        await Sql("UPDATE complaints SET status = 'Closed' WHERE id = @id", ("id", older));
        Assert.Equal([overflow], (await Ok<List<QueueItemDto>>(s.Supervisor.Client.GetAsync("/review-queue"))).Select(q => q.Complaint.Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/review-queue")).StatusCode);
    }

    [Fact]
    public async Task Accepting_or_overriding_the_ai_category_is_recorded_edited_fields_are_audited_and_accuracy_is_queryable()
    {
        var s = await Society.CreateAsync(app);
        var cats = (await s.Manager.Client.GetFromJsonAsync<List<CategoryDto>>("/categories"))!;
        var sewerage = cats.Single(c => c.Key == "sewerage");
        var roads = cats.Single(c => c.Key == "roads");

        var accepted = await NewComplaint(s, withCategory: false); await SetAi(accepted, "sewerage", "ur");
        var overridden = await NewComplaint(s, withCategory: false); await SetAi(overridden, "sewerage", "ur");
        var englishHit = await NewComplaint(s, withCategory: false); await SetAi(englishHit, "roads", "en");

        var a = await Ok<ComplaintDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{accepted}/triage", new Triage(true, null, null, null, null)));
        Assert.Equal((sewerage.Id, "High"), (a.CategoryId, a.Priority)); // priority follows the category default
        var o = await Ok<ComplaintDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{overridden}/triage", new Triage(null, roads.Id, "Urgent", null, "Near the mosque")));
        Assert.Equal((roads.Id, "Urgent", "Near the mosque"), (o.CategoryId, o.Priority, o.LocationText));
        await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{englishHit}/triage", new Triage(null, roads.Id, null, null, null));

        // a later decision replaces the earlier one for the same field
        await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{overridden}/triage", new Triage(null, sewerage.Id, null, null, null));

        var report = await Ok<List<AccuracyRow>>(s.Manager.Client.GetAsync("/reports/ai-accuracy"));
        Assert.Equal([("roads", "en", 1, 1), ("sewerage", "ur", 2, 2)], report.Select(r => (r.Suggested, r.Language!, r.Total, r.Accepted)));
        await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{overridden}/triage", new Triage(null, roads.Id, null, null, null));
        var again = await Ok<List<AccuracyRow>>(s.Manager.Client.GetAsync("/reports/ai-accuracy"));
        Assert.Equal((2, 1, 0.5), (again.Single(r => r.Suggested == "sewerage").Total, again.Single(r => r.Suggested == "sewerage").Accepted, again.Single(r => r.Suggested == "sewerage").AcceptRate));

        var audit = await Ok<List<AdminEndpoints.AuditDto>>(s.Manager.Client.GetAsync($"/audit?entityType=Complaint&entityId={overridden}&action=updated"));
        Assert.NotEmpty(audit);
        var supervisorId = await Me(s.Supervisor);
        Assert.All(audit, e => Assert.Equal(supervisorId, e.ActorUserId));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/reports/ai-accuracy")).StatusCode);
    }

    private static async Task<Guid> Me(ApiSession who) => (await who.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

    [Fact]
    public async Task Triage_validates_input_and_only_works_before_assignment()
    {
        var s = await Society.CreateAsync(app);
        var id = await NewComplaint(s);
        var post = (Triage t) => s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/triage", t);

        Assert.Equal(HttpStatusCode.BadRequest, (await post(new Triage(true, null, null, null, null))).StatusCode); // no AI suggestion yet
        await SetAi(id, "no-such-category");
        Assert.Equal(HttpStatusCode.BadRequest, (await post(new Triage(true, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await post(new Triage(null, Guid.NewGuid(), null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await post(new Triage(null, null, "Critical", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await post(new Triage(null, null, null, Guid.NewGuid(), null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await post(new Triage(null, null, "Low", s.LocationId, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{Guid.NewGuid()}/triage", new Triage(null, null, "Low", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsJsonAsync($"/complaints/{id}/triage", new Triage(null, null, "Low", null, null))).StatusCode);

        await Review(s, id);
        await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/jobs", new CreateJob(null, null));
        Assert.Equal(HttpStatusCode.Conflict, (await post(new Triage(null, null, "High", null, null))).StatusCode);
    }

    // DB-01 -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dashboard_figures_match_a_hand_checked_sample()
    {
        var s = await Society.CreateAsync(app);
        var crew = await NewTeam(s, "Crew A");

        await NewComplaint(s, "New one");
        var stuck = await NewComplaint(s, "Stuck for ten days");
        await Review(s, stuck);
        var stuckJob = await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{stuck}/jobs", new CreateJob(null, crew.Id)));
        await Sql("UPDATE complaints SET created_at = now() - interval '10 days' WHERE id = @id", ("id", stuck));

        var fixedA = await NewComplaint(s, "Fixed slowly");
        var fixedB = await NewComplaint(s, "Fixed then reopened");
        foreach (var id in new[] { fixedA, fixedB })
        {
            await Review(s, id);
            await s.FinishWorkAsync(await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/jobs", new CreateJob(null, null))));
        }
        await Sql("UPDATE complaints SET created_at = now() - interval '30 hours', resolved_at = now() - interval '10 hours' WHERE id = @id", ("id", fixedA)); // 20h
        await Sql("UPDATE complaints SET created_at = now() - interval '12 hours', resolved_at = now() - interval '2 hours' WHERE id = @id", ("id", fixedB)); // 10h
        Assert.Equal(HttpStatusCode.OK, (await s.Resident.Client.PostAsJsonAsync($"/complaints/{fixedB}/transition", new Transition(ComplaintStatus.UnderReview, "Still leaking", null))).StatusCode);

        var d = await Ok<Summary>(s.Manager.Client.GetAsync("/dashboard/summary"));
        Assert.Equal(4, d.Total);
        Assert.Equal(new Dictionary<string, int> { ["New"] = 1, ["Assigned"] = 1, ["Resolved"] = 1, ["UnderReview"] = 1 },
            d.ByStatus.Where(r => r.Count > 0).ToDictionary(r => r.Key, r => r.Count));
        Assert.Equal(15.0, d.AverageResolutionHours);
        Assert.Equal(2, d.ResolvedCount);
        Assert.Equal((1, 1), (d.ReopenedComplaints, d.ReopenEvents));

        var overdue = Assert.Single(d.OverdueJobs);
        Assert.Equal((stuckJob.Id, "High"), (overdue.JobId, overdue.Priority)); // water defaults to High
        Assert.InRange(overdue.HoursOverdue, 167.9, 168.2); // 240h old, 72h limit

        var load = Assert.Single(d.TeamWorkload);
        Assert.Equal(("Crew A", 1, 0, 0), (load.Team, load.Open, load.InProgress, load.PendingVerification));
        Assert.Equal(("Unassigned", 4), (d.ByDepartment.Single().Name, d.ByDepartment.Single().Count));
        Assert.Equal(("Block A", 4), (d.BySector.Single().Name, d.BySector.Single().Count));

        // a date range narrows the complaint figures but not the live job view
        var lastDay = await Ok<Summary>(s.Manager.Client.GetAsync($"/dashboard/summary?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-24).ToString("o"))}"));
        Assert.Equal(2, lastDay.Total); // created in the last day: the new one and the one resolved in 10h
        Assert.Equal(10.0, lastDay.AverageResolutionHours);
        Assert.Single(lastDay.OverdueJobs);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/dashboard/summary")).StatusCode);
    }

    [Fact]
    public async Task Dashboard_for_an_empty_organization_is_all_zeros_and_other_organizations_are_invisible()
    {
        var empty = await Society.CreateAsync(app);
        var busy = await Society.CreateAsync(app);
        await NewComplaint(busy);

        var d = await Ok<Summary>(empty.Manager.Client.GetAsync("/dashboard/summary"));
        Assert.Equal(0, d.Total);
        Assert.Null(d.AverageResolutionHours);
        Assert.Empty(d.OverdueJobs);
        Assert.Empty(d.TeamWorkload);
        Assert.All(d.ByStatus, r => Assert.Equal(0, r.Count));
    }
}
