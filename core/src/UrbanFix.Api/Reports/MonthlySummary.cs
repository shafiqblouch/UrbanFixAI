using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Pdf;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Reports;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Reports;

/// <summary>Makes the summary for a month, keeps the PDF, and tells the recipients (DB-05).</summary>
public sealed class MonthlySummaryService(AppDbContext db, IPdfRenderer pdf, IObjectStorage storage, IEventBus bus)
{
    /// <summary>The first and last day of the month before the one that <paramref name="today"/> falls in.</summary>
    public static (DateOnly Start, DateOnly End) PreviousMonth(DateOnly today)
    {
        var start = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        return (start, start.AddMonths(1).AddDays(-1));
    }

    /// <returns>The report (new or the one already made for that month), or null when it cannot be made (no recipients).</returns>
    public async Task<(SummaryReport? Report, bool Created)> GenerateAsync(DateOnly start, Guid? by, CancellationToken ct)
    {
        var existing = await db.SummaryReports.AsNoTracking().SingleOrDefaultAsync(r => r.PeriodStart == start, ct);
        if (existing is not null) return (existing, false);

        var schedule = await db.SummarySchedules.AsNoTracking().SingleOrDefaultAsync(ct);
        var recipients = (schedule?.RecipientUserIds ?? []).ToList();
        if (recipients.Count == 0) return (null, false);

        var end = start.AddMonths(1).AddDays(-1);
        var language = schedule!.Language ?? await db.OrgSettings.AsNoTracking().Select(s => s.Locale).SingleOrDefaultAsync(ct) ?? "en";
        var roles = (await db.Memberships.AsNoTracking().Where(m => m.IsActive && recipients.Contains(m.UserId)).Select(m => new { m.UserId, m.Role }).ToListAsync(ct))
            .GroupBy(m => m.UserId).ToDictionary(g => g.Key, g => g.Select(m => m.Role).ToList());
        // Costs go in only if every recipient may read them: the file is one document for all.
        var costs = recipients.All(u => roles.TryGetValue(u, out var r) && r.Any(role => RolePermissions.Allows(role, Permissions.CostRead)));

        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(end.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.Zero);
        var name = await db.OrgSettings.AsNoTracking().Select(s => s.DisplayName).SingleOrDefaultAsync(ct) ?? await db.Organizations.AsNoTracking().Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";
        var report = await SummaryReportBuilder.BuildAsync(db, name, language, from, to, costs, DateTimeOffset.UtcNow, ct);
        var bytes = pdf.Render(report);

        await using var stream = new MemoryStream(bytes);
        var stored = await storage.PutAsync(stream, "application/pdf", ".pdf", ct);
        var row = new SummaryReport
        {
            OrganizationId = schedule.OrganizationId, PeriodStart = start, PeriodEnd = end, Language = language, IncludesCosts = costs,
            StorageKey = stored.Key, SizeBytes = bytes.Length, RecipientUserIds = [.. recipients], CreatedBy = by,
        };
        db.SummaryReports.Add(row);
        bus.Publish(new MonthlySummaryReady(row.OrganizationId, row.Id, start.ToString("yyyy-MM"), recipients));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // Another run made it first (the one-per-month rule): use theirs. Anything else is a real failure.
            db.ChangeTracker.Clear();
            var theirs = await db.SummaryReports.AsNoTracking().SingleOrDefaultAsync(r => r.PeriodStart == start, ct);
            if (theirs is null) throw;
            return (theirs, false);
        }
        return (row, true);
    }

    /// <summary>Makes last month's summary if today is the day the schedule asks for and it has not been made yet.</summary>
    public async Task<bool> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var schedule = await db.SummarySchedules.AsNoTracking().SingleOrDefaultAsync(ct);
        if (schedule is not { Enabled: true } || schedule.RecipientUserIds.Length == 0) return false;
        var zoneId = await db.OrgSettings.AsNoTracking().Select(s => s.Timezone).SingleOrDefaultAsync(ct) ?? "Asia/Karachi";
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var z) ? z : TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (today.Day < schedule.DayOfMonth) return false;
        var (start, _) = PreviousMonth(today);
        return (await GenerateAsync(start, null, ct)).Created;
    }
}

/// <summary>Checks every society once a day for a monthly summary that is due.</summary>
public sealed class MonthlySummaryJob(IServiceScopeFactory scopes, Microsoft.Extensions.Configuration.IConfiguration config, IJobFailureNotifier failures)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var made = 0;
        var errors = new List<Exception>();
        foreach (var org in await OrganizationList.ReadAsync(config, ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(org, null);
                if (await scope.ServiceProvider.GetRequiredService<MonthlySummaryService>().RunDueAsync(DateTimeOffset.UtcNow, ct)) made++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                failures.JobFailed("monthly-summary", $"MonthlySummaryJob (organization {org})", ex.Message);
            }
        }
        if (errors.Count > 0) throw new AggregateException("Monthly summary failed", errors);
        return made;
    }
}

/// <summary>Tells the recipients their summary is ready, in their own language (DB-05).</summary>
public sealed class MonthlySummaryNotifications(NotificationService notifications) : IEventHandler<MonthlySummaryReady>
{
    public Task Handle(MonthlySummaryReady e, CancellationToken ct) =>
        notifications.SendAsync(e.EventId, e.OrganizationId,
            e.Recipients.Select(u => new Recipient(u, NotificationTemplates.MonthlySummary, new Dictionary<string, string> { ["period"] = e.Period }, $"/reports/summaries/{e.ReportId}")), ct);
}

