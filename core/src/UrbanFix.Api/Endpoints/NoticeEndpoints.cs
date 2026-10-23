using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Notices and announcements to residents, with delivered and read counts for the sender (NT-08).</summary>
public static class NoticeEndpoints
{
    public sealed record CreateNotice(string Title, string Body, string? TitleUr, string? BodyUr, string AudienceKind, Guid? LocationId, DateTimeOffset? ScheduledAt);
    public sealed record NoticeDto(
        Guid Id, string Title, string Body, string? TitleUr, string? BodyUr, string AudienceKind, Guid? LocationId, string Status,
        DateTimeOffset? ScheduledAt, DateTimeOffset? SentAt, DateTimeOffset CreatedAt, int Recipients, int Delivered, int Read, int Failed);
    public sealed record AudienceDto(int Recipients);
    public sealed record ReaderDto(Guid UserId, string Name, bool Delivered, bool Failed, DateTimeOffset? ReadAt);
    /// <summary>A notice as one recipient sees it. <c>Id</c> is their notification; reading it is <c>POST /notifications/{id}/read</c>.</summary>
    public sealed record MyNoticeDto(Guid Id, Guid NoticeId, string Title, string Body, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

    private const int MaxTitle = 200;
    private const int MaxBody = 4000;

    private static NoticeDto ToDto(Notice n, NoticeCounts? c) =>
        new(n.Id, n.Title, n.Body, n.TitleUr, n.BodyUr, n.AudienceKind, n.LocationId, n.Status, n.ScheduledAt, n.SentAt, n.CreatedAt,
            c?.Recipients ?? 0, c?.Delivered ?? 0, c?.Read ?? 0, c?.Failed ?? 0);

    public static void MapNoticeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Notices");
        var manage = AuthPolicies.For(Permissions.NoticeManage);

        g.MapPost("/notices", async (CreateNotice req, ClaimsPrincipal user, AppDbContext db, ITenantContext tenant, NoticeService notices, CancellationToken ct) =>
        {
            var title = req.Title?.Trim() ?? "";
            var body = req.Body?.Trim() ?? "";
            if (title.Length is 0 or > MaxTitle) return Results.BadRequest(new { error = "invalid_title" });
            if (body.Length is 0 or > MaxBody) return Results.BadRequest(new { error = "invalid_body" });
            var titleUr = string.IsNullOrWhiteSpace(req.TitleUr) ? null : req.TitleUr.Trim();
            var bodyUr = string.IsNullOrWhiteSpace(req.BodyUr) ? null : req.BodyUr.Trim();
            // The Urdu text is both parts or neither: half a translation would show an English title over Urdu words.
            if ((titleUr is null) != (bodyUr is null) || titleUr?.Length > MaxTitle || bodyUr?.Length > MaxBody) return Results.BadRequest(new { error = "invalid_urdu_text" });
            if (!NoticeAudience.Kinds.Contains(req.AudienceKind)) return Results.BadRequest(new { error = "invalid_audience" });
            if (req.LocationId is { } loc && !await db.Locations.AnyAsync(l => l.Id == loc, ct)) return Results.BadRequest(new { error = "location_not_found" });

            var now = DateTimeOffset.UtcNow;
            var later = req.ScheduledAt is { } at && at > now;
            if (!later && (await notices.ResolveAudienceAsync(req.AudienceKind, req.LocationId, ct)).Count == 0)
                return Results.BadRequest(new { error = "no_recipients" });

            var notice = new Notice
            {
                OrganizationId = tenant.OrganizationId!.Value, Title = title, Body = body, TitleUr = titleUr, BodyUr = bodyUr,
                AudienceKind = req.AudienceKind, LocationId = req.LocationId, CreatedBy = Guid.Parse(user.FindFirstValue("sub")!),
                ScheduledAt = later ? req.ScheduledAt : null,
            };
            db.Notices.Add(notice);
            if (!later) notices.Release(notice, now);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/notices/{notice.Id}", ToDto(notice, null));
        }).Produces<NoticeDto>(201).RequireAuthorization(manage);

        g.MapGet("/notices/audience", async (string kind, Guid? locationId, AppDbContext db, NoticeService notices, CancellationToken ct) =>
        {
            if (!NoticeAudience.Kinds.Contains(kind)) return Results.BadRequest(new { error = "invalid_audience" });
            if (locationId is { } loc && !await db.Locations.AnyAsync(l => l.Id == loc, ct)) return Results.BadRequest(new { error = "location_not_found" });
            return Results.Ok(new AudienceDto((await notices.ResolveAudienceAsync(kind, locationId, ct)).Count));
        }).Produces<AudienceDto>(200).RequireAuthorization(manage);

        g.MapGet("/notices", async (string? status, AppDbContext db, NoticeService notices, CancellationToken ct) =>
        {
            var q = db.Notices.AsNoTracking().AsQueryable();
            if (status is not null) q = q.Where(n => n.Status == status);
            var rows = await q.OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync(ct);
            var counts = await notices.CountsAsync(rows.Select(n => n.Id).ToList(), ct);
            return Results.Ok(rows.Select(n => ToDto(n, counts.GetValueOrDefault(n.Id))).ToList());
        }).Produces<List<NoticeDto>>(200).RequireAuthorization(manage);

        g.MapGet("/notices/{id:guid}", async (Guid id, AppDbContext db, NoticeService notices, CancellationToken ct) =>
        {
            var n = await db.Notices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
            if (n is null) return Results.NotFound();
            return Results.Ok(ToDto(n, (await notices.CountsAsync([id], ct)).GetValueOrDefault(id)));
        }).Produces<NoticeDto>(200).RequireAuthorization(manage);

        // Who has read it, for the sender (WP-19).
        g.MapGet("/notices/{id:guid}/readers", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Notices.AnyAsync(n => n.Id == id, ct)) return Results.NotFound();
            var rows = await (from n in db.Notifications.AsNoTracking()
                              join u in db.Users.AsNoTracking() on n.UserId equals u.Id
                              where n.NoticeId == id
                              orderby u.DisplayName
                              select new
                              {
                                  n.UserId, u.DisplayName, n.ReadAt,
                                  Statuses = db.NotificationDeliveries.Where(d => d.NotificationId == n.Id).Select(d => d.Status).ToList(),
                              }).ToListAsync(ct);
            return Results.Ok(rows.Select(r => new ReaderDto(r.UserId, r.DisplayName, r.Statuses.Any(s => s is "Sent" or "Delivered" or "Read"),
                r.Statuses.Count > 0 && r.Statuses.All(s => s == "Failed"), r.ReadAt)).ToList());
        }).Produces<List<ReaderDto>>(200).RequireAuthorization(manage);

        // Only a notice that has not gone out can be called back; one that has been sent cannot be unsent.
        g.MapPost("/notices/{id:guid}/cancel", async (Guid id, AppDbContext db, NoticeService notices, CancellationToken ct) =>
        {
            var n = await db.Notices.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (n is null) return Results.NotFound();
            if (n.Status != NoticeStatus.Scheduled) return Results.Conflict(new { error = "notice_not_scheduled" });
            n.Status = NoticeStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(n, null));
        }).Produces<NoticeDto>(200).RequireAuthorization(manage);

        // The recipient's own feed (RA-09). Reading one marks it read for the sender too.
        g.MapGet("/me/notices", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var me = Guid.Parse(user.FindFirstValue("sub")!);
            var rows = await db.Notifications.AsNoTracking().Where(n => n.UserId == me && n.NoticeId != null).OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(rows.Select(n => new MyNoticeDto(n.Id, n.NoticeId!.Value, n.Title, n.Body, n.CreatedAt, n.ReadAt)).ToList());
        }).Produces<List<MyNoticeDto>>(200).RequireAuthorization(AuthPolicies.Verified);
    }
}
