using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class BeforeAfterComparisonTests(TestApp app)
{
    private static byte[] Photo(int seed)
    {
        var rng = new Random(seed);
        using var img = new Image<Rgb24>(320, 240);
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var p in rows.GetRowSpan(y)) p = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        });
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }

    private static Task<HttpResponseMessage> Attach(ApiSession who, Guid jobId, string stage, int seed) =>
        who.Client.PostAsync($"/jobs/{jobId}/media?stage={stage}", new MultipartFormDataContent { { new ByteArrayContent(Photo(seed)), "file", "p.jpg" } });

    private static async Task<JobDto> StartedJob(Society s)
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Rubble on the road", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Clear it", null))).Content.ReadFromJsonAsync<JobDto>())!;
        (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId))).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null)).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/start", new StartWork(null, null, null))).EnsureSuccessStatusCode();
        return job;
    }

    [Fact]
    public async Task The_supervisor_gets_an_advisory_comparison_of_the_latest_before_and_after_photos_and_it_is_kept()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var job = await StartedJob(s);

        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison", null)).StatusCode); // no photos yet
        (await Attach(s.Technician, job.Id, "before", 1)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison", null)).StatusCode); // no after
        Assert.Equal(HttpStatusCode.NotFound, (await s.Supervisor.Client.GetAsync($"/jobs/{job.Id}/comparison")).StatusCode);
        (await Attach(s.Technician, job.Id, "after", 2)).EnsureSuccessStatusCode();

        var res = await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison", null);
        var c = (await res.Content.ReadFromJsonAsync<ComparisonDto>())!;
        Assert.Equal((0.9f, false, false, true), (c.SameLocationLikelihood, c.NeedsMoreInfo, c.Degraded, c.Advisory));
        Assert.Contains("appears", c.ChangeNote);

        var stored = (await s.Supervisor.Client.GetFromJsonAsync<ComparisonDto>($"/jobs/{job.Id}/comparison"))!;
        Assert.Equal(c.Id, stored.Id);

        // Asking again for the same two photos costs nothing; refresh asks the service again.
        var calls = ai.CompareCalls;
        Assert.Equal(c.Id, (await (await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison", null)).Content.ReadFromJsonAsync<ComparisonDto>())!.Id);
        Assert.Equal(calls, ai.CompareCalls);
        var again = (await (await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison?refresh=true", null)).Content.ReadFromJsonAsync<ComparisonDto>())!;
        Assert.NotEqual(c.Id, again.Id);
        Assert.Equal(calls + 1, ai.CompareCalls);

        // A newer "after" photo is a new pair.
        (await Attach(s.Technician, job.Id, "after", 3)).EnsureSuccessStatusCode();
        var newer = (await (await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison", null)).Content.ReadFromJsonAsync<ComparisonDto>())!;
        Assert.NotEqual(c.AfterMediaId, newer.AfterMediaId);
    }

    [Fact]
    public async Task A_doubtful_comparison_is_passed_on_as_needing_a_person_and_a_failed_one_is_a_clean_error()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var job = await StartedJob(s);
        (await Attach(s.Technician, job.Id, "before", 4)).EnsureSuccessStatusCode();
        (await Attach(s.Technician, job.Id, "after", 5)).EnsureSuccessStatusCode();

        var previous = ai.Comparison;
        ai.Comparison = new AiComparison(0.2f, "The photos appear to show different places.", true, 0.7f, false, "m", "v1");
        try
        {
            var c = (await (await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison", null)).Content.ReadFromJsonAsync<ComparisonDto>())!;
            Assert.Equal((0.2f, true), (c.SameLocationLikelihood, c.NeedsMoreInfo));

            ai.Down = true;
            var failed = await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/comparison?refresh=true", null);
            Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        }
        finally { ai.Comparison = previous; ai.Down = false; }
    }

    [Fact]
    public async Task Only_staff_who_sign_off_work_may_use_it()
    {
        var s = await Society.CreateAsync(app);
        var job = await StartedJob(s);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsync($"/jobs/{job.Id}/comparison", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync($"/jobs/{job.Id}/comparison")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Supervisor.Client.PostAsync($"/jobs/{Guid.NewGuid()}/comparison", null)).StatusCode);
    }
}
