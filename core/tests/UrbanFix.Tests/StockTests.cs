using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.PricingEndpoints;
using static UrbanFix.Api.Endpoints.StockEndpoints;

namespace UrbanFix.Tests;

public class StockRulesTests
{
    [Theory]
    [InlineData("receive", 10, "x", null)]
    [InlineData("receive", -1, "x", "quantity_must_be_positive")]
    [InlineData("return", 2, null, null)]
    [InlineData("return", -2, null, "quantity_must_be_positive")]
    [InlineData("issue", -3, null, null)]
    [InlineData("issue", 3, null, "quantity_must_be_negative")]
    [InlineData("adjust", -4, "Broken in handling", null)]
    [InlineData("adjust", 4, "Found on a shelf", null)]
    [InlineData("adjust", 4, "  ", "reason_required")]
    [InlineData("adjust", 4, null, "reason_required")]
    [InlineData("receive", 0, "x", "quantity_required")]
    [InlineData("steal", 1, "x", "unknown_kind")]
    public void A_movement_has_the_right_sign_a_real_quantity_and_an_adjustment_says_why(string kind, double qty, string? reason, string? error) =>
        Assert.Equal(error, StockRules.Check(kind, (decimal)qty, reason));

    [Theory]
    [InlineData(10, 2, 8, 0, false)]
    [InlineData(10, 2, 7, 1, true)]
    [InlineData(10, 0, 12, -2, true)]
    [InlineData(0, 0, 0, 0, false)]
    public void Issued_less_returned_less_used_is_what_is_unexplained(double issued, double returned, double used, double diff, bool flagged)
    {
        Assert.Equal((decimal)diff, StockRules.Unexplained((decimal)issued, (decimal)returned, (decimal)used));
        Assert.Equal(flagged, StockRules.IsDiscrepancy((decimal)issued, (decimal)returned, (decimal)used));
    }

    [Theory]
    [InlineData(20, 5, false, false, false)]   // plenty
    [InlineData(5, 5, false, true, true)]      // reaches the level: alert
    [InlineData(3, 5, true, false, true)]      // still low: quiet
    [InlineData(6, 5, true, false, false)]     // back above: re-armed
    [InlineData(2, 0, false, false, false)]    // no threshold set
    public void The_low_stock_alert_fires_once_and_re_arms_when_stock_recovers(double stock, double threshold, bool already, bool alert, bool alerted)
    {
        var (a, now) = StockRules.NextAlert((decimal)stock, (decimal)threshold, already);
        Assert.Equal((alert, alerted), (a, now));
    }

    [Theory]
    [InlineData(10, 10, null, null)]
    [InlineData(10, 8, "Two broken", null)]
    [InlineData(10, 8, " ", "reason_required")]
    [InlineData(10, -1, "x", "counted_cannot_be_negative")]
    public void A_count_that_differs_from_the_books_needs_a_reason(double expected, double counted, string? reason, string? error) =>
        Assert.Equal(error, StockRules.CheckCount((decimal)expected, (decimal)counted, reason));

    [Fact]
    public void Supervisors_and_managers_keep_the_store_and_others_do_not()
    {
        Assert.True(RolePermissions.Allows(Role.Supervisor, Permissions.StockManage));
        Assert.True(RolePermissions.Allows(Role.Manager, Permissions.StockManage));
        Assert.False(RolePermissions.Allows(Role.Technician, Permissions.StockManage));
        Assert.True(RolePermissions.Allows(Role.Technician, Permissions.PricingRead)); // can see what is in stock
        Assert.False(RolePermissions.Allows(Role.Resident, Permissions.PricingRead));
    }
}

