using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.CostReportEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

public class ChargeRulesTests
{
    [Fact]
    public void Accounts_approve_as_proposed_adjust_with_a_reason_or_reject_with_a_reason()
    {
        Assert.Equal((null, "Approved", 700m), ChargeRules.Decide("Pending", 700, "approve", null, null));
        Assert.Equal((null, "Adjusted", 500m), ChargeRules.Decide("Pending", 700, "adjust", 500, "Resident supplied the pipe"));
        Assert.Equal((null, "Rejected", (decimal?)null), ChargeRules.Decide("Pending", 700, "reject", null, "Landlord's responsibility"));
        Assert.Equal("amount_required", ChargeRules.Decide("Pending", 700, "adjust", null, "x").Error);
        Assert.Equal("amount_required", ChargeRules.Decide("Pending", 700, "adjust", -1, "x").Error);
        Assert.Equal("reason_required", ChargeRules.Decide("Pending", 700, "adjust", 500, " ").Error);
        Assert.Equal("reason_required", ChargeRules.Decide("Pending", 700, "reject", null, null).Error);
        Assert.Equal("unknown_action", ChargeRules.Decide("Pending", 700, "waive", null, "x").Error);
        Assert.Equal(0m, ChargeRules.Decide("Pending", 700, "adjust", 0, "Goodwill").Final);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("Adjusted")]
    [InlineData("Rejected")]
    [InlineData("Cancelled")]
    public void A_decided_charge_is_never_decided_again(string status) => Assert.Equal("not_pending", ChargeRules.Decide(status, 700, "approve", null, null).Error);

    [Fact]
    public void Only_approved_or_adjusted_charges_are_ready_for_billing()
    {
        Assert.True(ChargeRules.ReadyToPost("Approved"));
        Assert.True(ChargeRules.ReadyToPost("Adjusted"));
        Assert.False(ChargeRules.ReadyToPost("Pending"));
        Assert.False(ChargeRules.ReadyToPost("Rejected"));
    }
}

