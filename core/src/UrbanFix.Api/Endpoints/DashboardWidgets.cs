using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using static UrbanFix.Api.Endpoints.CostReportEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;

namespace UrbanFix.Api.Endpoints;

/// <summary>Trends over time (DB-02), the spend, overrun and low-stock widgets (DB-07) and which widgets each role sees (DB-08).</summary>
public static class DashboardWidgets
{
    public sealed record TrendSeries(string Key, string Name, IReadOnlyList<int> Created, IReadOnlyList<int> Resolved);
    public sealed record TrendReport(string Granularity, string By, IReadOnlyList<DateOnly> Periods, IReadOnlyList<TrendSeries> Series);

    public sealed record LowStockItem(Guid ItemId, string Item, string Unit, decimal OnHand, decimal Threshold);
    /// <summary>The same figures as the cost report for the period, plus what needs attention. Charges are for those who may see them, otherwise null.</summary>
    public sealed record SpendWidget(
        DateTimeOffset From, DateTimeOffset To, int Jobs, decimal Estimated, decimal Actual, IReadOnlyList<CostRow> ByCategory,
        IReadOnlyList<OverrunDto> Overruns, IReadOnlyList<LowStockItem> LowStock, int? ChargesWaiting);

    public sealed record Layout(string View, IReadOnlyList<string> Widgets);

    public static readonly string[] Granularities = ["week", "month"];
    public static readonly string[] Groupings = ["none", "category", "sector"];

    /// <summary>Which widgets each role lands on (DB-08). The data behind each is still guarded by its own permission.</summary>
    public static readonly IReadOnlyDictionary<Role, string[]> Views = new Dictionary<Role, string[]>
    {
        [Role.Manager] = ["kpis", "trends", "overdue", "workload", "spend", "overruns", "lowStock", "satisfaction", "delivery", "recent"],
        [Role.Committee] = ["kpis", "trends", "spend", "overruns", "satisfaction", "recent"],
        [Role.Supervisor] = ["kpis", "overdue", "workload", "overruns", "lowStock", "recent"],
        [Role.Accounts] = ["spend", "charges"],
        [Role.Office] = ["kpis", "overdue", "recent"],
        // A platform person in a support session sees what a committee member would, read-only.
        [Role.Support] = ["kpis", "trends", "spend", "overruns", "satisfaction", "recent"],
    };

    /// <summary>When a person holds several roles, the first of these decides their view.</summary>
    private static readonly Role[] Precedence = [Role.Manager, Role.Committee, Role.Supervisor, Role.Accounts, Role.Office, Role.Support];

    public static void MapDashboardWidgets(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/dashboard").WithTags("Dashboard");

        g.MapGet("/layout", async (CurrentMemberships memberships, CancellationToken ct) =>
        {
            var roles = (await memberships.GetAsync(ct)).Select(m => m.Role).ToHashSet();
            var view = Precedence.Where(roles.Contains).Cast<Role?>().FirstOrDefault();
            return Results.Ok(view is { } v ? new Layout(v.ToString(), Views[v]) : new Layout("none", []));
        }).Produces<Layout>(200).RequireAuthorization(AuthPolicies.Verified);

        g.MapGet("/trends", TrendsAsync).Produces<TrendReport>(200).RequireAuthorization(AuthPolicies.For(Permissions.DashboardRead));
        g.MapGet("/spend", SpendAsync).Produces<SpendWidget>(200).RequireAuthorization(AuthPolicies.For(Permissions.CostRead));
    }

    // Trends -----------------------------------------------------------------------------------------------------

    /// <summary>The first day of the week (Monday) or month a date falls in.</summary>
    public static DateOnly PeriodStart(DateOnly day, string granularity) =>
        granularity == "month" ? new DateOnly(day.Year, day.Month, 1) : day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static DateOnly NextPeriod(DateOnly start, string granularity) => granularity == "month" ? start.AddMonths(1) : start.AddDays(7);

