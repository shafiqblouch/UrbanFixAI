using System.Net;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.PricingEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

public class JobRulesTests
{
    [Fact]
    public void Job_state_machine_allows_only_the_documented_moves()
    {
        var states = new[] { "Open", "Accepted", "InProgress", "PendingVerification", "Completed", "Returned", "Cancelled" };
        var allowed = states.SelectMany(f => states.Select(t => (f, t))).Where(p => JobStateMachine.CanMove(p.f, p.t)).ToHashSet();
        Assert.Equal(JobStateMachine.All.ToHashSet(), allowed);
        Assert.True(JobStateMachine.CanMove("PendingVerification", "InProgress")); // supervisor returns the work
        Assert.False(JobStateMachine.CanMove("Open", "InProgress")); // must accept first
        Assert.False(JobStateMachine.CanMove("Completed", "InProgress"));
        Assert.All(new[] { "Completed", "Returned", "Cancelled" }, s => Assert.False(JobStatus.IsActive(s)));
    }

    [Theory]
    [InlineData("none", false, false, 0, null, false, false, "")]
    [InlineData("photo", false, false, 0, null, false, false, "before_photo,after_photo")]
    [InlineData("photo", true, false, 0, null, false, false, "after_photo")]
    [InlineData("photo", true, true, 0, null, false, false, "")]
    [InlineData("checklist", false, false, 1, null, false, false, "checklist_incomplete")]
    [InlineData("checklist", false, false, 0, null, false, false, "")]
    [InlineData("note", false, false, 0, "  ", false, false, "completion_note")]
    [InlineData("note", false, false, 0, "fixed", false, false, "")]
    [InlineData("none", false, false, 0, null, true, false, "actual_costs")]
    [InlineData("none", false, false, 0, null, true, true, "")]
    public void Evidence_rules_report_exactly_what_is_missing(string rule, bool before, bool after, int openItems, string? note, bool costsRequired, bool hasCosts, string expected)
    {
        var checklist = Enumerable.Range(0, openItems).Select(i => new ChecklistItem($"step {i}", false)).ToList();
        var missing = JobEvidence.Missing(rule, before, after, checklist, note, costsRequired, hasCosts);
        Assert.Equal(expected, string.Join(",", missing));
    }

    [Fact]
    public void Line_amounts_round_to_two_places_away_from_zero()
    {
        Assert.Equal(10.01m, Pricing.Amount(1.5m, 6.675m)); // 10.0125
        Assert.Equal(0.01m, Pricing.Amount(0.5m, 0.01m)); // 0.005 rounds up
        Assert.Equal(1250.00m, Pricing.Amount(2.5m, 500m));
    }
}

