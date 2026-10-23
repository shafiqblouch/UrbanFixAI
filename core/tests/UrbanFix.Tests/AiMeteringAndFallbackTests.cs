using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.OrgSetup;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;

namespace UrbanFix.Tests;

public class RulesOnlyClassifierTests
{
    private static readonly RulesOnlyClassifier.Candidate[] Seeded =
    [
        new("water", "Water supply"), new("roads", "Roads and potholes"), new("streetlights", "Street lights"),
        new("sewerage", "Sewerage and drainage"), new("sanitation", "Sanitation and garbage"), new("parks", "Parks and green belts"),
        new("building-maintenance", "Building maintenance"), new("parking", "Parking"),
    ];

    [Theory]
    [InlineData("No water in the tap since morning", "water")]
    [InlineData("pani nahi aa raha", "water")]
    [InlineData("نل میں پانی نہیں آ رہا", "water")]
    [InlineData("Big pothole on the main road", "roads")]
    [InlineData("سڑک پر گڑھا ہے", "roads")]
    [InlineData("The street light near the gate is out", "streetlights")]
    [InlineData("Sewage overflow from the manhole", "sewerage")]
    [InlineData("گٹر ابل رہا ہے", "sewerage")]
    [InlineData("Garbage has not been picked up", "sanitation")]
    [InlineData("kachra nahi uthaya gaya", "sanitation")]
    [InlineData("The swing in the park is broken", "parks")]
    [InlineData("A car is parked blocking my gate", "parking")]
    public void Common_wording_in_three_languages_finds_the_category(string text, string key) =>
        Assert.Equal(key, RulesOnlyClassifier.Classify(text, Seeded)?.Key);

    [Fact]
    public void Nothing_recognisable_gives_no_guess_and_the_organizations_own_categories_match_on_their_name()
    {
        Assert.Null(RulesOnlyClassifier.Classify("Something is wrong, please help", Seeded));
        Assert.Null(RulesOnlyClassifier.Classify("", Seeded));
        Assert.Equal("noise", RulesOnlyClassifier.Classify("Constant noise from the generator", [new("noise", "Noise complaints")])?.Key);
    }

    [Fact]
    public void The_confidence_is_always_below_anything_that_could_route_automatically() =>
        Assert.True(RulesOnlyClassifier.Confidence < 0.5f);
}

public class AiPricingTests
{
    [Fact]
    public void Cost_comes_from_the_configured_price_and_is_unknown_without_one()
    {
        var options = new AiOptions { Pricing = { ["m"] = new ModelPrice { InputPerMillion = 2m, OutputPerMillion = 10m } } };
        Assert.Equal(0.7m, MeteredAiClient.Cost(options, "m", new AiUsage(100_000, 50_000))); // $0.20 in + $0.50 out
        Assert.Null(MeteredAiClient.Cost(options, "other", new AiUsage(1, 1)));
    }
}

