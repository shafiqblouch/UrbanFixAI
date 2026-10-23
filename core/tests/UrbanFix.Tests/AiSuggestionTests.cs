using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class AiSuggestionTests(TestApp app)
{
    private static async Task<CreatedDto> Complain(Society s) =>
        (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("pani nahi aa raha", null, null, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;

    [Fact]
    public async Task Every_attempt_is_stored_with_confidence_and_model_version_and_failure_never_loses_the_complaint()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var c = await Complain(s);
        await using var scope = app.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>();
        var attempts = scope.ServiceProvider.GetRequiredService<JobOptions>().Attempts;

        ai.Down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => job.RunCoreAsync(s.Org, c.Id, 0, default));
            await job.RunCoreAsync(s.Org, c.Id, attempts, default); // out of retries
        }
        finally { ai.Down = false; }
        await job.RunCoreAsync(s.Org, c.Id, 0, default); // a later manual re-run succeeds

        var rows = (await s.Supervisor.Client.GetFromJsonAsync<List<SuggestionDto>>($"/complaints/{c.Id}/ai-suggestions"))!;
        Assert.Equal(["failed", "failed", "done"], rows.Select(r => r.Status));
        Assert.Equal([1, attempts + 1, 1], rows.Select(r => r.Attempt));
        Assert.Contains("unreachable", rows[0].Error);
        Assert.Null(rows[0].Model);

        var ok = rows[2];
        Assert.Equal(("understanding", "water", 0.9f, "fake-model", "v1", "en"), (ok.Task, ok.CategoryKey, ok.Confidence, ok.Model, ok.PromptVersion, ok.Language));
        Assert.False(ok.NeedsMoreInfo);

        // the complaint itself was kept all along
        Assert.Equal("done", (await s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{c.Id}"))!.Complaint.AiStatus);
    }

    [Fact]
    public async Task Suggestions_are_visible_to_staff_only_and_a_re_run_after_success_adds_nothing()
    {
        var s = await Society.CreateAsync(app);
        var c = await Complain(s);
        await using var scope = app.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>();
        await job.RunCoreAsync(s.Org, c.Id, 0, default);
        await job.RunCoreAsync(s.Org, c.Id, 0, default);

        Assert.Single((await s.Office.Client.GetFromJsonAsync<List<SuggestionDto>>($"/complaints/{c.Id}/ai-suggestions"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync($"/complaints/{c.Id}/ai-suggestions")).StatusCode);
    }
}