[Collection("db")]
public class JobWorkflowTests(TestApp app)
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

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<JobDto> NewJob(Society s, bool toReview = true, Guid? team = null)
    {
        var res = await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Leak at gate", null, s.CategoryId, s.HouseId, null, null, null));
        var c = (await res.Content.ReadFromJsonAsync<CreatedDto>())!;
        if (toReview) await s.Office.Client.PostAsJsonAsync($"/complaints/{c.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        return await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob("Fix it", team)));
    }

    private static Task<HttpResponseMessage> Post(ApiSession who, string url, object? body = null) =>
        body is null ? who.Client.PostAsync(url, null) : who.Client.PostAsJsonAsync(url, body);

    private static Task<JobDetailDto> Detail(ApiSession who, Guid id) => Ok<JobDetailDto>(who.Client.GetAsync($"/jobs/{id}"));

    private async Task<(ApiSession Session, Guid UserId)> Another(Society s, Role role = Role.Technician)
    {
        var session = await app.AddMemberAsync(s.Manager, s.Org, role);
        return (session, (await session.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId);
    }

    // JB-02 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Technician_sees_only_their_own_jobs_while_supervisors_see_all()
    {
        var s = await Society.CreateAsync(app);
        var other = await Another(s);
        var mine = await NewJob(s);
        var theirs = await NewJob(s);
        await Post(s.Supervisor, $"/jobs/{mine.Id}/assign", new AssignJob(null, s.TechnicianId));
        await Post(s.Supervisor, $"/jobs/{theirs.Id}/assign", new AssignJob(null, other.UserId));

        Assert.Equal([mine.Id], (await Ok<List<JobListItemDto>>(s.Technician.Client.GetAsync("/jobs"))).Select(j => j.Job.Id));
        Assert.Equal(2, (await Ok<List<JobListItemDto>>(s.Supervisor.Client.GetAsync("/jobs"))).Count);
        Assert.Equal([mine.Id], (await Ok<List<JobListItemDto>>(s.Technician.Client.GetAsync("/jobs?mine=true&status=Open"))).Select(j => j.Job.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Technician.Client.GetAsync($"/jobs/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(s.Technician, $"/jobs/{theirs.Id}/accept")).StatusCode);
        // the same filter applies on the per-complaint listing
        Assert.Empty(await Ok<List<JobDto>>(s.Technician.Client.GetAsync($"/complaints/{theirs.ComplaintId}/jobs")));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/jobs")).StatusCode);
    }

    [Fact]
    public async Task Assignment_checks_role_and_team_and_reassignment_asks_the_new_technician_to_accept_again()
    {
        var s = await Society.CreateAsync(app);
        var dept = await Ok<DepartmentDto>(s.Manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Water", null, null)));
        var crew = await Ok<TeamDto>(s.Manager.Client.PostAsJsonAsync("/teams", new CreateTeam(dept.Id, "Crew A")));
        var other = await Another(s);
        await s.Manager.Client.PostAsJsonAsync($"/teams/{crew.Id}/members", new AddTeamMember(s.TechnicianId, false));
        var job = await NewJob(s, team: crew.Id);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, s.ResidentId))).StatusCode); // not a technician
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, other.UserId))).StatusCode); // not in Crew A
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(s.Technician, $"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId))).StatusCode);

        Assert.Equal(s.TechnicianId, (await Ok<JobDto>(Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId)))).AssignedUserId);
        Assert.Equal("Accepted", (await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/accept"))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Technician, $"/jobs/{job.Id}/accept")).StatusCode);

        await s.Manager.Client.PostAsJsonAsync($"/teams/{crew.Id}/members", new AddTeamMember(other.UserId, false));
        var moved = await Ok<JobDto>(Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, other.UserId)));
        Assert.Equal(("Open", other.UserId), (moved.Status, moved.AssignedUserId));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(s.Technician, $"/jobs/{job.Id}/start")).StatusCode); // no longer theirs
        Assert.Equal(HttpStatusCode.Conflict, (await Post(other.Session, $"/jobs/{job.Id}/start")).StatusCode); // must accept first
    }

    // JB-03 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Progress_updates_build_a_timeline_with_authors_and_move_the_complaint_to_in_progress()
    {
        var s = await Society.CreateAsync(app);
        var job = await NewJob(s);
        await Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId));
        await Post(s.Technician, $"/jobs/{job.Id}/accept");

        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate("early", null, null, null))).StatusCode);
        await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/start", new StartWork("On site", 24.86, 67.01)));
        Assert.Equal(ComplaintStatus.InProgress, (await Ok<ComplaintDetailDto>(s.Manager.Client.GetAsync($"/complaints/{job.ComplaintId}"))).Complaint.Status);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate(null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate("x", 95, 10, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate("x", 10, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate(null, null, null, [9]))).StatusCode);
        await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate("Valve isolated", 24.861, 67.011, [0])));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(s.Supervisor, $"/jobs/{job.Id}/updates", new AddUpdate("hi", null, null, null))).StatusCode);

        var d = await Detail(s.Supervisor, job.Id);
        Assert.Equal(["created", "assigned", "status", "status", "note"], d.Timeline.Select(t => t.Kind));
        Assert.Equal(s.TechnicianId, d.Timeline[^1].AuthorId);
        Assert.Equal(24.861, d.Timeline[^1].Latitude);
        Assert.Equal(["Accepted", "InProgress"], d.Timeline.Where(t => t.Kind == "status").Select(t => t.ToStatus));
        Assert.True(d.Checklist[0].Done);
        Assert.False(d.Checklist[1].Done);
    }

    // JB-05 / JB-06 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Photo_rule_blocks_completion_until_before_and_after_photos_exist_then_supervisor_signs_off()
    {
        var s = await Society.CreateAsync(app);
        await s.SetEvidenceAsync("photo");
        var job = await NewJob(s);
        Assert.Equal("photo", job.EvidenceRule);
        await s.StartWorkAsync(job);

        var blocked = await Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("done"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Equal(["before_photo", "after_photo"], JsonDocument.Parse(await blocked.Content.ReadAsStringAsync()).RootElement.GetProperty("missing").EnumerateArray().Select(e => e.GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await Attach(s.Technician, job.Id, "sideways", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Attach(s.Technician, job.Id, "before", 1)).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("done"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Attach(s.Technician, job.Id, "after", 2)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Attach(s.Resident, job.Id, "after", 3)).StatusCode);

        Assert.Equal("PendingVerification", (await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("Replaced washer")))).Status);
        var detail = await Detail(s.Supervisor, job.Id);
        Assert.Equal(["before", "after"], detail.Evidence.Select(e => e.Stage));
        Assert.Equal("Replaced washer", detail.CompletionNote);
        // still In Progress / Assigned for the complaint until sign-off
        Assert.Equal(ComplaintStatus.InProgress, (await Ok<ComplaintDetailDto>(s.Manager.Client.GetAsync($"/complaints/{job.ComplaintId}"))).Complaint.Status);

        Assert.Equal(HttpStatusCode.Forbidden, (await Post(s.Technician, $"/jobs/{job.Id}/sign-off")).StatusCode);
        Assert.Equal("Completed", (await Ok<JobDto>(Post(s.Supervisor, $"/jobs/{job.Id}/sign-off"))).Status);
        Assert.Equal(ComplaintStatus.Resolved, (await Ok<ComplaintDetailDto>(s.Manager.Client.GetAsync($"/complaints/{job.ComplaintId}"))).Complaint.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Supervisor, $"/jobs/{job.Id}/sign-off")).StatusCode);
    }

    [Fact]
    public async Task Supervisor_can_return_work_with_a_reason_and_the_technician_carries_on()
    {
        var s = await Society.CreateAsync(app);
        var job = await NewJob(s);
        await s.StartWorkAsync(job);
        await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("done")));

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Supervisor, $"/jobs/{job.Id}/return", new ReasonRequest(" "))).StatusCode);
        Assert.Equal("InProgress", (await Ok<JobDto>(Post(s.Supervisor, $"/jobs/{job.Id}/return", new ReasonRequest("Photo is of the wrong tap")))).Status);

        var timeline = (await Detail(s.Supervisor, job.Id)).Timeline;
        Assert.Equal("Photo is of the wrong tap", timeline[^1].Note);
        Assert.Equal(("PendingVerification", "InProgress"), (timeline[^1].FromStatus!, timeline[^1].ToStatus!));
        Assert.Equal("PendingVerification", (await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("redone")))).Status);
    }

    [Fact]
    public async Task Checklist_and_note_rules_are_enforced()
    {
        var s = await Society.CreateAsync(app);
        await s.SetEvidenceAsync("checklist", checklist: ["Isolate", "Repair"]);
        var job = await NewJob(s);
        await s.StartWorkAsync(job);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("x"))).StatusCode);
        await Post(s.Technician, $"/jobs/{job.Id}/updates", new AddUpdate(null, null, null, [0, 1]));
        Assert.Equal(HttpStatusCode.OK, (await Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob(null))).StatusCode);

        await s.SetEvidenceAsync("note");
        var noted = await NewJob(s);
        await s.StartWorkAsync(noted);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await Post(s.Technician, $"/jobs/{noted.Id}/complete", new CompleteJob(" "))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(s.Technician, $"/jobs/{noted.Id}/complete", new CompleteJob("Cleared the line"))).StatusCode);
    }

    [Fact]
    public async Task Rules_are_snapshotted_on_the_job_so_later_configuration_changes_do_not_move_the_goalposts()
    {
        var s = await Society.CreateAsync(app);
        await s.SetEvidenceAsync("photo");
        var job = await NewJob(s);
        await s.SetEvidenceAsync("none");
        Assert.Equal("photo", (await Detail(s.Supervisor, job.Id)).Job.EvidenceRule);
        Assert.Equal("none", (await NewJob(s)).EvidenceRule);
    }

    // JB-04 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Cannot_complete_returns_the_complaint_to_review_with_the_reason_and_cancels_nothing_else()
    {
        var s = await Society.CreateAsync(app);
        var job = await NewJob(s);
        await s.StartWorkAsync(job);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/cannot-complete", new ReasonRequest(null))).StatusCode);
        Assert.Equal("Returned", (await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/cannot-complete", new ReasonRequest("Needs excavation")))).Status);

        var complaint = (await Ok<ComplaintDetailDto>(s.Supervisor.Client.GetAsync($"/complaints/{job.ComplaintId}")));
        Assert.Equal(ComplaintStatus.UnderReview, complaint.Complaint.Status);
        Assert.Equal("Needs excavation", complaint.Complaint.StatusReason);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Technician, $"/jobs/{job.Id}/cannot-complete", new ReasonRequest("again"))).StatusCode);
        // a rework job can now be raised
        Assert.Equal(2, (await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{job.ComplaintId}/jobs", new CreateJob("Excavate", null)))).SequenceNo);
    }

    [Fact]
    public async Task Moving_a_complaint_through_work_states_by_hand_is_refused_while_a_job_is_active()
    {
        var s = await Society.CreateAsync(app);
        var job = await NewJob(s);
        var move = (ComplaintStatus to) => s.Technician.Client.PostAsJsonAsync($"/complaints/{job.ComplaintId}/transition", new Transition(to, "x", null));
        Assert.Equal(HttpStatusCode.Conflict, (await move(ComplaintStatus.InProgress)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await move(ComplaintStatus.Resolved)).StatusCode);
        // Sending it back to review is still allowed and cancels the job.
        var back = await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{job.ComplaintId}/transition", new Transition(ComplaintStatus.UnderReview, "Wrong category", null));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal("Cancelled", (await Detail(s.Supervisor, job.Id)).Job.Status);
    }

    // JC-02 / JC-04 ---------------------------------------------------------------------------------------------------

    private async Task<PriceItemDto> PriceItem(Society s, string name, string unit, decimal rate) =>
        await Ok<PriceItemDto>(s.Manager.Client.PostAsJsonAsync("/price-items", new CreatePriceItem(name, null, unit, rate, DateTimeOffset.UtcNow.AddDays(-1))));

    [Fact]
    public async Task Estimates_are_versioned_priced_from_the_list_and_locked_once_work_starts()
    {
        var s = await Society.CreateAsync(app);
        var pipe = await PriceItem(s, "PVC pipe 1in", "metre", 320.50m);
        var job = await NewJob(s);
        await Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId));

        // validation
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/estimates", new SaveEstimate(null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/estimates",
            new SaveEstimate(null, [new("material", null, "Mystery part", 1, null, 10)]))).StatusCode); // materials need a price item
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/estimates",
            new SaveEstimate(null, [new("labour", null, "Plumber", 0, "hour", 400)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(s.Technician, $"/jobs/{job.Id}/estimates",
            new SaveEstimate(null, [new("material", Guid.NewGuid(), null, 1, null, null)]))).StatusCode);

        var v1 = await Ok<EstimateDto>(Post(s.Technician, $"/jobs/{job.Id}/estimates", new SaveEstimate("First look",
        [
            new("material", pipe.Id, null, 2.5m, null, 1m), // client rate is ignored: the list rate applies
            new("labour", null, "Plumber", 3, null, 400m),
            new("transport", null, "Van", 1, null, 150m),
        ])));
        Assert.Equal((1, "Current"), (v1.Version, v1.Status));
        Assert.Equal(801.25m + 1200m + 150m, v1.Total);
        Assert.Equal([801.25m, 1200m, 150m], v1.Lines.Select(l => l.Amount));
        Assert.Equal(320.50m, v1.Lines[0].UnitRate);
        Assert.Equal("hour", v1.Lines[1].Unit);

        // a later rate change does not touch the stored estimate
        await s.Manager.Client.PostAsJsonAsync($"/price-items/{pipe.Id}/rates", new AddRate(999m, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var v2 = await Ok<EstimateDto>(Post(s.Supervisor, $"/jobs/{job.Id}/estimates", new SaveEstimate("Revised", [new("material", pipe.Id, null, 1, null, null)])));
        Assert.Equal((2, 999m), (v2.Version, v2.Total));

        var all = await Ok<List<EstimateDto>>(s.Technician.Client.GetAsync($"/jobs/{job.Id}/estimates"));
        Assert.Equal([("Current", 2), ("Superseded", 1)], all.Select(e => (e.Status, e.Version)));
        Assert.Equal(2151.25m, all[1].Total);
        Assert.Equal(2, (await Detail(s.Technician, job.Id)).Estimate!.Version);

        // another technician cannot estimate someone else's job; nobody can once work has started
        var other = await Another(s);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(other.Session, $"/jobs/{job.Id}/estimates", new SaveEstimate(null, [new("transport", null, "x", 1, null, 1)]))).StatusCode);
        await Post(s.Technician, $"/jobs/{job.Id}/accept");
        await Post(s.Technician, $"/jobs/{job.Id}/start");
        Assert.Equal(HttpStatusCode.Conflict, (await Post(s.Supervisor, $"/jobs/{job.Id}/estimates", new SaveEstimate(null, [new("transport", null, "x", 1, null, 1)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(s.Resident, $"/jobs/{job.Id}/estimates", new SaveEstimate(null, []))).StatusCode);
    }

    [Fact]
    public async Task Actual_costs_are_required_when_the_category_says_so_and_show_variance_against_the_estimate()
    {
        var s = await Society.CreateAsync(app);
        await s.SetEvidenceAsync("none", requiresCosts: true);
        var cable = await PriceItem(s, "Cable 2.5mm", "metre", 100m);
        var job = await NewJob(s);
        Assert.True(job.RequiresCosts);
        await Post(s.Supervisor, $"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId));
        await Ok<EstimateDto>(Post(s.Technician, $"/jobs/{job.Id}/estimates", new SaveEstimate(null, [new("material", cable.Id, null, 10, null, null)]))); // 1000
        await Post(s.Technician, $"/jobs/{job.Id}/accept");

        Assert.Equal(HttpStatusCode.Conflict, (await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new("transport", null, "Van", 1, null, 100)]))).StatusCode); // not started
        await Post(s.Technician, $"/jobs/{job.Id}/start");

        var blocked = await Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("done"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, blocked.StatusCode);
        Assert.Contains("actual_costs", await blocked.Content.ReadAsStringAsync());

        var saved = await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts(
            [new("material", cable.Id, null, 12, null, null), new("labour", null, "Electrician", 2, null, 250)]));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        // saving again replaces the lines rather than adding to them
        await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts(
            [new("material", cable.Id, null, 12, null, null), new("labour", null, "Electrician", 2, null, 250), new("transport", null, "Rickshaw", 1, null, 80)]));

        await Ok<JobDto>(Post(s.Technician, $"/jobs/{job.Id}/complete", new CompleteJob("done")));
        var d = await Detail(s.Supervisor, job.Id);
        Assert.Equal(3, d.Actuals.Count);
        Assert.Equal((1000m, 1780m, 780m, 78.0m), (d.Variance.Estimated, d.Variance.Actual, d.Variance.Difference, d.Variance.Percent));

        // technician is locked out after submitting; the supervisor can still correct the lines
        Assert.Equal(HttpStatusCode.Conflict, (await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new("transport", null, "x", 1, null, 1)]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Supervisor.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new("transport", null, "Fuel", 1, null, 500)]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Manager.Client.PutAsJsonAsync($"/jobs/{Guid.NewGuid()}/costs", new SaveCosts([]))).StatusCode);
    }

    [Fact]
    public async Task Jobs_without_an_estimate_report_no_variance_and_other_organizations_cannot_see_the_job()
    {
        var s = await Society.CreateAsync(app);
        var other = await Society.CreateAsync(app);
        var job = await NewJob(s);
        var d = await Detail(s.Supervisor, job.Id);
        Assert.Null(d.Estimate);
        Assert.Equal((null, 0m, null), (d.Variance.Estimated, d.Variance.Actual, d.Variance.Difference));
        Assert.Equal(HttpStatusCode.NotFound, (await other.Supervisor.Client.GetAsync($"/jobs/{job.Id}")).StatusCode);
    }
}
