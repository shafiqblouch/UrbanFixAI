using System.Diagnostics;
using System.Net;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.DashboardWidgets;
using static UrbanFix.Api.Endpoints.JobEndpoints;

namespace UrbanFix.Tests;

public class TrendPeriodTests
{
    [Theory]
    [InlineData("2026-10-21", "week", "2026-10-19")] // a Wednesday: the week starts on Monday
    [InlineData("2026-10-19", "week", "2026-10-19")]
    [InlineData("2026-10-25", "week", "2026-10-19")] // Sunday belongs to the week before it
    [InlineData("2026-10-26", "week", "2026-10-26")]
    [InlineData("2026-10-31", "month", "2026-10-01")]
    [InlineData("2026-01-01", "month", "2026-01-01")]
    public void A_date_falls_in_the_week_or_month_it_belongs_to(string day, string granularity, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), PeriodStart(DateOnly.Parse(day), granularity));
}

[Collection("db")]
public class DashboardWidgetTests(TestApp app)
{
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<CreatedDto> Complain(Society s, string text = "No water")
        => await Ok<CreatedDto>(s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, s.CategoryId, s.HouseId, null, null, null)));

    // DB-02 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Complaints_are_counted_per_week_and_per_month_with_empty_periods_shown_as_zero()
    {
        var s = await Society.CreateAsync(app);
        var a = await Complain(s); var b = await Complain(s); var c = await Complain(s);
        // Two last week (one of them resolved this week), one three weeks ago.
        await Sql("UPDATE complaints SET created_at = now() - interval '7 days' WHERE id = ANY(@ids)", ("ids", new[] { a.Id, b.Id }));
        await Sql("UPDATE complaints SET created_at = now() - interval '21 days', resolved_at = now() WHERE id = @id", ("id", c.Id));
        await Sql("UPDATE complaints SET resolved_at = now() WHERE id = @id", ("id", a.Id));

        var weekly = await Ok<TrendReport>(s.Manager.Client.GetAsync("/dashboard/trends?granularity=week&periods=6"));
        Assert.Equal(6, weekly.Periods.Count);
        Assert.All(weekly.Periods, d => Assert.Equal(DayOfWeek.Monday, d.DayOfWeek));
        var all = Assert.Single(weekly.Series);
        Assert.Equal(6, all.Created.Count);
        Assert.Equal(3, all.Created.Sum());
        Assert.Equal(2, all.Resolved.Sum());
        Assert.Equal(2, all.Created.Count(n => n > 0)); // two busy weeks, the rest zero
        Assert.Equal(2, all.Resolved[^1]);              // both resolved just now: the current week

        var monthly = await Ok<TrendReport>(s.Manager.Client.GetAsync("/dashboard/trends?granularity=month&periods=3"));
        Assert.Equal(3, monthly.Periods.Count);
        Assert.All(monthly.Periods, d => Assert.Equal(1, d.Day));
        Assert.Equal(3, monthly.Series[0].Created.Sum());
    }

    [Fact]
    public async Task Trends_can_be_split_by_category_and_sector()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s); await Complain(s);

        var byCategory = await Ok<TrendReport>(s.Manager.Client.GetAsync("/dashboard/trends?by=category"));
        Assert.Equal("all", byCategory.Series[0].Key);
        var water = Assert.Single(byCategory.Series, x => x.Key == s.CategoryId.ToString());
        Assert.Equal("Water supply", water.Name);
        Assert.Equal(2, water.Created.Sum());

        var bySector = await Ok<TrendReport>(s.Manager.Client.GetAsync("/dashboard/trends?by=sector"));
        Assert.Equal(2, bySector.Series.Skip(1).Sum(x => x.Created.Sum()));
    }

    [Fact]
    public async Task Bad_trend_requests_are_refused_and_only_dashboard_readers_get_them()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.GetAsync("/dashboard/trends?granularity=day")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.GetAsync("/dashboard/trends?by=colour")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.GetAsync("/dashboard/trends")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/dashboard/trends")).StatusCode);
    }

    // DB-07 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_spend_widget_shows_the_same_figures_as_the_cost_report_and_lists_what_needs_attention()
    {
        var s = await Society.CreateAsync(app);
        var created = await Complain(s);
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Fix", null)));
        await Ok<EstimateDto>(s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/estimates",
            new SaveEstimate(null, [new CostLineInput("labour", null, "Fitter", 1, "job", 1000m)])));
        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId));
        await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null);
        await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/start", new StartWork(null, null, null));
        await Ok<CostsResult>(s.Technician.Client.PutAsJsonAsync($"/jobs/{job.Id}/costs", new SaveCosts([new CostLineInput("labour", null, "Fitter", 1, "job", 1500m)])));

        var widget = await Ok<SpendWidget>(s.Manager.Client.GetAsync("/dashboard/spend"));
        var report = await Ok<CostReportEndpoints.CostReport>(s.Manager.Client.GetAsync($"/reports/costs?groupBy=category&from={Uri.EscapeDataString(widget.From.ToString("o"))}&to={Uri.EscapeDataString(widget.To.ToString("o"))}"));
        Assert.Equal(report.Actual, widget.Actual);
        Assert.Equal(report.Estimated, widget.Estimated);
        Assert.Equal(report.Jobs, widget.Jobs);
        Assert.Equal(1500m, widget.Actual);
        Assert.Equal(1000m, widget.Estimated);

        // 50% over a 20% threshold: flagged.
        Assert.Equal(job.Number, Assert.Single(widget.Overruns).Number);
        Assert.Null(widget.ChargesWaiting); // managers do not review charges
        var accounts = await app.AddMemberAsync(s.Manager, s.Org, Role.Accounts);
        Assert.Equal(0, (await Ok<SpendWidget>(accounts.Client.GetAsync("/dashboard/spend"))).ChargesWaiting);
    }

    [Fact]
    public async Task Low_stock_appears_in_the_widget_once_stock_is_at_or_below_the_alert_level()
    {
        var s = await Society.CreateAsync(app);
        var item = await Ok<PricingEndpoints.PriceItemDto>(s.Manager.Client.PostAsJsonAsync("/price-items", new PricingEndpoints.CreatePriceItem("Cement bag", null, "bag", 900m, null)));
        await s.Manager.Client.PostAsJsonAsync("/stock/receipts", new StockEndpoints.Receive(item.Id, 10m, null));
        await s.Manager.Client.PutAsJsonAsync($"/stock/items/{item.Id}/threshold", new StockEndpoints.Threshold(4m));
        Assert.Empty((await Ok<SpendWidget>(s.Manager.Client.GetAsync("/dashboard/spend"))).LowStock);

        await s.Manager.Client.PostAsJsonAsync("/stock/adjustments", new StockEndpoints.Adjust(item.Id, -7m, "Wet bags thrown away"));
        var low = Assert.Single((await Ok<SpendWidget>(s.Manager.Client.GetAsync("/dashboard/spend"))).LowStock);
        Assert.Equal((item.Id, 3m, 4m), (low.ItemId, low.OnHand, low.Threshold));
    }

    [Fact]
    public async Task Money_widgets_need_the_cost_permission_and_counts_need_only_the_dashboard_one()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Office.Client.GetAsync("/dashboard/spend")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Office.Client.GetAsync("/reports/costs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Office.Client.GetAsync("/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Supervisor.Client.GetAsync("/dashboard/spend")).StatusCode);
    }

    // DB-08 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Each_role_lands_on_its_own_view()
    {
        var s = await Society.CreateAsync(app);
        var accounts = await app.AddMemberAsync(s.Manager, s.Org, Role.Accounts);
        var committee = await app.AddMemberAsync(s.Manager, s.Org, Role.Committee);

        async Task<Layout> LayoutOf(ApiSession who) => await Ok<Layout>(who.Client.GetAsync("/dashboard/layout"));
        var manager = await LayoutOf(s.Manager);
        var supervisor = await LayoutOf(s.Supervisor);
        var accountant = await LayoutOf(accounts);
        var office = await LayoutOf(s.Office);

        Assert.Equal(("Manager", "Supervisor", "Accounts", "Office"), (manager.View, supervisor.View, accountant.View, office.View));
        Assert.Equal("Committee", (await LayoutOf(committee)).View);
        Assert.Contains("trends", manager.Widgets);
        Assert.Contains("spend", manager.Widgets);
        Assert.Contains("workload", supervisor.Widgets);
        Assert.DoesNotContain("spend", supervisor.Widgets.Where(w => w == "charges"));
        Assert.Equal(["spend", "charges"], accountant.Widgets);
        Assert.DoesNotContain("spend", office.Widgets);
        Assert.DoesNotContain("trends", office.Widgets);

        var resident = await LayoutOf(s.Resident);
        Assert.Equal("none", resident.View);
        Assert.Empty(resident.Widgets);
    }

    [Fact]
    public void Every_view_names_only_widgets_the_portal_knows()
    {
        var known = new[] { "kpis", "trends", "overdue", "workload", "spend", "overruns", "lowStock", "satisfaction", "delivery", "recent", "charges" };
        Assert.All(Views.Values.SelectMany(v => v), w => Assert.Contains(w, known));
    }

    // DB-06 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_dashboard_stays_under_half_a_second_with_a_lot_of_complaints()
    {
        var s = await Society.CreateAsync(app);
        const int rows = 20_000;
        await Sql(@"
            INSERT INTO complaints (id, organization_id, reference, status, channel, description, category_id, house_id, location_id, priority, created_at, updated_at, resolved_at)
            SELECT gen_random_uuid(), @org, 'LOAD-' || n, (ARRAY['New','UnderReview','Assigned','InProgress','Resolved','Closed'])[1 + n % 6], 'app', 'Load test complaint ' || n,
                   @cat, @house, @loc, 'Normal', now() - (n % 400) * interval '1 day' - (n % 24) * interval '1 hour', now(),
                   CASE WHEN n % 6 >= 4 THEN now() - (n % 400) * interval '1 day' + interval '2 hours' END
            FROM generate_series(1, @rows) AS n",
            ("org", s.Org), ("cat", s.CategoryId), ("house", s.HouseId), ("loc", s.LocationId), ("rows", rows));

        string[] urls = ["/dashboard/summary", "/dashboard/trends?granularity=week&periods=26&by=category", "/dashboard/trends?granularity=month&periods=12&by=sector", "/dashboard/spend"];
        foreach (var url in urls) await s.Manager.Client.GetAsync(url); // warm up the connection and the plan cache

        var timings = new List<double>();
        for (var i = 0; i < 5; i++)
            foreach (var url in urls)
            {
                var clock = Stopwatch.StartNew();
                var res = await s.Manager.Client.GetAsync(url);
                clock.Stop();
                Assert.True(res.IsSuccessStatusCode, url);
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        Assert.True(p95 < 500, $"p95 was {p95:0} ms over {timings.Count} calls with {rows} complaints");

        var total = (await Ok<DashboardEndpoints.Summary>(s.Manager.Client.GetAsync("/dashboard/summary"))).Total;
        Assert.Equal(rows, total);
    }
}