[Collection("db")]
public class CostReportTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private static async Task<Guid> Category(Society s, string key) => (await s.Manager.Client.GetFromJsonAsync<List<CategoryDto>>("/categories"))!.Single(c => c.Key == key).Id;

    /// <summary>A job on a new complaint at a house, with a recorded cost.</summary>
    private async Task<JobDto> JobAt(Society s, Guid category, Guid houseId, decimal cost)
    {
        var created = (await (await s.Office.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Repair needed", "phone", category, houseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Fix", null))).Content.ReadFromJsonAsync<JobDto>())!;
        if (cost > 0)
            await Sql("INSERT INTO job_costs (id, organization_id, job_id, kind, description, quantity, unit, unit_rate, amount) VALUES (gen_random_uuid(), @o, @j, 'labour', 'Work', 1, 'job', @a, @a)", ("o", s.Org), ("j", job.Id), ("a", cost));
        return job;
    }

    /// <summary>A second house on a street of its own.</summary>
    private static async Task<Guid> HouseOnNewStreet(Society s)
    {
        var street = (await (await s.Manager.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "street", "Street 9", null, null, null))).Content.ReadFromJsonAsync<LocationDto>())!;
        var house = (await (await s.Manager.Client.PostAsJsonAsync("/locations", new CreateLocation(street.Id, "house", "House 2", null, null, null))).Content.ReadFromJsonAsync<LocationDto>())!;
        return (await (await s.Manager.Client.PostAsJsonAsync("/houses", new CreateHouse(house.Id, null, null, null))).Content.ReadFromJsonAsync<HouseDto>())!.Id;
    }

    // JC-10 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Spend_per_job_category_and_street_always_adds_up_to_the_job_actuals()
    {
        var s = await Society.CreateAsync(app);
        var water = await Category(s, "water");
        var roads = await Category(s, "roads");
        var house2 = await HouseOnNewStreet(s);
        await JobAt(s, water, s.HouseId, 100);
        await JobAt(s, water, s.HouseId, 200);
        var big = await JobAt(s, roads, house2, 500);

        var byJob = (await s.Manager.Client.GetFromJsonAsync<CostReport>("/reports/costs?groupBy=job"))!;
        Assert.Equal((3, 800m), (byJob.Jobs, byJob.Actual));
        Assert.Equal(500m, byJob.Rows[0].Actual); // biggest spend first
        Assert.Contains(big.Number, byJob.Rows[0].Name);

        var byCategory = (await s.Manager.Client.GetFromJsonAsync<CostReport>("/reports/costs?groupBy=category"))!;
        Assert.Equal([("Water supply", 2, 300m), ("Roads and potholes", 1, 500m)], byCategory.Rows.OrderBy(r => r.Name == "Water supply" ? 0 : 1).Select(r => (r.Name, r.Jobs, r.Actual)));
        Assert.Equal(800m, byCategory.Rows.Sum(r => r.Actual));

        var byStreet = (await s.Manager.Client.GetFromJsonAsync<CostReport>("/reports/costs?groupBy=street"))!;
        Assert.Equal([("Street 9", 500m), ("Block A", 300m)], byStreet.Rows.Select(r => (r.Name, r.Actual)));
        Assert.Equal(800m, byStreet.Actual);

        var direct = await Sql800(s);
        Assert.Equal(direct, byJob.Actual);
    }

    private async Task<decimal> Sql800(Society s)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT COALESCE(SUM(amount), 0) FROM job_costs WHERE organization_id = @o", conn);
        cmd.Parameters.AddWithValue("o", s.Org);
        return (decimal)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task The_report_shows_the_estimate_against_the_actual_and_can_be_limited_to_a_period()
    {
        var s = await Society.CreateAsync(app);
        var job = await JobAt(s, await Category(s, "water"), s.HouseId, 1300);
        (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/estimates", new SaveEstimate(null, [new CostLineInput("labour", null, "Plumber", 1, "day", 1000)]))).EnsureSuccessStatusCode();

        var row = Assert.Single((await s.Manager.Client.GetFromJsonAsync<CostReport>("/reports/costs?groupBy=category"))!.Rows);
        Assert.Equal((1000m, 1300m, 300m), (row.Estimated, row.Actual, row.Variance));

        var future = DateTimeOffset.UtcNow.AddDays(2).ToString("o");
        Assert.Empty((await s.Manager.Client.GetFromJsonAsync<CostReport>($"/reports/costs?from={Uri.EscapeDataString(future)}"))!.Rows);
        var past = DateTimeOffset.UtcNow.AddDays(-2).ToString("o");
        Assert.Single((await s.Manager.Client.GetFromJsonAsync<CostReport>($"/reports/costs?from={Uri.EscapeDataString(past)}"))!.Rows);
    }

    [Fact]
    public async Task Cancelled_jobs_are_left_out_and_a_bad_grouping_or_role_is_refused()
    {
        var s = await Society.CreateAsync(app);
        var cancelled = await JobAt(s, await Category(s, "water"), s.HouseId, 900);
        await Sql("UPDATE jobs SET status = 'Cancelled' WHERE id = @j", ("j", cancelled.Id));
        Assert.Equal(0, (await s.Manager.Client.GetFromJsonAsync<CostReport>("/reports/costs"))!.Jobs);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.GetAsync("/reports/costs?groupBy=colour")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/reports/costs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/reports/repeated-repairs")).StatusCode);
    }

    [Fact]
    public async Task The_same_repair_coming_back_to_a_house_or_street_is_flagged_by_a_threshold_that_can_be_changed()
    {
        var s = await Society.CreateAsync(app);
        var water = await Category(s, "water");
        var jobs = new List<JobDto>();
        for (var i = 0; i < 3; i++) jobs.Add(await JobAt(s, water, s.HouseId, 100 * (i + 1)));
        await JobAt(s, await Category(s, "roads"), s.HouseId, 50);

        var flagged = Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs"))!);
        Assert.Equal(("Water supply", 3, 600m), (flagged.Category, flagged.Repairs, flagged.TotalCost));
        Assert.Contains("House 1", flagged.Place);
        Assert.Equal(3, flagged.Jobs.Count);

        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs?minCount=4"))!);
        var any = Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs?minCount=4&sameCategory=false"))!);
        Assert.Equal(4, any.Repairs);
        Assert.Equal("Block A", Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs?by=street"))!).Place);

        // the society's own setting is the default
        (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { RepeatedRepairCount = 5 })).EnsureSuccessStatusCode();
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs"))!);

        // old repairs fall out of the window
        (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { RepeatedRepairCount = 3, RepeatedRepairMonths = 6 })).EnsureSuccessStatusCode();
        await Sql("UPDATE jobs SET created_at = now() - interval '8 months' WHERE id = @j", ("j", jobs[0].Id));
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs"))!);
        Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<RepeatedRepair>>("/reports/repeated-repairs?months=12"))!);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.GetAsync("/reports/repeated-repairs?by=planet")).StatusCode);
    }

    [Fact]
    public async Task The_repeated_repair_settings_are_validated()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { RepeatedRepairCount = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { RepeatedRepairMonths = 0 })).StatusCode);
    }

    // JC-11 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_finished_chargeable_job_reaches_accounts_with_its_cost_evidence_and_residents_and_waits_for_a_decision()
    {
        var s = await Society.CreateAsync(app);
        var accounts = await app.AddMemberAsync(s.Manager, s.Org, Role.Accounts);
        var job = await JobAt(s, await Category(s, "water"), s.HouseId, 700);
        (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, s.HouseId))).EnsureSuccessStatusCode();
        Assert.Empty((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!); // not finished yet

        await s.FinishWorkAsync(job);
        await Drain();
        var charge = Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!);
        Assert.Equal((job.Number, 700m, "Pending", false, s.HouseId), (charge.JobNumber, charge.ProposedAmount, charge.Status, charge.ReadyToPost, charge.HouseId));
        Assert.Contains("House 1", charge.HouseLabel);
        Assert.Contains(charge.Residents, r => r.UserId == s.ResidentId);
        Assert.Equal(("Work", 700m), (charge.Costs.Single().Description, charge.Costs.Single().Amount));

        // finishing again changes nothing: one charge per job
        await Drain();
        Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!);

        var approved = await accounts.Client.PostAsJsonAsync($"/charges/{charge.Id}/decision", new ChargeDecision("approve", null, null));
        var done = (await approved.Content.ReadFromJsonAsync<ChargeDto>())!;
        Assert.Equal(("Approved", 700m, true), (done.Status, done.FinalAmount, done.ReadyToPost));
        Assert.Empty((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!);
        Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges?status=Approved"))!);
        Assert.Equal(HttpStatusCode.Conflict, (await accounts.Client.PostAsJsonAsync($"/charges/{charge.Id}/decision", new ChargeDecision("reject", null, "Changed my mind"))).StatusCode);
    }

    [Fact]
    public async Task Accounts_can_adjust_or_reject_a_charge_but_only_with_a_reason_and_only_accounts_decide()
    {
        var s = await Society.CreateAsync(app);
        var accounts = await app.AddMemberAsync(s.Manager, s.Org, Role.Accounts);
        var a = await JobAt(s, await Category(s, "water"), s.HouseId, 700);
        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{a.Id}/chargeable", new SetChargeable(true, s.HouseId));
        await s.FinishWorkAsync(a);
        await Drain();
        var id = Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!).Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.Client.PostAsJsonAsync($"/charges/{id}/decision", new ChargeDecision("approve", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.GetAsync("/charges")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accounts.Client.PostAsJsonAsync($"/charges/{id}/decision", new ChargeDecision("adjust", 500, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accounts.Client.PostAsJsonAsync($"/charges/{id}/decision", new ChargeDecision("adjust", null, "Pipe supplied by resident"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await accounts.Client.PostAsJsonAsync($"/charges/{id}/decision", new ChargeDecision("reject", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await accounts.Client.PostAsJsonAsync($"/charges/{Guid.NewGuid()}/decision", new ChargeDecision("approve", null, null))).StatusCode);

        var adjusted = (await (await accounts.Client.PostAsJsonAsync($"/charges/{id}/decision", new ChargeDecision("adjust", 500, "Pipe supplied by resident"))).Content.ReadFromJsonAsync<ChargeDto>())!;
        Assert.Equal(("Adjusted", 500m, 700m, "Pipe supplied by resident"), (adjusted.Status, adjusted.FinalAmount, adjusted.ProposedAmount, adjusted.Note));

        var b = await JobAt(s, await Category(s, "water"), s.HouseId, 90);
        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{b.Id}/chargeable", new SetChargeable(true, s.HouseId));
        await s.FinishWorkAsync(b);
        await Drain();
        var second = Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!).Id;
        var rejected = (await (await accounts.Client.PostAsJsonAsync($"/charges/{second}/decision", new ChargeDecision("reject", null, "Damage was caused by the contractor"))).Content.ReadFromJsonAsync<ChargeDto>())!;
        Assert.Equal(("Rejected", null, false), (rejected.Status, rejected.FinalAmount, rejected.ReadyToPost));

        var audit = await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.AuditDto>>("/audit?entityType=JobCharge&limit=20");
        Assert.NotEmpty(audit!);
    }

    [Fact]
    public async Task Marking_a_finished_job_chargeable_queues_it_at_once_and_clearing_the_mark_withdraws_it()
    {
        var s = await Society.CreateAsync(app);
        var accounts = await app.AddMemberAsync(s.Manager, s.Org, Role.Accounts);
        var job = await JobAt(s, await Category(s, "water"), s.HouseId, 250);
        await s.FinishWorkAsync(job);
        await Drain();
        Assert.Empty((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!);

        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, s.HouseId));
        Assert.Equal(250m, Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!).ProposedAmount);
        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(true, s.HouseId)); // again: still one
        Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!);

        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/chargeable", new SetChargeable(false, null));
        Assert.Empty((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges"))!);
        Assert.Single((await accounts.Client.GetFromJsonAsync<List<ChargeDto>>("/charges?status=Cancelled"))!);
    }
}
