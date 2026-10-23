using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ApprovalEndpoints;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.DashboardEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;

namespace UrbanFix.Tests;

public class ApprovalRulesTests
{
    private static Approval Pending(int needed = 1, Guid? by = null, DateTimeOffset? expires = null) =>
        new() { RequestedBy = by ?? Guid.NewGuid(), ApprovalsNeeded = needed, ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddHours(5) };

    [Theory]
    [InlineData(500, 1000, 0, false, 0)]
    [InlineData(1000, 1000, 0, false, 0)]      // exactly at the limit needs no approval
    [InlineData(1000.01, 1000, 0, true, 1)]
    [InlineData(5000, 1000, 5000, true, 1)]
    [InlineData(5000.01, 1000, 5000, true, 2)]
    [InlineData(999999, 0, 0, false, 0)]       // a limit of 0 means the society has not turned approval on
    public void The_limit_decides_whether_and_from_how_many_people(double amount, double limit, double second, bool needed, int approvers) =>
        Assert.Equal((needed, approvers), ApprovalRules.Requirement((decimal)amount, (decimal)limit, (decimal)second));

    [Fact]
    public void Only_managers_and_committee_members_may_answer_and_never_the_person_who_asked()
    {
        var a = Pending();
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.Null(ApprovalRules.CanDecide(a, user, [Role.Manager], [], now));
        Assert.Null(ApprovalRules.CanDecide(a, user, [Role.Committee], [], now));
        Assert.Null(ApprovalRules.CanDecide(a, user, [Role.Supervisor, Role.Manager], [], now));
        Assert.Equal("not_an_approver", ApprovalRules.CanDecide(a, user, [Role.Supervisor], [], now));
        Assert.Equal("not_an_approver", ApprovalRules.CanDecide(a, user, [Role.Technician, Role.Resident], [], now));
        Assert.Equal("requester_cannot_decide", ApprovalRules.CanDecide(a, a.RequestedBy, [Role.Manager], [], now));
    }

    [Fact]
    public void A_finished_or_expired_request_and_a_second_answer_from_the_same_person_are_refused()
    {
        var user = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("expired", ApprovalRules.CanDecide(Pending(expires: now.AddMinutes(-1)), user, [Role.Manager], [], now));
        var done = Pending(); done.Status = ApprovalStatus.Approved;
        Assert.Equal("not_pending", ApprovalRules.CanDecide(done, user, [Role.Manager], [], now));
        Assert.Equal("already_decided", ApprovalRules.CanDecide(Pending(2), user, [Role.Manager], [new ApprovalDecision { UserId = user, Approve = true }], now));
    }

    [Fact]
    public void One_no_ends_it_and_yes_counts_until_enough_different_people_have_said_it()
    {
        var one = Pending(1);
        Assert.Equal(ApprovalStatus.Approved, ApprovalRules.Outcome(one, [new() { Approve = true }]));
        var two = Pending(2);
        Assert.Equal(ApprovalStatus.Pending, ApprovalRules.Outcome(two, [new() { Approve = true }]));
        Assert.Equal(ApprovalStatus.Approved, ApprovalRules.Outcome(two, [new() { Approve = true }, new() { Approve = true }]));
        Assert.Equal(ApprovalStatus.Rejected, ApprovalRules.Outcome(two, [new() { Approve = true }, new() { Approve = false }]));
        Assert.Equal(ApprovalStatus.Rejected, ApprovalRules.Outcome(two, [new() { Approve = false }]));
    }

    [Theory]
    [InlineData("NotRequired", true)]
    [InlineData("Approved", true)]
    [InlineData("Pending", false)]
    [InlineData("Rejected", false)]
    [InlineData("Expired", false)]
    public void Work_starts_only_when_the_estimate_needs_no_approval_or_has_it(string status, bool allowed) => Assert.Equal(allowed, ApprovalRules.AllowsWork(status));

    [Theory]
    [InlineData(1000, 1100, 10.0, false)]
    [InlineData(1000, 1200, 20.0, false)]   // exactly at the threshold is not over it
    [InlineData(1000, 1201, 20.1, true)]
    [InlineData(1000, 800, -20.0, false)]
    public void Overrun_is_the_percentage_over_the_estimate_beyond_the_threshold(double estimate, double actual, double percent, bool over)
    {
        Assert.Equal(percent, ApprovalRules.OverrunPercent((decimal)estimate, (decimal)actual));
        Assert.Equal(over, ApprovalRules.IsOverrun((decimal)estimate, (decimal)actual, 20));
    }