[Collection("db")]
public class StockFlowTests(TestApp app)
{
    private async Task<T?> Scalar<T>(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private static async Task<PriceItemDto> NewItem(Society s, string name = "Copper pipe", string unit = "m", decimal rate = 100) =>
        (await (await s.Manager.Client.PostAsJsonAsync("/price-items", new CreatePriceItem($"{name} {Guid.NewGuid():N}"[..20], null, unit, rate, null))).Content.ReadFromJsonAsync<PriceItemDto>())!;

    private static async Task<StockLevelDto> Level(Society s, Guid item, ApiSession? who = null) =>
        (await (who ?? s.Supervisor).Client.GetFromJsonAsync<List<StockLevelDto>>("/stock"))!.Single(l => l.ItemId == item);

    private static Task<HttpResponseMessage> Receive(Society s, Guid item, decimal qty, string? reason = null) =>
        s.Supervisor.Client.PostAsJsonAsync("/stock/receipts", new Receive(item, qty, reason));

    private static async Task<JobDto> StartedJob(Society s)
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Burst pipe", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Fix", null))).Content.ReadFromJsonAsync<JobDto>())!;
        (await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId))).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null)).EnsureSuccessStatusCode();
        (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/start", new StartWork(null, null, null))).EnsureSuccessStatusCode();
        return job;
    }

    // JC-06 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_is_the_sum_of_movements_and_each_movement_keeps_who_did_it_and_why()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        Assert.Equal(0, (await Level(s, item.Id)).OnHand);

        var first = await Receive(s, item.Id, 50, "Delivery from Ahmad Traders");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(50, (await first.Content.ReadFromJsonAsync<MovementResult>())!.OnHand);
        await Receive(s, item.Id, 25.5m);
        var adjusted = await s.Manager.Client.PostAsJsonAsync("/stock/adjustments", new Adjust(item.Id, -5.5m, "Damaged in the rain"));
        Assert.Equal(70, (await adjusted.Content.ReadFromJsonAsync<MovementResult>())!.OnHand);
        Assert.Equal(70, (await Level(s, item.Id)).OnHand);

        var moves = (await s.Supervisor.Client.GetFromJsonAsync<List<MovementDto>>($"/stock/items/{item.Id}/movements"))!;
        Assert.Equal(70, moves.Sum(m => m.Quantity));
        Assert.All(moves, m => { Assert.NotEqual(Guid.Empty, m.UserId); Assert.False(string.IsNullOrWhiteSpace(m.Reason)); });
        Assert.Contains(moves, m => m.Reason == "Delivery from Ahmad Traders");
        Assert.Contains(moves, m => m.Reason == "Received into the store");
    }

    [Fact]
    public async Task Movements_are_checked_and_stock_cannot_go_below_zero()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        var job = await StartedJob(s);
        Assert.Equal(HttpStatusCode.BadRequest, (await Receive(s, item.Id, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Receive(s, item.Id, -5)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync("/stock/adjustments", new Adjust(item.Id, 3, " "))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Receive(s, Guid.NewGuid(), 5)).StatusCode);

        await Receive(s, item.Id, 10);
        var over = await s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(job.Id, item.Id, 11, null));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("insufficient_stock", await over.Content.ReadAsStringAsync());
        var tooMuch = await s.Supervisor.Client.PostAsJsonAsync("/stock/adjustments", new Adjust(item.Id, -11, "Miscount"));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal(10, (await Level(s, item.Id)).OnHand);
    }

    [Fact]
    public async Task Two_issues_at_the_same_moment_cannot_both_take_the_last_stock()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        var job = await StartedJob(s);
        await Receive(s, item.Id, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(job.Id, item.Id, 4, null))));
        Assert.Equal(2, results.Count(r => r.StatusCode == HttpStatusCode.Created)); // 4 + 4 fit in 10; the rest are refused
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(2, (await Level(s, item.Id)).OnHand);
    }

    [Fact]
    public async Task The_ledger_cannot_be_edited_or_deleted_even_by_the_application_itself()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        await Receive(s, item.Id, 5);
        await using var conn = await app.OpenAppConnectionAsync(s.Org);
        await using var update = new NpgsqlCommand("UPDATE stock_movements SET quantity = 500", conn);
        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        await using var delete = new NpgsqlCommand("DELETE FROM stock_movements", conn);
        await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Only_the_storekeepers_move_stock_but_technicians_can_see_what_is_there()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync("/stock/receipts", new Receive(item.Id, 5, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Office.Client.PostAsJsonAsync("/stock/receipts", new Receive(item.Id, 5, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync("/stock/counts", new SaveCount(null, [new CountLineInput(item.Id, 1, "x")]))).StatusCode);
        Assert.Equal(item.Id, (await Level(s, item.Id, s.Technician)).ItemId);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync($"/stock/items/{item.Id}/movements")).StatusCode);
    }

    // JC-07 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Stock_is_issued_to_a_job_returned_and_reconciled_against_what_was_used()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s, "Elbow joint", "pcs", 50);
        var job = await StartedJob(s);
        await Receive(s, item.Id, 20);

        Assert.Equal(HttpStatusCode.NotFound, (await s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(Guid.NewGuid(), item.Id, 5, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(job.Id, item.Id, 10, "For the main line"))).StatusCode);
        Assert.Equal(10, (await Level(s, item.Id)).OnHand);

        var over = await s.Supervisor.Client.PostAsJsonAsync("/stock/returns", new IssueOrReturn(job.Id, item.Id, 11, null));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("more_than_issued", await over.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await s.Supervisor.Client.PostAsJsonAsync("/stock/returns", new IssueOrReturn(job.Id, item.Id, 3, "Not needed"))).StatusCode);
        Assert.Equal(13, (await Level(s, item.Id)).OnHand);

        // 10 issued, 3 returned, so 7 used: recorded as 7 the books agree.
        await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new CostLineInput("material", item.Id, null, 7, null, null)]));
        var report = (await s.Supervisor.Client.GetFromJsonAsync<List<MaterialLine>>($"/stock/jobs/{job.Id}/materials"))!;
        var line = Assert.Single(report);
        Assert.Equal((10m, 3m, 7m, 0m, false), (line.Issued, line.Returned, line.Used, line.Unexplained, line.Flagged));
    }

    [Fact]
    public async Task A_job_cannot_draw_stock_once_it_is_over_and_unexplained_materials_are_flagged_at_completion()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s, "Valve", "pcs", 200);
        var job = await StartedJob(s);
        await Receive(s, item.Id, 10);
        await s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(job.Id, item.Id, 6, null));
        // the technician says only 4 were used and returns nothing: 2 are unexplained
        await s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new CostLineInput("material", item.Id, null, 4, null, null)]));
        await s.SetEvidenceAsync("none");
        (await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/complete", new CompleteJob("Done"))).EnsureSuccessStatusCode();

        var line = Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<MaterialLine>>($"/stock/jobs/{job.Id}/materials"))!);
        Assert.Equal((2m, true), (line.Unexplained, line.Flagged));
        var timeline = (await s.Supervisor.Client.GetFromJsonAsync<JobDetailDto>($"/jobs/{job.Id}"))!.Timeline.Where(t => t.Kind == "materials").Select(t => t.Note).ToList();
        Assert.Contains(timeline, n => n!.Contains("do not add up") && n.Contains("issued 6"));

        await Drain();
        var inbox = await s.Supervisor.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.NotificationEndpoints.InboxDto>("/notifications");
        Assert.Contains(inbox!.Items, n => n.Template == "materials_mismatch" && n.Body.Contains(job.Number));

        // finished job: nothing more can be issued to it
        await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/sign-off", null);
        var late = await s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(job.Id, item.Id, 1, null));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Contains("job_not_active", await late.Content.ReadAsStringAsync());
    }

    // JC-08 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_low_stock_alert_fires_once_when_stock_reaches_the_level_and_again_only_after_a_recovery()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s, "Washer", "pcs", 2);
        var job = await StartedJob(s);
        await Receive(s, item.Id, 20);
        Assert.Equal(HttpStatusCode.OK, (await s.Supervisor.Client.PutAsJsonAsync($"/stock/items/{item.Id}/threshold", new Threshold(5))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PutAsJsonAsync($"/stock/items/{item.Id}/threshold", new Threshold(-1))).StatusCode);

        async Task<int> Alerts() { await Drain(); return (await s.Supervisor.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.NotificationEndpoints.InboxDto>("/notifications"))!.Items.Count(n => n.Template == "stock_low"); }
        async Task Issue(decimal q) => (await s.Supervisor.Client.PostAsJsonAsync("/stock/issues", new IssueOrReturn(job.Id, item.Id, q, null))).EnsureSuccessStatusCode();

        await Issue(10);                    // 10 left: fine
        Assert.Equal(0, await Alerts());
        Assert.False((await Level(s, item.Id)).Low);
        await Issue(5);                     // 5 left: reaches the level
        Assert.Equal(1, await Alerts());
        Assert.True((await Level(s, item.Id)).Low);
        await Issue(2);                     // 3 left: still low, no second alert
        Assert.Equal(1, await Alerts());

        await Receive(s, item.Id, 30);      // recovered
        Assert.False((await Level(s, item.Id)).Low);
        await Issue(30);                    // down to 3 again: a new alert
        Assert.Equal(2, await Alerts());
        Assert.Contains("Washer", (await s.Supervisor.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.NotificationEndpoints.InboxDto>("/notifications"))!.Items.First(n => n.Template == "stock_low").Body);
    }

    // JC-09 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_physical_count_turns_every_difference_into_an_adjustment_with_its_reason_all_or_nothing()
    {
        var s = await Society.CreateAsync(app);
        var a = await NewItem(s, "Cement bag", "bag", 900);
        var b = await NewItem(s, "Sand", "ton", 3000);
        await Receive(s, a.Id, 40);
        await Receive(s, b.Id, 10);

        // a difference with no reason: nothing is recorded
        var missing = await s.Supervisor.Client.PostAsJsonAsync("/stock/counts", new SaveCount("Monthly", [new CountLineInput(a.Id, 38, null), new CountLineInput(b.Id, 10, null)]));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("reason_required", await missing.Content.ReadAsStringAsync());
        Assert.Equal(40, (await Level(s, a.Id)).OnHand);
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<List<CountDto>>("/stock/counts"))!);

        var counted = await s.Supervisor.Client.PostAsJsonAsync("/stock/counts", new SaveCount("Monthly", [new CountLineInput(a.Id, 38, "Two bags set hard"), new CountLineInput(b.Id, 10, null)]));
        Assert.Equal(HttpStatusCode.Created, counted.StatusCode);
        var count = (await counted.Content.ReadFromJsonAsync<CountDto>())!;
        Assert.Equal([(a.Id, 40m, 38m, -2m), (b.Id, 10m, 10m, 0m)], count.Lines.OrderBy(l => l.ItemId == a.Id ? 0 : 1).Select(l => (l.ItemId, l.Expected, l.Counted, l.Difference)));

        Assert.Equal(38, (await Level(s, a.Id)).OnHand);
        Assert.Equal(10, (await Level(s, b.Id)).OnHand);
        var moves = (await s.Supervisor.Client.GetFromJsonAsync<List<MovementDto>>($"/stock/items/{a.Id}/movements"))!;
        Assert.Contains(moves, m => m.Kind == "adjust" && m.Quantity == -2 && m.Reason.Contains("Two bags set hard"));
        Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<CountDto>>("/stock/counts"))!);

        // a count is audited
        var audit = await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.AuditDto>>("/audit?entityType=StockCount&limit=20");
        Assert.NotEmpty(audit!);
    }

    [Fact]
    public async Task A_count_with_no_lines_or_the_same_item_twice_or_an_unknown_item_is_refused()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync("/stock/counts", new SaveCount(null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync("/stock/counts", new SaveCount(null, [new CountLineInput(item.Id, 0, null), new CountLineInput(item.Id, 0, null)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync("/stock/counts", new SaveCount(null, [new CountLineInput(Guid.NewGuid(), 0, null)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync("/stock/counts", new SaveCount(null, [new CountLineInput(item.Id, -1, "x")]))).StatusCode);
    }

    // TA-09 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_technician_asks_for_materials_the_storekeeper_issues_them_and_they_appear_on_the_job()
    {
        var s = await Society.CreateAsync(app);
        var pipe = await NewItem(s, "PVC pipe", "m", 120);
        var glue = await NewItem(s, "Glue", "tin", 300);
        var job = await StartedJob(s);
        await Receive(s, pipe.Id, 30);
        await Receive(s, glue.Id, 5);

        var asked = await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, "Burst main", [new RequestLineInput(pipe.Id, 6), new RequestLineInput(glue.Id, 1)]));
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var request = (await asked.Content.ReadFromJsonAsync<RequestDto>())!;
        Assert.Equal(("Open", job.Number, 2), (request.Status, request.JobNumber, request.Lines.Count));
        Assert.Equal(30, request.Lines.Single(l => l.ItemId == pipe.Id).OnHand);

        // the storekeeper sees it (and was told); the technician sees only their own
        await Drain();
        Assert.Contains((await s.Supervisor.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.NotificationEndpoints.InboxDto>("/notifications"))!.Items, n => n.Template == "stock_requested");
        Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<RequestDto>>("/stock/requests?status=Open"))!, r => r.Id == request.Id);
        Assert.Single((await s.Technician.Client.GetFromJsonAsync<List<RequestDto>>("/stock/requests"))!, r => r.Id == request.Id);

        var done = await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/fulfil", new Fulfil(null));
        Assert.Equal("Fulfilled", (await done.Content.ReadFromJsonAsync<RequestDto>())!.Status);
        Assert.Equal(24, (await Level(s, pipe.Id)).OnHand);
        Assert.Equal(4, (await Level(s, glue.Id)).OnHand);

        var materials = (await s.Technician.Client.GetFromJsonAsync<List<MaterialLine>>($"/stock/jobs/{job.Id}/materials"))!;
        Assert.Equal([6m, 1m], materials.OrderBy(m => m.Item == pipe.Name ? 0 : 1).Select(m => m.Issued));
        Assert.Contains((await s.Technician.Client.GetFromJsonAsync<JobDetailDto>($"/jobs/{job.Id}"))!.Timeline, t => t.Kind == "materials" && t.Note!.Contains("issued from the store"));

        await Drain();
        Assert.Contains((await s.Technician.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.NotificationEndpoints.InboxDto>("/notifications"))!.Items, n => n.Template == "stock_issued");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/fulfil", new Fulfil(null))).StatusCode); // only once
    }

    [Fact]
    public async Task A_request_that_cannot_be_met_issues_nothing_and_a_partial_amount_can_be_issued_instead()
    {
        var s = await Society.CreateAsync(app);
        var pipe = await NewItem(s, "Steel pipe", "m", 120);
        var glue = await NewItem(s, "Epoxy", "tin", 300);
        var job = await StartedJob(s);
        await Receive(s, pipe.Id, 10);
        await Receive(s, glue.Id, 1);
        var request = (await (await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(pipe.Id, 8), new RequestLineInput(glue.Id, 3)]))).Content.ReadFromJsonAsync<RequestDto>())!;

        var short1 = await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/fulfil", new Fulfil(null));
        Assert.Equal(HttpStatusCode.Conflict, short1.StatusCode);
        Assert.Contains("insufficient_stock", await short1.Content.ReadAsStringAsync());
        Assert.Equal(10, (await Level(s, pipe.Id)).OnHand); // the pipe that could have been issued was not
        Assert.Equal("Open", (await s.Supervisor.Client.GetFromJsonAsync<List<RequestDto>>("/stock/requests"))!.Single(r => r.Id == request.Id).Status);

        var partial = await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/fulfil", new Fulfil([new RequestLineInput(pipe.Id, 8), new RequestLineInput(glue.Id, 1)]));
        Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        Assert.Equal((2, 0), ((await Level(s, pipe.Id)).OnHand, (await Level(s, glue.Id)).OnHand));
        var foreign = await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/fulfil", new Fulfil([new RequestLineInput(Guid.NewGuid(), 1)]));
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode); // already done
    }

    [Fact]
    public async Task A_request_can_be_refused_with_a_reason_or_withdrawn_and_only_for_your_own_active_job()
    {
        var s = await Society.CreateAsync(app);
        var item = await NewItem(s);
        var job = await StartedJob(s);
        var request = (await (await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(item.Id, 2)]))).Content.ReadFromJsonAsync<RequestDto>())!;

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/reject", new Reason(" "))).StatusCode);
        var rejected = await s.Supervisor.Client.PostAsJsonAsync($"/stock/requests/{request.Id}/reject", new Reason("Out of stock until Monday"));
        Assert.Equal(("Rejected", "Out of stock until Monday"), ((await rejected.Content.ReadFromJsonAsync<RequestDto>())!.Status, (await s.Supervisor.Client.GetFromJsonAsync<List<RequestDto>>("/stock/requests"))!.Single(r => r.Id == request.Id).DecisionNote));
        await Drain();
        Assert.Contains((await s.Technician.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.NotificationEndpoints.InboxDto>("/notifications"))!.Items, n => n.Template == "stock_refused" && n.Body.Contains("Out of stock until Monday"));

        var second = (await (await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(item.Id, 1)]))).Content.ReadFromJsonAsync<RequestDto>())!;
        Assert.Equal("Cancelled", (await (await s.Technician.Client.PostAsync($"/stock/requests/{second.Id}/cancel", null)).Content.ReadFromJsonAsync<RequestDto>())!.Status);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Technician.Client.PostAsync($"/stock/requests/{second.Id}/cancel", null)).StatusCode);

        // not their job, bad lines, unknown item
        var other = await app.AddMemberAsync(s.Manager, s.Org, Role.Technician);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(item.Id, 1)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, []))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(item.Id, 0)]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Technician.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(Guid.NewGuid(), 1)]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsJsonAsync("/stock/requests", new SaveRequest(job.Id, null, [new RequestLineInput(item.Id, 1)]))).StatusCode);
    }
}
