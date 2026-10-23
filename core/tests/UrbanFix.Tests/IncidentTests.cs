using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.IncidentEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;

namespace UrbanFix.Tests;

public class DuplicateScoringTests
{
    private static readonly DuplicateScoring.Settings S = new();

    [Fact]
    public void The_same_words_next_door_an_hour_later_in_the_same_category_score_very_high()
    {
        var r = DuplicateScoring.Score(0.95, 20, 1, true, S);
        Assert.True(r.Score > 0.9, r.Reason);
        Assert.Contains("same category", r.Reason);
    }

    [Fact]
    public void Different_words_far_apart_and_days_later_score_low()
    {
        Assert.True(DuplicateScoring.Score(0.2, 290, 160, false, S).Score < 0.4);
    }

    [Fact]
    public void Without_words_the_score_never_passes_what_a_text_match_would_give_and_is_capped()
    {
        var r = DuplicateScoring.Score(null, 0, 0, true, S);
        Assert.Equal(0.85, r.Score);
        Assert.Contains("no wording", r.Reason);
    }

    [Theory]
    [InlineData(1.0, 100.0, false, true)]
    [InlineData(1.0, 400.0, false, false)]    // outside the radius
    [InlineData(1.0, 400.0, true, true)]      // same place always counts
    [InlineData(192.0, 10.0, false, false)] // outside the window
    [InlineData(1.0, null, false, false)]   // place unknown and not the same
    public void Only_recent_nearby_complaints_are_candidates(double hours, double? metres, bool samePlace, bool expected) =>
        Assert.Equal(expected, DuplicateScoring.IsCandidate(hours, metres, samePlace, S));

    [Fact]
    public void A_pair_has_one_order_whichever_way_round_it_is_given()
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid();
        Assert.Equal(DuplicateScoring.Pair(x, y), DuplicateScoring.Pair(y, x));
    }

    // DP-06: a labelled set of pairs (the kinds seen in a society) and the threshold chosen from it.
    private static readonly DuplicateScoring.Labelled[] Labelled =
    [
        new(DuplicateScoring.Score(0.92, 10, 2, true, S).Score, true),
        new(DuplicateScoring.Score(0.88, 60, 5, true, S).Score, true),
        new(DuplicateScoring.Score(0.80, 120, 20, true, S).Score, true),
        new(DuplicateScoring.Score(0.75, 200, 48, true, S).Score, true),
        new(DuplicateScoring.Score(0.70, 40, 3, true, S).Score, true),
        new(DuplicateScoring.Score(0.55, 30, 4, true, S).Score, true),   // worded differently, still the same leak
        new(DuplicateScoring.Score(0.40, 20, 6, true, S).Score, false),  // same block, a different water issue
        new(DuplicateScoring.Score(0.35, 250, 70, true, S).Score, false),
        new(DuplicateScoring.Score(0.30, 50, 10, false, S).Score, false),
        new(DuplicateScoring.Score(0.20, 280, 150, false, S).Score, false),
        new(DuplicateScoring.Score(0.60, 15, 1, false, S).Score, false), // alike wording, other category
        new(DuplicateScoring.Score(0.15, 5, 2, true, S).Score, false),
    ];

    [Fact]
    public void The_default_threshold_keeps_precision_at_ninety_percent_on_the_labelled_set()
    {
        var q = DuplicateScoring.Evaluate(Labelled, DuplicateScoring.DefaultThreshold);
        Assert.True(q.Precision >= 0.9, $"precision {q.Precision}, recall {q.Recall}");
        Assert.True(q.Recall >= 0.6, $"recall {q.Recall}");
    }

    [Fact]
    public void Tuning_returns_the_lowest_threshold_that_meets_the_precision_target()
    {
        var best = DuplicateScoring.Tune(Labelled, 0.9)!;
        Assert.True(best.Precision >= 0.9);
        Assert.True(best.Recall >= 0.6, $"recall {best.Recall} at {best.Threshold}");
        Assert.Null(DuplicateScoring.Tune([new(0.9, false), new(0.8, false)], 0.9));
    }
}

