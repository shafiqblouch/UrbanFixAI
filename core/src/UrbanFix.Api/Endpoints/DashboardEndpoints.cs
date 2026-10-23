using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Aggregation queries behind the dashboards (DB-01). All figures are for one organization; row-level security does the scoping.</summary>
public static class DashboardEndpoints
{
    public sealed record CountRow(string Key, string Name, int Count);
    public sealed record OverdueJob(Guid JobId, string Number, string Reference, string Priority, string Status, Guid? TeamId, double HoursOverdue);
    public sealed record WorkloadRow(Guid? TeamId, string Team, int Open, int InProgress, int PendingVerification);
    public sealed record Summary(
        int Total, IReadOnlyList<CountRow> ByStatus, IReadOnlyList<CountRow> ByDepartment, IReadOnlyList<CountRow> BySector,
        IReadOnlyList<OverdueJob> OverdueJobs, IReadOnlyList<WorkloadRow> TeamWorkload,
        double? AverageResolutionHours, int ResolvedCount, int ReopenedComplaints, int ReopenEvents,
        double? AverageRating = null, int RatingCount = 0, int ConfirmedFixedCount = 0);

    public static void MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dashboard/summary", SummaryAsync).WithTags("Dashboard").Produces<Summary>(200).RequireAuthorization(AuthPolicies.For(Permissions.DashboardRead));
        app.MapDashboardWidgets();
    }

    private static async Task<IResult> SummaryAsync(DateTimeOffset? from, DateTimeOffset? to, AppDbContext db, CancellationToken ct) =>
        Results.Ok(await BuildSummaryAsync(db, from, to, ct));

    /// <summary>The summary itself; the exports and the monthly summary use it too, so every report agrees with the dashboard.</summary>
    internal static async Task<Summary> BuildSummaryAsync(AppDbContext db, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var complaints = db.Complaints.AsNoTracking().AsQueryable();
        if (from is { } f) complaints = complaints.Where(c => c.CreatedAt >= f);
        if (to is { } t) complaints = complaints.Where(c => c.CreatedAt <= t);

        var rows = await complaints.Select(c => new { c.Id, c.Status, c.CategoryId, c.LocationId, c.CreatedAt, c.ResolvedAt }).ToListAsync(ct);

        var byStatus = Enum.GetValues<ComplaintStatus>()
            .Select(s => new CountRow(s.ToString(), s.ToString(), rows.Count(r => r.Status == s))).ToList();

        var categories = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.DepartmentId }).ToDictionaryAsync(c => c.Id, c => c.DepartmentId, ct);
        var departments = await db.Departments.AsNoTracking().ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var byDepartment = rows
            .GroupBy(r => r.CategoryId is { } cid && categories.TryGetValue(cid, out var dept) ? dept : null)
            .Select(g => new CountRow(g.Key?.ToString() ?? "none", g.Key is { } d && departments.TryGetValue(d, out var n) ? n : "Unassigned", g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList();

        // A sector is the nearest ancestor of kind "sector"; locations without one count under their top-level ancestor.
        var sectors = new SectorLookup(await db.Locations.AsNoTracking().Select(l => new SectorLookup.Place(l.Id, l.Path, l.Kind, l.Name)).ToListAsync(ct));
        var bySector = rows.GroupBy(r => sectors.Of(r.LocationId).Key)
            .Select(g => new CountRow(g.Key, sectors.Of(g.First().LocationId).Name, g.Count()))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToList();

        // Jobs: overdue and workload cover every active job, whatever range the complaint counts use.
        var active = await (from j in db.Jobs.AsNoTracking()
                            join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                            where j.Status == JobStatus.Open || j.Status == JobStatus.Accepted || j.Status == JobStatus.InProgress || j.Status == JobStatus.PendingVerification
                            select new { j.Id, j.Number, j.Status, j.AssignedTeamId, c.Reference, c.Priority, c.CreatedAt }).ToListAsync(ct);
        var overdue = active.Where(j => Sla.IsOverdue(j.Priority, j.CreatedAt, now))
            .Select(j => new OverdueJob(j.Id, j.Number, j.Reference, j.Priority, j.Status, j.AssignedTeamId, Math.Round((now - j.CreatedAt - Sla.For(j.Priority)).TotalHours, 1)))
            .OrderByDescending(j => j.HoursOverdue).ToList();

        var teams = await db.Teams.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        var workload = active.GroupBy(j => j.AssignedTeamId)
            .Select(g => new WorkloadRow(g.Key, g.Key is { } tid && teams.TryGetValue(tid, out var name) ? name : "No team",
                g.Count(j => j.Status is JobStatus.Open or JobStatus.Accepted), g.Count(j => j.Status == JobStatus.InProgress), g.Count(j => j.Status == JobStatus.PendingVerification)))
            .OrderBy(w => w.Team).ToList();

        var resolved = rows.Where(r => r.ResolvedAt != null).ToList();
        double? average = resolved.Count == 0 ? null : Math.Round(resolved.Average(r => (r.ResolvedAt!.Value - r.CreatedAt).TotalHours), 2);

        var ids = rows.Select(r => r.Id).ToHashSet();
        var reopens = (await db.ComplaintHistory.AsNoTracking()
            .Where(h => h.ToStatus == ComplaintStatus.UnderReview && (h.FromStatus == ComplaintStatus.Resolved || h.FromStatus == ComplaintStatus.Closed))
            .Select(h => h.ComplaintId).ToListAsync(ct)).Where(ids.Contains).ToList();

        var ratings = (await db.Feedback.AsNoTracking().Select(f => new { f.ComplaintId, f.Rating, f.ConfirmedFixed }).ToListAsync(ct)).Where(f => ids.Contains(f.ComplaintId)).ToList();

        return new Summary(rows.Count, byStatus, byDepartment, bySector, overdue, workload, average, resolved.Count, reopens.Distinct().Count(), reopens.Count,
            ratings.Count == 0 ? null : Math.Round(ratings.Average(r => r.Rating), 2), ratings.Count, ratings.Count(r => r.ConfirmedFixed));
    }
}
