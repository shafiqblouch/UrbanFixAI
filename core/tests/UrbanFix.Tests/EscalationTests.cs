using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;

namespace UrbanFix.Tests;

public class EscalationRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static EscalationRules.Facts Facts(
        ComplaintStatus status = ComplaintStatus.New, DateTimeOffset? respondBy = null, DateTimeOffset? dueAt = null,
        string? job = null, DateTimeOffset? jobCreated = null, DateTimeOffset? submitted = null, DateTimeOffset? reopened = null) =>
        new(status, Now.AddDays(-3), respondBy, dueAt, job, jobCreated, submitted, reopened);

    private static string[] Rules(EscalationRules.Facts f) => EscalationRules.Evaluate(f, Now).Select(c => c.Rule).ToArray();

    [Fact]
    public void Not_assigned_fires_once_the_respond_by_time_has_passed()
    {
        Assert.Empty(Rules(Facts(respondBy: Now.AddHours(1))));
        var c = Assert.Single(EscalationRules.Evaluate(Facts(respondBy: Now.AddHours(-2)), Now));
        Assert.Equal((EscalationRuleNames.NotAssigned, Now.AddHours(-2)), (c.Rule, c.Since));
    }

    [Fact]
    public void A_job_nobody_accepted_fires_after_a_quarter_of_the_resolve_time()
    {
        // created 3 days ago, due tomorrow: 4 days in total, so 24 hours to accept
        var due = Now.AddDays(1);
        Assert.Empty(Rules(Facts(ComplaintStatus.Assigned, dueAt: due, job: JobStatus.Open, jobCreated: Now.AddHours(-23))));
        Assert.Equal([EscalationRuleNames.NotAccepted], Rules(Facts(ComplaintStatus.Assigned, dueAt: due, job: JobStatus.Open, jobCreated: Now.AddHours(-25))));
        Assert.Empty(Rules(Facts(ComplaintStatus.Assigned, dueAt: due, job: JobStatus.Accepted, jobCreated: Now.AddHours(-30))));
    }

    [Fact]
    public void Target_passed_applies_to_assigned_work_but_not_to_finished_complaints()
    {
        Assert.Equal([EscalationRuleNames.TargetPassed], Rules(Facts(ComplaintStatus.InProgress, dueAt: Now.AddHours(-1), job: JobStatus.InProgress, jobCreated: Now.AddHours(-2))));
        Assert.Empty(Rules(Facts(ComplaintStatus.Resolved, dueAt: Now.AddHours(-1))));
        Assert.Empty(Rules(Facts(ComplaintStatus.Closed, dueAt: Now.AddHours(-1))));
    }

    [Fact]
    public void Work_waiting_for_sign_off_for_a_day_escalates()
    {
        Assert.Empty(Rules(Facts(ComplaintStatus.InProgress, dueAt: Now.AddDays(1), job: JobStatus.PendingVerification, jobCreated: Now.AddDays(-2), submitted: Now.AddHours(-23))));
        Assert.Equal([EscalationRuleNames.AwaitingSignOff],
            Rules(Facts(ComplaintStatus.InProgress, dueAt: Now.AddDays(1), job: JobStatus.PendingVerification, jobCreated: Now.AddDays(-2), submitted: Now.AddHours(-25))));
    }

    [Fact]
    public void A_reopened_complaint_without_a_new_job_is_a_dispute_and_nothing_else()
    {
        var f = Facts(ComplaintStatus.UnderReview, respondBy: Now.AddDays(-1), reopened: Now.AddHours(-1), job: JobStatus.Completed, jobCreated: Now.AddDays(-2));
        Assert.Equal([EscalationRuleNames.Dispute], Rules(f));
        // a fresh job clears the dispute
        Assert.DoesNotContain(EscalationRuleNames.Dispute, Rules(f with { JobStatus = JobStatus.Open, JobCreatedAt = Now }));
    }

    [Fact]
    public void Levels_come_due_in_order_by_their_delay()
    {
        var levels = new (int, int, Role)[] { (1, 0, Role.Supervisor), (2, 12, Role.Manager) };
        Assert.Equal([1], EscalationRules.DueLevels(levels, Now.AddHours(-3), Now).Select(l => l.Level));
        Assert.Equal([1, 2], EscalationRules.DueLevels(levels, Now.AddHours(-13), Now).Select(l => l.Level));
        Assert.Empty(EscalationRules.DueLevels(levels, Now.AddHours(1), Now));
    }

    [Fact]
    public void Level_configuration_is_validated()
    {
        Assert.Null(EscalationRules.ValidateLevels([(1, 0, Role.Supervisor), (2, 6, Role.Manager)]));
        Assert.Equal("one_to_five_levels", EscalationRules.ValidateLevels([]));
        Assert.Equal("duplicate_level", EscalationRules.ValidateLevels([(1, 0, Role.Supervisor), (1, 4, Role.Manager)]));
        Assert.Equal("levels_must_start_at_1", EscalationRules.ValidateLevels([(2, 0, Role.Supervisor)]));
        Assert.Equal("levels_must_be_consecutive", EscalationRules.ValidateLevels([(1, 0, Role.Supervisor), (3, 4, Role.Manager)]));
        Assert.Equal("later_levels_must_not_come_sooner", EscalationRules.ValidateLevels([(1, 8, Role.Supervisor), (2, 4, Role.Manager)]));
        Assert.Equal("role_cannot_receive_escalations", EscalationRules.ValidateLevels([(1, 0, Role.Resident)]));
    }
}