[Collection("db")]
public class IncidentFlowTests(TestApp app)
{
    private static async Task<CreatedDto> Report(ApiSession who, Society s, string text, Guid? reportedBy = null) =>
        (await (await who.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, s.CategoryId, s.HouseId, s.LocationId, null, reportedBy))).Content.ReadFromJsonAsync<CreatedDto>())!;

    private static async Task<List<SuggestionDto>> Check(Society s, Guid complaint) =>
        (await (await s.Supervisor.Client.PostAsync($"/complaints/{complaint}/duplicate-check", null)).Content.ReadFromJsonAsync<List<SuggestionDto>>())!;

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    [Fact]
    public async Task A_complaint_from_the_same_place_and_category_is_suggested_and_a_person_decides()
    {
        var s = await Society.CreateAsync(app);
        var first = await Report(s.Resident, s, "Water leaking in the lane");
        var second = await Report(s.Office, s, "Lane is flooded with water", s.NeighbourId);

        var found = Assert.Single(await Check(s, second.Id));
        Assert.Equal(first.Id, found.ComplaintId);
        Assert.Equal("Pending", found.Status);
        Assert.True(found.Score >= 0.7);

        // Checking again does not make a second suggestion.
        Assert.Single(await Check(s, second.Id));

        var incident = (await (await s.Supervisor.Client.PostAsJsonAsync($"/duplicate-suggestions/{found.Id}/decision", new SuggestionDecision(true, "same leak"))).Content.ReadFromJsonAsync<IncidentDto>())!;
        Assert.Equal(2, incident.Complaints.Count);
        Assert.Equal(first.Id, incident.PrimaryComplaintId);

        // Both complaints still exist and are open on their own.
        var again = (await s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{second.Id}"))!.Complaint;
        Assert.Equal(incident.Id, again.IncidentId);
        Assert.NotEqual(ComplaintStatus.Merged, again.Status);
        Assert.Empty(await s.Supervisor.Client.GetFromJsonAsync<List<SuggestionDto>>($"/complaints/{second.Id}/duplicates") ?? []);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsJsonAsync($"/duplicate-suggestions/{found.Id}/decision", new SuggestionDecision(true, null))).StatusCode);
    }

    [Fact]
    public async Task A_rejected_pair_is_never_suggested_again()
    {
        var s = await Society.CreateAsync(app);
        var first = await Report(s.Resident, s, "Water leaking in the lane");
        var second = await Report(s.Office, s, "Water tank overflowing", s.NeighbourId);

        var found = Assert.Single(await Check(s, first.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Supervisor.Client.PostAsJsonAsync($"/duplicate-suggestions/{found.Id}/decision", new SuggestionDecision(false, "different"))).StatusCode);

        Assert.Empty(await Check(s, first.Id));
        Assert.Empty(await Check(s, second.Id));
        Assert.Empty(await s.Supervisor.Client.GetFromJsonAsync<List<SuggestionDto>>($"/complaints/{first.Id}/duplicates?status=Pending") ?? []);
        Assert.Single(await s.Supervisor.Client.GetFromJsonAsync<List<SuggestionDto>>($"/complaints/{first.Id}/duplicates?status=Rejected") ?? []);
    }

    [Fact]
    public async Task A_complaint_in_another_category_and_place_is_not_suggested()
    {
        var s = await Society.CreateAsync(app);
        var first = await Report(s.Resident, s, "Water leaking in the lane");
        var other = (await (await s.Office.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Street light out", null, null, null, null, "Far away park", s.NeighbourId))).Content.ReadFromJsonAsync<CreatedDto>())!;
        Assert.Empty(await Check(s, other.Id));
        Assert.Empty(await Check(s, first.Id));
    }

    [Fact]
    public async Task A_supervisor_can_group_and_ungroup_by_hand_and_a_pair_left_alone_dissolves()
    {
        var s = await Society.CreateAsync(app);
        var a = await Report(s.Resident, s, "Leak one");
        var b = await Report(s.Office, s, "Leak two", s.NeighbourId);
        var c = await Report(s.Office, s, "Leak three", s.NeighbourId);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync("/incidents", new CreateIncident([a.Id], null))).StatusCode);
        var made = await s.Supervisor.Client.PostAsJsonAsync("/incidents", new CreateIncident([a.Id, b.Id], "Lane leak"));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var incident = (await made.Content.ReadFromJsonAsync<IncidentDto>())!;
        Assert.Equal("Lane leak", incident.Title);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsJsonAsync("/incidents", new CreateIncident([a.Id, c.Id], null))).StatusCode);

        Assert.Equal(3, (await (await s.Supervisor.Client.PostAsJsonAsync($"/incidents/{incident.Id}/complaints", new AddToIncident(c.Id))).Content.ReadFromJsonAsync<IncidentDto>())!.Complaints.Count);

        // Taking out the primary hands it to the oldest left.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Supervisor.Client.DeleteAsync($"/incidents/{incident.Id}/complaints/{a.Id}")).StatusCode);
        var left = (await s.Supervisor.Client.GetFromJsonAsync<IncidentDto>($"/incidents/{incident.Id}"))!;
        Assert.Equal(b.Id, left.PrimaryComplaintId);

        // One complaint alone is not an incident.
        await s.Supervisor.Client.DeleteAsync($"/incidents/{incident.Id}/complaints/{c.Id}");
        Assert.Equal("closed", (await s.Supervisor.Client.GetFromJsonAsync<IncidentDto>($"/incidents/{incident.Id}"))!.Status);
        Assert.Null((await s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{b.Id}"))!.Complaint.IncidentId);
    }

    [Fact]
    public async Task Residents_cannot_see_or_decide_on_duplicates()
    {
        var s = await Society.CreateAsync(app);
        var a = await Report(s.Resident, s, "Leak");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync($"/complaints/{a.Id}/duplicates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsJsonAsync("/incidents", new CreateIncident([a.Id, a.Id], null))).StatusCode);
    }

    [Fact]
    public async Task When_the_work_is_done_everyone_who_reported_the_same_problem_is_told()
    {
        var s = await Society.CreateAsync(app);
        var first = await Report(s.Resident, s, "Water leaking in the lane");
        var second = await Report(s.Office, s, "Lane is flooded with water", s.NeighbourId);
        (await s.Supervisor.Client.PostAsJsonAsync("/incidents", new CreateIncident([first.Id, second.Id], null))).EnsureSuccessStatusCode();

        await s.Office.Client.PostAsJsonAsync($"/complaints/{first.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{first.Id}/jobs", new CreateJob("Fix", null))).Content.ReadFromJsonAsync<JobDto>())!;
        await s.FinishWorkAsync(job);
        await Drain();

        var own = (await s.Resident.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications"))!.Items;
        Assert.Contains(own, n => n.Template == "confirmation_request");
        var sibling = (await s.Neighbour.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications"))!.Items;
        var told = Assert.Single(sibling, n => n.Template == "incident_completed");
        Assert.Contains(second.Reference, told.Body);
        Assert.DoesNotContain(first.Reference, told.Body);
    }
}

[Collection("db")]
public class AutoRouteGateTests(TestApp app)
{
    /// <summary>The gate is off in the shared test host; here it is a real one (90% over at least 4 suggestions).</summary>
    private HttpClient Strict(Society s)
    {
        var host = app.WithWebHostBuilder(b => { b.UseSetting("AutoRoute:MinAccuracy", "0.9"); b.UseSetting("AutoRoute:MinSamples", "4"); });
        var client = host.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UrbanFix-Tests/1.0");
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", s.Manager.Token);
        return client;
    }

    private async Task SeedFeedback(Society s, int accepted, int overridden)
    {
        await using var conn = new Npgsql.NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        for (var i = 0; i < accepted + overridden; i++)
        {
            var good = i < accepted;
            var made = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint($"Seed {i}", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
            await using var cmd = new Npgsql.NpgsqlCommand(
                "INSERT INTO ai_feedback (id, organization_id, complaint_id, field, suggested, final_value, accepted, created_at) VALUES (gen_random_uuid(), @o, @c, 'category', 'water', @f, @a, now())", conn);
            cmd.Parameters.AddWithValue("o", s.Org); cmd.Parameters.AddWithValue("c", made.Id); cmd.Parameters.AddWithValue("f", good ? "water" : "electricity"); cmd.Parameters.AddWithValue("a", good);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task A_category_cannot_be_switched_to_automatic_until_the_ai_has_been_right_often_enough()
    {
        var s = await Society.CreateAsync(app);
        var client = Strict(s);

        var readiness = (await client.GetFromJsonAsync<List<RoutingEndpoints.ReadinessDto>>("/reports/auto-route-readiness"))!.Single(r => r.CategoryId == s.CategoryId);
        Assert.False(readiness.Ready);
        Assert.Equal(0, readiness.Samples);
        Assert.Null(readiness.Accuracy);

        var refused = await client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new RoutingEndpoints.SaveAutoRoute(true, 0.9f));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("accuracy_below_gate", await refused.Content.ReadAsStringAsync());

        // Turning it off is never refused.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new RoutingEndpoints.SaveAutoRoute(false, null))).StatusCode);
    }

    [Fact]
    public async Task Enough_accepted_suggestions_open_the_gate_and_too_many_overrides_close_it()
    {
        var s = await Society.CreateAsync(app);
        var client = Strict(s);
        await SeedFeedback(s, 9, 1);

        var ready = (await client.GetFromJsonAsync<List<RoutingEndpoints.ReadinessDto>>("/reports/auto-route-readiness"))!.Single(r => r.CategoryId == s.CategoryId);
        Assert.True(ready.Ready);
        Assert.Equal(0.9, ready.Accuracy);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new RoutingEndpoints.SaveAutoRoute(true, 0.9f))).StatusCode);

        var s2 = await Society.CreateAsync(app);
        var client2 = Strict(s2);
        await SeedFeedback(s2, 6, 4);
        Assert.Equal(HttpStatusCode.Conflict, (await client2.PutAsJsonAsync($"/categories/{s2.CategoryId}/auto-route", new RoutingEndpoints.SaveAutoRoute(true, 0.9f))).StatusCode);
    }
}
