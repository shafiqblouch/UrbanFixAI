using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Reports;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Pdf;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Api.Endpoints;

/// <summary>
/// CSV and PDF downloads of the complaint list, the management summary and the cost report (DB-04). Each one is built from the same
/// query as the screen it comes from, so a person can only export what they can already see. Every export is written to the audit log.
/// </summary>
public static class ExportEndpoints
{
    public const int MaxRows = 5000;

    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/exports").WithTags("Exports");
        g.MapGet("/complaints", ComplaintsAsync).Produces(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapGet("/summary", SummaryAsync).Produces(200).RequireAuthorization(AuthPolicies.For(Permissions.DashboardRead));
        g.MapGet("/costs", CostsAsync).Produces(200).RequireAuthorization(AuthPolicies.For(Permissions.CostRead));
    }

    private static bool KnownFormat(string? format) => format is "csv" or "pdf";

    private static IResult File(byte[] bytes, string format, string name) =>
        Results.File(bytes, format == "pdf" ? "application/pdf" : "text/csv; charset=utf-8", $"{name}-{DateTime.UtcNow:yyyy-MM-dd}.{format}");

    private static void Audit(AppDbContext db, ClaimsPrincipal user, HttpContext http, string what, string format, int rows, object filters) =>
        db.AuditLog.Add(new AuditEntry
        {
            OrganizationId = Guid.Parse(user.FindFirstValue("org")!), ActorUserId = Guid.Parse(user.FindFirstValue("sub")!), Action = "exported", EntityType = "Export", EntityId = what,
            After = JsonSerializer.Serialize(new { format, rows, filters }), Ip = http.Connection.RemoteIpAddress?.ToString(), Device = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 200)] : null,
        });

    /// <summary>English or Urdu: the language asked for, else the person's own, else the society's.</summary>
    private static async Task<string> LocaleAsync(string? lang, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        if (lang is "en" or "ur") return lang;
        var me = Guid.Parse(user.FindFirstValue("sub")!);
        var own = await db.Users.AsNoTracking().Where(u => u.Id == me).Select(u => u.PreferredLocale).SingleOrDefaultAsync(ct);
        return own ?? await db.OrgSettings.AsNoTracking().Select(s => s.Locale).SingleOrDefaultAsync(ct) ?? "en";
    }

    private static async Task<string> OrganizationNameAsync(AppDbContext db, CancellationToken ct) =>
        await db.OrgSettings.AsNoTracking().Select(s => s.DisplayName).SingleOrDefaultAsync(ct)
        ?? await db.Organizations.AsNoTracking().Select(o => o.Name).FirstOrDefaultAsync(ct) ?? "";

    private static string Stamp(DateTimeOffset? at) => at is { } t ? t.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "";

    // Complaint list ---------------------------------------------------------------------------------------------

    private static async Task<IResult> ComplaintsAsync(
        string? format, string? lang, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, IPdfRenderer pdf,
        ComplaintStatus? status, string? q, Guid? categoryId, Guid? departmentId, Guid? locationId, string? priority, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        format ??= "csv";
        if (!KnownFormat(format)) return Results.BadRequest(new { error = "invalid_format" });
        var actor = await ActorAsync(principal, memberships, ct);
        var (query, error) = await FilteredAsync(actor, db, status, q, categoryId, departmentId, locationId, priority, from, to, ct);
        if (error is not null) return error;

        var total = await query!.CountAsync(ct);
        var rows = await query!.OrderByDescending(c => c.CreatedAt).Take(MaxRows).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c, ct);
        var locale = await LocaleAsync(lang, principal, db, ct);
        string CategoryName(Guid? id) => id is { } c && categories.TryGetValue(c, out var k) ? (locale == "ur" && k.NameUr is { Length: > 0 } ur ? ur : k.NameEn) : "";

        // A phone number was given to the society to follow up a complaint; only people who triage may take it out of the system.
        var withPhone = actor.Has(Permissions.ComplaintTriage);
        var headers = new List<string> { "Reference", "Status", "Priority", "Category", "Channel", "Location", "Created", "Resolved", "Description" };
        if (withPhone) headers.Add("Reporter phone");
        List<string> Row(Complaint c, int cut)
        {
            var description = cut > 0 && c.Description.Length > cut ? c.Description[..cut] + "…" : c.Description;
            var row = new List<string> { c.Reference, c.Status.ToString(), c.Priority, CategoryName(c.CategoryId), c.Channel, c.LocationText ?? "", Stamp(c.CreatedAt), Stamp(c.ResolvedAt), description };
            if (withPhone) row.Add(c.ReporterPhone ?? "");
            return row;
        }

        Audit(db, principal, http, "complaints", format, rows.Count, new { status, q, categoryId, departmentId, locationId, priority, from, to });
        await db.SaveChangesAsync(ct);

        if (format == "csv") return File(CsvWriter.Write(headers, rows.Select(c => (IReadOnlyList<string>)Row(c, 0))), "csv", "complaints");

        var shown = new[] { 0, 1, 2, 3, 6, 8 };
        var report = new PdfReport(locale == "ur" ? "شکایات کی فہرست" : "Complaint list", total > rows.Count ? (locale == "ur" ? $"پہلی {rows.Count} از {total}" : $"First {rows.Count} of {total}") : null,
            locale, await OrganizationNameAsync(db, ct), DateTimeOffset.UtcNow,
            [new PdfSection(Table: new PdfTable(shown.Select(i => headers[i]).ToList(), rows.Select(c => (IReadOnlyList<string>)shown.Select(i => Row(c, 90)[i]).ToList()).ToList()))]);
        return File(pdf.Render(report), "pdf", "complaints");
    }

    // Management summary ------------------------------------------------------------------------------------------

    private static async Task<IResult> SummaryAsync(
        string? format, string? lang, DateTimeOffset? from, DateTimeOffset? to, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships,
        IPdfRenderer pdf, CancellationToken ct)
    {
        format ??= "pdf";
        if (!KnownFormat(format)) return Results.BadRequest(new { error = "invalid_format" });
        var costs = (await memberships.GetAsync(ct)).Any(m => RolePermissions.Allows(m.Role, Permissions.CostRead));
        Audit(db, principal, http, "summary", format, 0, new { from, to, costs });
        await db.SaveChangesAsync(ct);

        var locale = await LocaleAsync(lang, principal, db, ct);
        var report = await SummaryReportBuilder.BuildAsync(db, await OrganizationNameAsync(db, ct), locale, from, to, costs, DateTimeOffset.UtcNow, ct);
        if (format == "pdf") return File(pdf.Render(report), "pdf", "summary");

        // The same sections, flattened: section, name, then the figures.
        var lines = report.Sections.SelectMany(s => (s.Facts ?? []).Select(f => (IReadOnlyList<string>)[s.Heading ?? "", f.Label, f.Value])
            .Concat((s.Table?.Rows ?? []).Select(r => (IReadOnlyList<string>)[s.Heading ?? "", ..r]))).ToList();
        return File(CsvWriter.Write(["Section", "Item", "Value 1", "Value 2", "Value 3"], lines), "csv", "summary");
    }

    // Costs -------------------------------------------------------------------------------------------------------

    private static async Task<IResult> CostsAsync(string? groupBy, DateTimeOffset? from, DateTimeOffset? to, HttpContext http, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var by = groupBy ?? "category";
        if (by is not ("job" or "category" or "street")) return Results.BadRequest(new { error = "invalid_group_by" });
        var report = await CostReportEndpoints.BuildAsync(db, by, from, to, ct);
        Audit(db, principal, http, "costs", "csv", report.Rows.Count, new { by, from, to });
        await db.SaveChangesAsync(ct);
        string M(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        return File(CsvWriter.Write([by, "Jobs", "Estimated", "Actual", "Over / under"], report.Rows.Select(r => (IReadOnlyList<string>)[r.Name, r.Jobs.ToString(CultureInfo.InvariantCulture), M(r.Estimated), M(r.Actual), M(r.Variance)])), "csv", "costs");
    }
}