    [Fact]
    public void Without_an_estimate_there_is_nothing_to_overrun()
    {
        Assert.Null(ApprovalRules.OverrunPercent(null, 500));
        Assert.Null(ApprovalRules.OverrunPercent(0, 500));
        Assert.False(ApprovalRules.IsOverrun(null, 500, 20));
    }

    [Fact]
    public void An_estimate_waiting_too_long_escalates_from_the_moment_the_wait_ran_out()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        EscalationRules.Facts Facts(DateTimeOffset? since) => new(ComplaintStatus.Assigned, now.AddDays(-2), now.AddDays(3), now.AddDays(3), JobStatus.Open, now.AddHours(-30), null, null, since, 24);
        Assert.Empty(EscalationRules.Evaluate(Facts(now.AddHours(-23)), now).Where(c => c.Rule == EscalationRuleNames.EstimateWaiting));
        var c = Assert.Single(EscalationRules.Evaluate(Facts(now.AddHours(-30)), now), c => c.Rule == EscalationRuleNames.EstimateWaiting);
        Assert.Equal(now.AddHours(-6), c.Since);
        Assert.DoesNotContain(EscalationRules.Evaluate(Facts(null), now), c => c.Rule == EscalationRuleNames.EstimateWaiting);
    }

    [Theory]
    [InlineData(1000, 0, 72, 24, 20, null)]
    [InlineData(1000, 5000, 72, 24, 20, null)]
    [InlineData(-1, 0, 72, 24, 20, "amount_cannot_be_negative")]
    [InlineData(1000, 1000, 72, 24, 20, "second_approver_limit_must_exceed_first")]
    [InlineData(0, 5000, 72, 24, 20, "second_approver_limit_must_exceed_first")]
    [InlineData(1000, 0, 0, 24, 20, "approval_expiry_out_of_range")]
    [InlineData(1000, 0, 72, 0, 20, "estimate_wait_out_of_range")]
    [InlineData(1000, 0, 72, 24, -1, "overrun_percent_out_of_range")]
    public void The_cost_settings_are_validated(double limit, double second, int expiry, int wait, int overrun, string? error) =>
        Assert.Equal(error, (OrgPolicy.Default with { EstimateApprovalLimit = (decimal)limit, SecondApproverAbove = (decimal)second, ApprovalExpiryHours = expiry, EstimateWaitHours = wait, OverrunPercent = overrun }).Validate());

    [Fact]
    public void The_cost_settings_survive_the_settings_json()
    {
        var p = OrgPolicy.Default with { EstimateApprovalLimit = 15000.50m, SecondApproverAbove = 80000, ApprovalExpiryHours = 48, EstimateWaitHours = 12, OverrunPercent = 15 };
        Assert.Equal(p, OrgPolicy.From(p.WriteInto("{}")));
        Assert.Equal((0m, 0m, 72, 24, 20), (OrgPolicy.Default.EstimateApprovalLimit, OrgPolicy.Default.SecondApproverAbove, OrgPolicy.Default.ApprovalExpiryHours, OrgPolicy.Default.EstimateWaitHours, OrgPolicy.Default.OverrunPercent));
    }
}