public static class MonthlySummaryEndpoints
{
    public sealed record ScheduleDto(bool Enabled, int DayOfMonth, string? Language, IReadOnlyList<Guid> RecipientUserIds);
    public sealed record SummaryDto(Guid Id, string Period, DateOnly PeriodStart, DateOnly PeriodEnd, string Language, bool IncludesCosts, long SizeBytes, int Recipients, DateTimeOffset CreatedAt, bool Scheduled);
    public sealed record MakeSummary(string Period);

    private static SummaryDto ToDto(SummaryReport r) => new(r.Id, r.PeriodStart.ToString("yyyy-MM"), r.PeriodStart, r.PeriodEnd, r.Language, r.IncludesCosts, r.SizeBytes, r.RecipientUserIds.Length, r.CreatedAt, r.CreatedBy is null);

    public static void MapMonthlySummaryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/reports").WithTags("Reports");
        var read = AuthPolicies.For(Permissions.DashboardRead);
        var manage = AuthPolicies.For(Permissions.OrganizationSettings);

        g.MapGet("/summary-schedule", async (AppDbContext db, CancellationToken ct) =>
        {
            var s = await db.SummarySchedules.AsNoTracking().SingleOrDefaultAsync(ct);
            return Results.Ok(s is null ? new ScheduleDto(false, 1, null, []) : new ScheduleDto(s.Enabled, s.DayOfMonth, s.Language, s.RecipientUserIds));
        }).Produces<ScheduleDto>(200).RequireAuthorization(manage);

        g.MapPut("/summary-schedule", async (ScheduleDto req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (req.DayOfMonth is < 1 or > 28) return Results.BadRequest(new { error = "day_out_of_range" });
            if (req.Language is not (null or "en" or "ur")) return Results.BadRequest(new { error = "unsupported_locale" });
            var ids = req.RecipientUserIds.Distinct().ToList();
            if (req.Enabled && ids.Count == 0) return Results.BadRequest(new { error = "recipients_required" });
            // Only people who can read the dashboards: the summary is a management document.
            var eligible = (await db.Memberships.AsNoTracking().Where(m => m.IsActive && ids.Contains(m.UserId)).Select(m => new { m.UserId, m.Role }).ToListAsync(ct))
                .Where(m => RolePermissions.Allows(m.Role, Permissions.DashboardRead)).Select(m => m.UserId).ToHashSet();
            if (ids.Any(i => !eligible.Contains(i))) return Results.BadRequest(new { error = "recipient_cannot_read_dashboards" });

            var s = await db.SummarySchedules.SingleOrDefaultAsync(ct);
            if (s is null) { s = new SummarySchedule { OrganizationId = tenant.OrganizationId!.Value }; db.SummarySchedules.Add(s); }
            s.Enabled = req.Enabled; s.DayOfMonth = req.DayOfMonth; s.Language = req.Language; s.RecipientUserIds = [.. ids]; s.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new ScheduleDto(s.Enabled, s.DayOfMonth, s.Language, s.RecipientUserIds));
        }).Produces<ScheduleDto>(200).RequireAuthorization(manage);

        g.MapGet("/summaries", async (CurrentMemberships memberships, AppDbContext db, CancellationToken ct) =>
        {
            var costs = (await memberships.GetAsync(ct)).Any(m => RolePermissions.Allows(m.Role, Permissions.CostRead));
            var rows = await db.SummaryReports.AsNoTracking().Where(r => costs || !r.IncludesCosts).OrderByDescending(r => r.PeriodStart).Take(60).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        }).Produces<List<SummaryDto>>(200).RequireAuthorization(read);

        g.MapGet("/summaries/{id:guid}/download", async (Guid id, CurrentMemberships memberships, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            var r = await db.SummaryReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (r is null) return Results.NotFound();
            if (r.IncludesCosts && !(await memberships.GetAsync(ct)).Any(m => RolePermissions.Allows(m.Role, Permissions.CostRead))) return Results.Forbid();
            return Results.File(await storage.OpenReadAsync(r.StorageKey, ct), "application/pdf", $"summary-{r.PeriodStart:yyyy-MM}.pdf");
        }).Produces(200).RequireAuthorization(read);

        // Make a month's summary now, for example the first time, or a month that was missed. A month already made is returned as it is.
        g.MapPost("/summaries", async (MakeSummary req, ClaimsPrincipal user, MonthlySummaryService service, CancellationToken ct) =>
        {
            if (!DateOnly.TryParseExact(req.Period + "-01", "yyyy-MM-dd", out var start)) return Results.BadRequest(new { error = "invalid_period" });
            if (start >= new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1)) return Results.BadRequest(new { error = "period_not_finished" });
            var (report, created) = await service.GenerateAsync(start, Guid.Parse(user.FindFirstValue("sub")!), ct);
            if (report is null) return Results.BadRequest(new { error = "recipients_required" });
            return created ? Results.Created($"/reports/summaries/{report.Id}/download", ToDto(report)) : Results.Ok(ToDto(report));
        }).Produces<SummaryDto>(200).RequireAuthorization(manage);
    }
}