[Collection("db")]
public class EscalationTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> NewComplaint(Society s) =>
        (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!.Id;

    private static async Task<List<EscalationDto>> Escalations(Society s, string query = "") =>
        (await s.Supervisor.Client.GetFromJsonAsync<List<EscalationDto>>("/escalations" + query))!;

    private Task<int> Scan() => app.Services.GetRequiredService<EscalationScanner>().RunAsync(default);

    [Fact]
    public async Task An_unassigned_complaint_escalates_once_per_level_and_clears_when_it_is_assigned()
    {
        var s = await Society.CreateAsync(app);
        var id = await NewComplaint(s);
        await Sql("UPDATE complaints SET respond_by = now() - interval '13 hours' WHERE id = @id", ("id", id));

        await Scan();
        var rows = (await Escalations(s)).Where(e => e.ComplaintId == id).OrderBy(e => e.Level).ToList();
        Assert.Equal([1, 2], rows.Select(r => r.Level));
        Assert.All(rows, r => Assert.Equal((EscalationRuleNames.NotAssigned, null), (r.Rule, r.ResolvedAt)));

        await Scan(); // nothing new the second time
        Assert.Equal(2, (await Escalations(s)).Count(e => e.ComplaintId == id));

        // A person acts: review and assign.
        (await s.Office.Client.PostAsJsonAsync($"/complaints/{id}/transition", new Transition(ComplaintStatus.UnderReview, null, null))).EnsureSuccessStatusCode();
        (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/jobs", new CreateJob("Fix", null))).EnsureSuccessStatusCode();
        await Scan();
        var after = (await Escalations(s)).Where(e => e.ComplaintId == id && e.Rule == EscalationRuleNames.NotAssigned).ToList();
        Assert.All(after, r => Assert.Equal("condition_cleared", r.Resolution));
        Assert.Empty((await Escalations(s, "?open=true")).Where(e => e.ComplaintId == id && e.Rule == EscalationRuleNames.NotAssigned));
    }

    [Fact]
    public async Task A_resident_dispute_escalates_and_clears_when_a_new_job_is_raised()
    {
        var s = await Society.CreateAsync(app);
        var id = await NewComplaint(s);
        await Sql("UPDATE complaints SET status = 'UnderReview', respond_by = now() + interval '1 day' WHERE id = @id", ("id", id));
        await Sql("INSERT INTO complaint_status_history (id, organization_id, complaint_id, from_status, to_status, occurred_at) VALUES (gen_random_uuid(), @o, @c, 'Resolved', 'UnderReview', now() - interval '1 hour')",
            ("o", s.Org), ("c", id));

        await Scan();
        Assert.Contains(await Escalations(s, "?open=true"), e => e.ComplaintId == id && e.Rule == EscalationRuleNames.Dispute && e.Level == 1);

        (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/jobs", new CreateJob("Rework", null))).EnsureSuccessStatusCode();
        await Scan();
        Assert.DoesNotContain(await Escalations(s, "?open=true"), e => e.ComplaintId == id && e.Rule == EscalationRuleNames.Dispute);
    }

    [Fact]
    public async Task Levels_and_contacts_are_configurable_by_managers_and_followed_by_the_scanner()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(2, (await s.Supervisor.Client.GetFromJsonAsync<List<LevelDto>>("/escalation-levels"))!.Count); // defaults

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.PutAsJsonAsync("/escalation-levels", new[] { new LevelDto(1, 0, Role.Manager) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/escalation-levels", new[] { new LevelDto(1, 0, Role.Resident) })).StatusCode);
        (await s.Manager.Client.PutAsJsonAsync("/escalation-levels", new[] { new LevelDto(1, 0, Role.Manager) })).EnsureSuccessStatusCode();
        Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<LevelDto>>("/escalation-levels"))!);

        var id = await NewComplaint(s);
        await Sql("UPDATE complaints SET respond_by = now() - interval '30 hours' WHERE id = @id", ("id", id));
        await Scan();
        Assert.Single((await Escalations(s)).Where(e => e.ComplaintId == id)); // one level only
    }

    [Fact]
    public async Task A_failing_scan_reports_at_once_and_still_fails_the_job()
    {
        var notifier = new RecordingNotifier();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Owner"] = app.OwnerConnectionString }).Build();
        var scanner = new EscalationScanner(new BrokenScopes(), config, notifier);

        await Assert.ThrowsAsync<AggregateException>(() => scanner.RunAsync(default));
        Assert.Contains(notifier.Failures, f => f.JobId == "escalation-scan" && f.Reason == "database is down");
    }

    private sealed class BrokenScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("database is down");
    }
}