[Collection("db")]
public class ApprovalFlowTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task SetPolicy(Society s, OrgPolicy p) => s.Manager.Client.PutAsJsonAsync("/organization/policies", p).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    /// <summary>A job assigned to the technician and accepted, ready for an estimate.</summary>
    private static async Task<JobDto> AcceptedJob(Society s)
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Water leak", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Fix", null))).Content.ReadFromJsonAsync<JobDto>())!;
        (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId))).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null)).EnsureSuccessStatusCode();
        return job;
    }

    private static Task<HttpResponseMessage> Estimate(ApiSession who, Guid job, decimal rate) =>
        who.Client.PostAsJsonAsync($"/jobs/{job}/estimates", new SaveEstimate(null, [new CostLineInput("labour", null, "Plumber", 1, "day", rate)]));

    private static Task<HttpResponseMessage> Start(Society s, Guid job) => s.Technician.Client.PostAsJsonAsync($"/jobs/{job}/start", new StartWork(null, null, null));

    private static async Task<List<ApprovalDto>> Pending(ApiSession who) => (await who.Client.GetFromJsonAsync<List<ApprovalDto>>("/approvals"))!;

    // JC-03 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_estimate_under_the_limit_needs_no_approval_and_with_no_limit_set_nothing_is_ever_asked()
    {
        var s = await Society.CreateAsync(app);
        var none = await AcceptedJob(s);
        var big = (await (await Estimate(s.Technician, none.Id, 900000)).Content.ReadFromJsonAsync<EstimateDto>())!;
        Assert.Equal("NotRequired", big.ApprovalStatus);
        Assert.Equal(HttpStatusCode.OK, (await Start(s, none.Id)).StatusCode);

        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);
        var small = (await (await Estimate(s.Technician, job.Id, 800)).Content.ReadFromJsonAsync<EstimateDto>())!;
        Assert.Equal("NotRequired", small.ApprovalStatus);
        Assert.Empty(await Pending(s.Manager));
        Assert.Equal(HttpStatusCode.OK, (await Start(s, job.Id)).StatusCode);
    }

    [Fact]
    public async Task An_estimate_over_the_limit_blocks_work_until_a_manager_approves_it()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);

        var est = (await (await Estimate(s.Technician, job.Id, 2000)).Content.ReadFromJsonAsync<EstimateDto>())!;
        Assert.Equal("Pending", est.ApprovalStatus);
        var blocked = await Start(s, job.Id);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("estimate_not_approved", await blocked.Content.ReadAsStringAsync());

        var waiting = Assert.Single(await Pending(s.Manager));
        Assert.Equal((job.Number, 2000m, 1, "Pending"), (waiting.JobNumber, waiting.Amount, waiting.ApprovalsNeeded, waiting.Status));
        Assert.Equal(est.Id, (await s.Manager.Client.GetFromJsonAsync<JobDetailDto>($"/jobs/{job.Id}"))!.Estimate!.Id);

        // Not the technician who proposed it, not a supervisor.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync($"/approvals/{waiting.Id}/decision", new Decide(true, null))).StatusCode);
        var supervisor = await s.Supervisor.Client.PostAsJsonAsync($"/approvals/{waiting.Id}/decision", new Decide(true, null));
        Assert.Equal(HttpStatusCode.Forbidden, supervisor.StatusCode);
        Assert.Contains("not_an_approver", await supervisor.Content.ReadAsStringAsync());

        var approved = await s.Manager.Client.PostAsJsonAsync($"/approvals/{waiting.Id}/decision", new Decide(true, "Fine, go ahead"));
        Assert.Equal("Approved", (await approved.Content.ReadFromJsonAsync<ApprovalDto>())!.Status);
        Assert.Equal("Approved", (await s.Manager.Client.GetFromJsonAsync<JobDetailDto>($"/jobs/{job.Id}"))!.Estimate!.ApprovalStatus);
        Assert.Equal(HttpStatusCode.OK, (await Start(s, job.Id)).StatusCode);

        var timeline = (await s.Manager.Client.GetFromJsonAsync<JobDetailDto>($"/jobs/{job.Id}"))!.Timeline.Where(t => t.Kind == "approval").Select(t => t.Note).ToList();
        Assert.Contains(timeline, n => n!.Contains("sent for approval"));
        Assert.Contains(timeline, n => n!.Contains("Estimate approved: Fine, go ahead"));
    }

    [Fact]
    public async Task A_rejection_needs_a_reason_returns_the_estimate_and_a_revision_replaces_it()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 2000);
        var id = Assert.Single(await Pending(s.Manager)).Id;

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(false, "  "))).StatusCode);
        var no = await s.Manager.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(false, "Too expensive for a washer"));
        Assert.Equal("Rejected", (await no.Content.ReadFromJsonAsync<ApprovalDto>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Start(s, job.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Manager.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(true, null))).StatusCode); // answered already

        // Revised under the limit: no approval needed, work can start.
        var revised = (await (await Estimate(s.Technician, job.Id, 600)).Content.ReadFromJsonAsync<EstimateDto>())!;
        Assert.Equal(("NotRequired", 2), (revised.ApprovalStatus, revised.Version));
        Assert.Equal(HttpStatusCode.OK, (await Start(s, job.Id)).StatusCode);

        await Drain();
        var inbox = await s.Technician.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications");
        Assert.Contains(inbox!.Items, n => n.Template == "approval_rejected" && n.Body.Contains("Too expensive for a washer"));
    }

    [Fact]
    public async Task A_revision_over_the_limit_withdraws_the_old_request_and_asks_again()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 2000);
        var first = Assert.Single(await Pending(s.Manager)).Id;
        await Estimate(s.Technician, job.Id, 1500);

        var now = Assert.Single(await Pending(s.Manager));
        Assert.NotEqual(first, now.Id);
        Assert.Equal(1500m, now.Amount);
        Assert.Equal("Cancelled", Assert.Single(await s.Manager.Client.GetFromJsonAsync<List<ApprovalDto>>("/approvals?status=Cancelled")!, a => a.Id == first).Status);
    }

    [Fact]
    public async Task A_very_large_estimate_needs_two_different_people_and_nobody_answers_their_own_request()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000, SecondApproverAbove = 5000 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 6000);
        var approval = Assert.Single(await Pending(s.Manager));
        Assert.Equal(2, approval.ApprovalsNeeded);

        var committee = await app.AddMemberAsync(s.Manager, s.Org, Role.Committee);
        var first = await s.Manager.Client.PostAsJsonAsync($"/approvals/{approval.Id}/decision", new Decide(true, null));
        var afterFirst = (await first.Content.ReadFromJsonAsync<ApprovalDto>())!;
        Assert.Equal(("Pending", 1), (afterFirst.Status, afterFirst.Decisions.Count));
        Assert.Equal(HttpStatusCode.Conflict, (await Start(s, job.Id)).StatusCode); // one yes is not enough

        var again = await s.Manager.Client.PostAsJsonAsync($"/approvals/{approval.Id}/decision", new Decide(true, null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already_decided", await again.Content.ReadAsStringAsync());

        var second = await committee.Client.PostAsJsonAsync($"/approvals/{approval.Id}/decision", new Decide(true, null));
        Assert.Equal("Approved", (await second.Content.ReadFromJsonAsync<ApprovalDto>())!.Status);
        Assert.Equal(HttpStatusCode.OK, (await Start(s, job.Id)).StatusCode);
    }

    [Fact]
    public async Task A_manager_who_proposed_the_estimate_cannot_approve_it_but_another_manager_can()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);
        await Estimate(s.Manager, job.Id, 3000);
        var id = Assert.Single(await Pending(s.Manager)).Id;

        var own = await s.Manager.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(true, null));
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Contains("requester_cannot_decide", await own.Content.ReadAsStringAsync());

        var other = await app.AddMemberAsync(s.Manager, s.Org, Role.Manager);
        Assert.Equal(HttpStatusCode.OK, (await other.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(true, null))).StatusCode);
    }

    [Fact]
    public async Task A_request_nobody_answers_expires_and_the_estimate_goes_back_for_revision()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 2000);
        var id = Assert.Single(await Pending(s.Manager)).Id;

        await Sql("UPDATE approvals SET expires_at = now() - interval '1 minute' WHERE id = @id", ("id", id));
        await app.Services.GetRequiredService<ApprovalSweeper>().RunAsync(default);

        Assert.Equal("Expired", Assert.Single(await s.Manager.Client.GetFromJsonAsync<List<ApprovalDto>>("/approvals?status=Expired")!, a => a.Id == id).Status);
        Assert.Equal("Expired", (await s.Manager.Client.GetFromJsonAsync<JobDetailDto>($"/jobs/{job.Id}"))!.Estimate!.ApprovalStatus);
        Assert.Equal(HttpStatusCode.Conflict, (await Start(s, job.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Manager.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(true, null))).StatusCode);
    }

    [Fact]
    public async Task Managers_are_told_when_an_estimate_needs_them_and_the_approvals_list_is_not_for_everyone()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 2000);
        await Drain();

        var asked = (await s.Manager.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications"))!.Items.Single(n => n.Template == "approval_requested");
        Assert.Contains(job.Number, asked.Body);
        Assert.Contains("2000.00", asked.Body);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/approvals")).StatusCode);
    }

    // RT-11 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_estimate_waiting_too_long_escalates_to_supervisors_then_managers_and_clears_once_decided()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 1000, EstimateWaitHours = 24, ApprovalExpiryHours = 720 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 2000);
        var id = Assert.Single(await Pending(s.Manager)).Id;
        await Sql("UPDATE approvals SET requested_at = now() - interval '37 hours' WHERE id = @id", ("id", id));

        await app.Services.GetRequiredService<EscalationScanner>().RunAsync(default);
        var open = (await s.Supervisor.Client.GetFromJsonAsync<List<EscalationDto>>("/escalations?open=true"))!.Where(e => e.Rule == EscalationRuleNames.EstimateWaiting).OrderBy(e => e.Level).ToList();
        Assert.Equal([1, 2], open.Select(e => e.Level)); // 13 hours past the wait: both levels (supervisors at once, managers after 12)

        await s.Manager.Client.PostAsJsonAsync($"/approvals/{id}/decision", new Decide(true, null));
        await app.Services.GetRequiredService<EscalationScanner>().RunAsync(default);
        Assert.DoesNotContain((await s.Supervisor.Client.GetFromJsonAsync<List<EscalationDto>>("/escalations?open=true"))!, e => e.Rule == EscalationRuleNames.EstimateWaiting && e.ComplaintId == open[0].ComplaintId);
    }

    // JC-05 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Costs_far_over_the_estimate_are_flagged_once_listed_and_cleared_when_corrected()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { OverrunPercent = 20 });
        var job = await AcceptedJob(s);
        await Estimate(s.Technician, job.Id, 1000);
        Assert.Equal(HttpStatusCode.OK, (await Start(s, job.Id)).StatusCode);

        async Task<CostsResult> Cost(decimal rate) => (await (await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new CostLineInput("labour", null, "Plumber", 1, "day", rate)]))).Content.ReadFromJsonAsync<CostsResult>())!;

        var within = await Cost(1100);
        Assert.Equal((false, 10.0m), (within.Variance.Overrun, within.Variance.Percent));
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<OverrunDto>>("/jobs/overruns"))!);

        var over = await Cost(1300);
        Assert.Equal((true, 30.0m, 20), (over.Variance.Overrun, over.Variance.Percent, over.Variance.ThresholdPercent));
        var listed = Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<OverrunDto>>("/jobs/overruns"))!);
        Assert.Equal((job.Number, 1000m, 1300m, 300m, 30.0), (listed.Number, listed.Estimated, listed.Actual, listed.Difference, listed.Percent));
        Assert.NotNull(listed.FlaggedAt);

        await Cost(1400); // worse again: still one flag, one alert
        await Drain();
        Assert.Single((await s.Manager.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications"))!.Items.Where(n => n.Template == "cost_overrun"));

        await Cost(1000);
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<OverrunDto>>("/jobs/overruns"))!);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/jobs/overruns")).StatusCode);
    }

    [Fact]
    public async Task A_job_with_no_estimate_is_never_flagged_and_the_flag_can_be_switched_off()
    {
        var s = await Society.CreateAsync(app);
        await SetPolicy(s, OrgPolicy.Default with { OverrunPercent = 20 });
        var job = await AcceptedJob(s);
        await Start(s, job.Id);
        var costs = (await (await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new CostLineInput("labour", null, "Plumber", 1, "day", 99999)]))).Content.ReadFromJsonAsync<CostsResult>())!;
        Assert.False(costs.Variance.Overrun);
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<OverrunDto>>("/jobs/overruns"))!);
    }

    // JB-10 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_supervisors_mark_a_job_chargeable_to_a_house_and_it_is_audited()
    {
        var s = await Society.CreateAsync(app);
        var job = await AcceptedJob(s);

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, s.HouseId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, s.HouseId))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, Guid.NewGuid()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{Guid.NewGuid()}/chargeable", new SetChargeable(false, null))).StatusCode);

        var on = (await (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, s.HouseId))).Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal((true, s.HouseId), (on.Chargeable, on.ChargeHouseId));
        var off = (await (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(false, null))).Content.ReadFromJsonAsync<JobDto>())!;
        Assert.Equal((false, null), (off.Chargeable, off.ChargeHouseId));

        var audit = await s.Manager.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>($"/audit?entityType=Job&entityId={job.Id}&limit=50");
        Assert.True(audit!.Count(a => a.After.HasValue && a.After.Value.ToString().Contains("\"Chargeable\":true")) >= 1);
    }

    [Fact]
    public async Task The_settings_endpoint_carries_the_cost_settings_and_rejects_nonsense()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { EstimateApprovalLimit = 1000, SecondApproverAbove = 500 })).StatusCode);
        await SetPolicy(s, OrgPolicy.Default with { EstimateApprovalLimit = 2500.75m, OverrunPercent = 10 });
        var read = (await s.Supervisor.Client.GetFromJsonAsync<OrgPolicy>("/organization/policies"))!;
        Assert.Equal((2500.75m, 10), (read.EstimateApprovalLimit, read.OverrunPercent));
    }
}