    private static async Task<IResult> TrendsAsync(string? granularity, string? by, int? periods, AppDbContext db, CancellationToken ct)
    {
        var step = granularity ?? "week";
        var grouping = by ?? "none";
        if (!Granularities.Contains(step)) return Results.BadRequest(new { error = "invalid_granularity" });
        if (!Groupings.Contains(grouping)) return Results.BadRequest(new { error = "invalid_grouping" });
        var count = Math.Clamp(periods ?? 12, 1, 60);

        var zoneId = await db.OrgSettings.AsNoTracking().Select(s => s.Timezone).SingleOrDefaultAsync(ct) ?? "Asia/Karachi";
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var found) ? found : TimeZoneInfo.Utc;
        DateOnly Local(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

        // The last `count` periods ending with the current one.
        var starts = new List<DateOnly> { PeriodStart(Local(DateTimeOffset.UtcNow), step) };
        while (starts.Count < count) starts.Insert(0, step == "month" ? starts[0].AddMonths(-1) : starts[0].AddDays(-7));
        var index = starts.Select((d, i) => (d, i)).ToDictionary(x => x.d, x => x.i);
        var first = TimeZoneInfo.ConvertTimeToUtc(starts[0].ToDateTime(TimeOnly.MinValue), zone);

        var rows = await db.Complaints.AsNoTracking()
            .Where(c => c.CreatedAt >= first || (c.ResolvedAt != null && c.ResolvedAt >= first))
            .Select(c => new { c.CategoryId, c.LocationId, c.CreatedAt, c.ResolvedAt }).ToListAsync(ct);

        Func<Guid?, Guid?, (string Key, string Name)> groupOf = (_, _) => ("all", "All");
        if (grouping == "category")
        {
            var names = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
            groupOf = (cat, _) => cat is { } c && names.TryGetValue(c, out var n) ? (c.ToString(), n) : ("none", "Not categorised");
        }
        else if (grouping == "sector")
        {
            var sectors = new SectorLookup(await db.Locations.AsNoTracking().Select(l => new SectorLookup.Place(l.Id, l.Path, l.Kind, l.Name)).ToListAsync(ct));
            groupOf = (_, loc) => sectors.Of(loc);
        }

        var all = (Created: new int[count], Resolved: new int[count]);
        var groups = new Dictionary<string, (string Name, int[] Created, int[] Resolved)>();
        void Add(Guid? cat, Guid? loc, DateTimeOffset at, bool resolved)
        {
            if (!index.TryGetValue(PeriodStart(Local(at), step), out var i)) return;
            (resolved ? all.Resolved : all.Created)[i]++;
            if (grouping == "none") return;
            var (key, name) = groupOf(cat, loc);
            if (!groups.TryGetValue(key, out var series)) groups[key] = series = (name, new int[count], new int[count]);
            (resolved ? series.Resolved : series.Created)[i]++;
        }
        foreach (var r in rows)
        {
            Add(r.CategoryId, r.LocationId, r.CreatedAt, false);
            if (r.ResolvedAt is { } done) Add(r.CategoryId, r.LocationId, done, true);
        }

        var series = new List<TrendSeries> { new("all", "All", all.Created, all.Resolved) };
        series.AddRange(groups.OrderByDescending(x => x.Value.Created.Sum()).ThenBy(x => x.Value.Name).Select(x => new TrendSeries(x.Key, x.Value.Name, x.Value.Created, x.Value.Resolved)));
        return Results.Ok(new TrendReport(step, grouping, starts, series));
    }

    // Spend, overruns, low stock ----------------------------------------------------------------------------------

    private static async Task<IResult> SpendAsync(
        DateTimeOffset? from, DateTimeOffset? to, ClaimsPrincipal user, AppDbContext db, StockService stock, CurrentMemberships memberships, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = to ?? now;
        var report = await BuildAsync(db, "category", start, end, ct);

        var onHand = await stock.OnHandAsync(ct);
        var items = await db.PriceItems.AsNoTracking().Where(i => i.IsActive).ToListAsync(ct);
        var thresholds = await db.StockSettings.AsNoTracking().ToDictionaryAsync(s => s.ItemId, s => s.LowThreshold, ct);
        var low = items.Where(i => thresholds.TryGetValue(i.Id, out var t) && t > 0 && onHand.GetValueOrDefault(i.Id) <= t)
            .Select(i => new LowStockItem(i.Id, i.Name, i.Unit, onHand.GetValueOrDefault(i.Id), thresholds[i.Id])).OrderBy(i => i.Item).ToList();

        int? charges = null;
        if ((await memberships.GetAsync(ct)).Any(m => RolePermissions.Allows(m.Role, Permissions.BillManage)))
            charges = await db.JobCharges.AsNoTracking().CountAsync(c => c.Status == ChargeStatus.Pending, ct);

        return Results.Ok(new SpendWidget(start, end, report.Jobs, report.Estimated, report.Actual, report.Rows, await OverrunListAsync(db, ct), low, charges));
    }
}
