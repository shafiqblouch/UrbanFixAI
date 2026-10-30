using System.Net;
using UrbanFix.Complaints;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.MapEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class MapTests(TestApp app)
{
    private static CreateComplaint At(Society s, string text, double? lat, double? lon) =>
        new(text, "app", s.CategoryId, s.HouseId, null, null, null, Latitude: lat, Longitude: lon);

    [Fact]
    public async Task A_position_sent_with_a_complaint_is_stored_and_returned()
    {
        var s = await Society.CreateAsync(app);
        var res = await s.Resident.Client.PostAsJsonAsync("/complaints", At(s, "Water is leaking from the pipe", 31.5204, 74.3587));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<CreatedDto>())!;
        var detail = (await s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{created.Id}"))!;
        Assert.Equal((31.5204, 74.3587), (detail.Complaint.Latitude, detail.Complaint.Longitude));
    }

    [Theory]
    [InlineData(91.0, 10.0)]
    [InlineData(10.0, 181.0)]
    [InlineData(10.0, null)]
    [InlineData(null, 10.0)]
    public async Task Impossible_or_half_a_position_is_refused(double? lat, double? lon)
    {
        var s = await Society.CreateAsync(app);
        var res = await s.Resident.Client.PostAsJsonAsync("/complaints", At(s, "Water is leaking from the pipe", lat, lon));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_map_lists_pinned_complaints_counts_the_rest_and_follows_the_filters()
    {
        var s = await Society.CreateAsync(app);
        await s.Resident.Client.PostAsJsonAsync("/complaints", At(s, "Pinned complaint about a leak", 31.52, 74.35));
        await s.Resident.Client.PostAsJsonAsync("/complaints", At(s, "Unpinned complaint about a leak", null, null));

        var map = (await s.Manager.Client.GetFromJsonAsync<MapDto>("/map"))!;
        var pin = Assert.Single(map.Complaints);
        Assert.Equal((31.52, 74.35), (pin.Latitude, pin.Longitude));
        Assert.Equal(1, map.ComplaintsWithoutPosition);

        var none = (await s.Manager.Client.GetFromJsonAsync<MapDto>("/map?status=Closed"))!;
        Assert.Empty(none.Complaints);
    }

    [Fact]
    public async Task A_resident_cannot_open_the_map_and_a_technician_sees_only_their_own_jobs()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/map")).StatusCode);

        var made = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", At(s, "Pole light is out near my gate", 31.5, 74.3))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{made.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{made.Id}/jobs", new CreateJob("Fix it", null))).Content.ReadFromJsonAsync<JobDto>())!;
        (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId))).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null)).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/start", new StartWork(null, 31.5101, 74.3202))).EnsureSuccessStatusCode();

        var seen = (await s.Supervisor.Client.GetFromJsonAsync<MapDto>("/map"))!;
        var tech = Assert.Single(seen.Technicians);
        Assert.Equal((job.Id, 31.5101, 74.3202), (tech.JobId, tech.Latitude, tech.Longitude));

        // A later note with a newer position replaces the earlier one on the map.
        (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/updates", new AddUpdate("moved", 31.5111, 74.3212, null))).EnsureSuccessStatusCode();
        Assert.Equal(31.5111, Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<MapDto>("/map"))!.Technicians).Latitude);

        // Finishing the work with a position puts the technician at the place it was finished.
        (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/complete", new CompleteJob("done", 31.5121, 74.3222))).EnsureSuccessStatusCode();
        Assert.Equal(31.5121, Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<MapDto>("/map"))!.Technicians).Latitude);

        var other = await app.AddMemberAsync(s.Manager, s.Org, UrbanFix.SharedKernel.Role.Technician);
        Assert.Empty((await other.Client.GetFromJsonAsync<MapDto>("/map"))!.Technicians);
    }
}