[Collection("db")]
public class AiMeteringAndFallbackTests(TestApp app)
{
    private static async Task<Guid> Complain(Society s, string text, Guid? category = null) =>
        (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, category, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!.Id;

    private static async Task<ComplaintDto> Detail(Society s, Guid id) => (await s.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!.Complaint;

    private async Task Enrich(Society s, Guid id, int retries = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>().RunCoreAsync(s.Org, id, retries, default);
    }

    private static Task SetPolicy(Society s, OrgPolicy p) => s.Manager.Client.PutAsJsonAsync("/organization/policies", p).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private static Task<UsageReport> Report(Society s) => s.Manager.Client.GetFromJsonAsync<UsageReport>("/reports/ai-usage")!;

    // AI-13 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_call_is_counted_with_its_tokens_and_cost_and_the_report_gives_the_cost_per_complaint()
    {
        var s = await Society.CreateAsync(app);
        var one = await Complain(s, "No water since morning");
        var two = await Complain(s, "Water pressure is very low");
        await Enrich(s, one);
        await Enrich(s, two);

        var r = await Report(s);
        Assert.Equal((2, 240L, 60L), (r.Calls, r.InputTokens, r.OutputTokens));
        var row = Assert.Single(r.ByTask);
        Assert.Equal(("understanding", "fake-model", 2), (row.Task, row.Model, row.Calls));
        Assert.Equal(0.00054m, r.CostUsd); // 2 x (120 x $1 + 30 x $5) per million
        Assert.True(r.CostComplete);
        Assert.Equal((2, 0.00027m, 150L), (r.ComplaintsWithAi, r.CostPerComplaint, r.TokensPerComplaint));
        Assert.Equal(300L, r.MonthTokens);
    }

    [Fact]
    public async Task A_model_without_a_price_is_counted_in_tokens_but_the_cost_is_marked_incomplete()
    {
        var s = await Society.CreateAsync(app);
        var id = await Complain(s, "Water leak");
        await Enrich(s, id);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UrbanFix.Infrastructure.Persistence.AppDbContext>();
            await db.ChangeTenantAsync(s.Org, null);
            db.AiUsage.Add(new AiUsageEntry { OrganizationId = s.Org, ComplaintId = id, Task = "photo", Model = "unpriced-model", InputTokens = 500, OutputTokens = 40 });
            await db.SaveChangesAsync();
        }
        var r = await Report(s);
        Assert.False(r.CostComplete);
        Assert.Null(r.CostUsd);
        Assert.Contains(r.ByTask, t => t.Task == "photo" && t.CostUsd is null);
        Assert.Contains(r.ByTask, t => t.Task == "understanding" && t.CostUsd == 0.00027m);
    }

    [Fact]
    public async Task Usage_is_per_organization_and_the_report_is_for_those_who_read_dashboards()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        await Enrich(a, await Complain(a, "No water"));
        Assert.Equal(0, (await Report(b)).Calls);
        Assert.Equal(1, (await Report(a)).Calls);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Resident.Client.GetAsync("/reports/ai-usage")).StatusCode);
    }

    [Fact]
    public async Task Over_the_monthly_allowance_the_ai_is_not_called_and_the_complaint_falls_back_to_rules_only()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        await Enrich(s, await Complain(s, "No water since morning")); // 150 tokens used
        await SetPolicy(s, OrgPolicy.Default with { AiMonthlyTokenCap = 100 });

        var calls = ai.Calls.Count;
        var id = await Complain(s, "The tap is dripping and water pressure is low");
        await Enrich(s, id);

        Assert.Equal(calls, ai.Calls.Count); // the service was not asked
        var c = await Detail(s, id);
        Assert.Equal(("rules_only", "water", 0.3f), (c.AiStatus, c.AiCategoryKey, c.AiConfidence));
        Assert.Equal(1, (await Report(s)).Calls); // nothing more was spent
        Assert.Equal(100, (await Report(s)).MonthlyTokenCap);
    }

    [Fact]
    public async Task A_cap_of_zero_switches_the_ai_off_for_the_organization()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        await SetPolicy(s, OrgPolicy.Default with { AiMonthlyTokenCap = 0 });
        var calls = ai.Calls.Count;
        var id = await Complain(s, "Garbage not collected");
        await Enrich(s, id);
        Assert.Equal(calls, ai.Calls.Count);
        Assert.Equal(("rules_only", "sanitation"), ((await Detail(s, id)).AiStatus, (await Detail(s, id)).AiCategoryKey));
    }

    [Fact]
    public async Task The_cap_is_validated()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { AiMonthlyTokenCap = -1 })).StatusCode);
    }

    // AI-14 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task When_the_ai_stays_down_the_complaint_still_reaches_the_queue_with_a_keyword_guess()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var attempts = app.Services.GetRequiredService<UrbanFix.Infrastructure.Jobs.JobOptions>().Attempts;
        var id = await Complain(s, "Big pothole on the road outside house 9");

        ai.Down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Enrich(s, id));
            await Enrich(s, id, attempts); // out of retries
        }
        finally { ai.Down = false; }

        var c = await Detail(s, id);
        Assert.Equal(("rules_only", "roads", RulesOnlyClassifier.Model, ComplaintStatus.New), (c.AiStatus, c.AiCategoryKey, c.AiModel, c.Status));

        var queue = await s.Supervisor.Client.GetFromJsonAsync<List<QueueItemDto>>("/review-queue");
        var item = queue!.Single(q => q.Complaint.Id == id);
        Assert.NotNull(item.SuggestedCategoryId); // pre-filled for the supervisor

        // a later manual re-run, with the service back, replaces the guess
        await Enrich(s, id);
        Assert.Equal("done", (await Detail(s, id)).AiStatus);
    }

    [Fact]
    public async Task A_guess_with_no_match_leaves_the_complaint_marked_skipped_or_failed_but_still_in_the_queue()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var attempts = app.Services.GetRequiredService<UrbanFix.Infrastructure.Jobs.JobOptions>().Attempts;

        var vague = await Complain(s, "Please look into this");
        ai.Down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Enrich(s, vague));
            await Enrich(s, vague, attempts);
        }
        finally { ai.Down = false; }
        Assert.Equal(("failed", null), ((await Detail(s, vague)).AiStatus, (await Detail(s, vague)).AiCategoryKey));

        await SetPolicy(s, OrgPolicy.Default with { AiMonthlyTokenCap = 0 });
        var off = await Complain(s, "Please look into this too");
        await Enrich(s, off);
        Assert.Equal("skipped", (await Detail(s, off)).AiStatus);

        var queue = await s.Supervisor.Client.GetFromJsonAsync<List<QueueItemDto>>("/review-queue");
        Assert.Contains(queue!, q => q.Complaint.Id == vague);
        Assert.Contains(queue!, q => q.Complaint.Id == off);
    }

    [Fact]
    public async Task A_rules_only_guess_never_routes_a_complaint_by_itself_and_never_replaces_a_persons_category()
    {
        var s = await Society.CreateAsync(app);
        var crew = await (await s.Manager.Client.PostAsJsonAsync("/departments", new SetupEndpoints.SaveDepartment("Works", null, null))).Content.ReadFromJsonAsync<SetupEndpoints.DepartmentDto>();
        var team = await (await s.Manager.Client.PostAsJsonAsync("/teams", new SetupEndpoints.CreateTeam(crew!.Id, "Crew"))).Content.ReadFromJsonAsync<SetupEndpoints.TeamDto>();
        (await s.Manager.Client.PostAsJsonAsync("/routing-rules", new SaveRule("all", null, null, team!.Id, null, null))).EnsureSuccessStatusCode();
        (await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new SaveAutoRoute(true, 0.5f))).EnsureSuccessStatusCode();
        await SetPolicy(s, OrgPolicy.Default with { AiMonthlyTokenCap = 0 });

        var guessed = await Complain(s, "No water in the tap");
        await Enrich(s, guessed);
        var g = await Detail(s, guessed);
        Assert.Equal((ComplaintStatus.New, false, "water"), (g.Status, g.AutoRouted, g.AiCategoryKey));
        Assert.Null(g.CategoryId); // a guess is a suggestion, not a decision

        var chosen = await Complain(s, "Big pothole on the road", s.CategoryId); // a person picked "water"
        await Enrich(s, chosen);
        var c = await Detail(s, chosen);
        Assert.Equal(s.CategoryId, c.CategoryId); // untouched by the guess ("roads")
    }

    [Fact]
    public async Task Other_ai_work_is_skipped_quietly_when_the_ai_is_not_available_to_the_organization()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { AiMonthlyTokenCap = 0 });
        var id = await Complain(s, "Garbage pile");
        byte[] note = [0, 0, 0, 0x20, .. "ftypM4A "u8.ToArray(), .. Enumerable.Repeat((byte)9, 300)];
        var up = await s.Resident.Client.PostAsync($"/complaints/{id}/media", new MultipartFormDataContent { { new ByteArrayContent(note), "file", "n.m4a" } });
        var media = (await up.Content.ReadFromJsonAsync<MediaDto>())!;

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VoiceTranscriptionJob>().RunCoreAsync(s.Org, media.Id, 0, default);

        var m = (await s.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!.Media.Single();
        Assert.Equal("skipped", m.TranscriptStatus);
        Assert.False(string.IsNullOrEmpty(m.Url)); // the recording is still there for people
        Assert.Equal(0, (await Report(s)).Calls);
    }
}
