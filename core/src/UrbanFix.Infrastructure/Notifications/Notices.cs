using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Notifications;

public static class NoticeStatus
{
    public const string Scheduled = "Scheduled";
    public const string Sent = "Sent";
    public const string Cancelled = "Cancelled";
}

public static class NoticeAudience
{
    public const string All = "all";
    public const string Owners = "owners";
    public const string Tenants = "tenants";
    public static readonly string[] Kinds = [All, Owners, Tenants];
}

/// <summary>An announcement from the committee or manager to residents (NT-08).</summary>
public class Notice : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? TitleUr { get; set; }
    public string? BodyUr { get; set; }
    public string AudienceKind { get; set; } = NoticeAudience.All;
    /// <summary>Only the houses under this location (a street, a block); null means the whole organization.</summary>
    public Guid? LocationId { get; set; }
    public string Status { get; set; } = NoticeStatus.Scheduled;
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A notice has been released and its audience is to be notified. Handling it twice sends nothing twice.</summary>
public sealed record NoticePublished(Guid OrganizationId, Guid NoticeId) : DomainEvent(OrganizationId);

public sealed record NoticeCounts(int Recipients, int Delivered, int Read, int Failed);

public sealed class NoticeService(AppDbContext db, IEventBus bus, NotificationService notifications) : IEventHandler<NoticePublished>
{
    /// <summary>
    /// Who a notice reaches right now. Owners and tenants are people with a current link of that kind; everyone is all
    /// current links of any kind plus residents not tied to a house (only when the notice is not limited to a location).
    /// </summary>
    public async Task<IReadOnlyList<Guid>> ResolveAudienceAsync(string kind, Guid? locationId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var links = db.ResidentLinks.AsNoTracking().Where(r => r.FromDate <= today && (r.ToDate == null || r.ToDate >= today));
        if (kind == NoticeAudience.Owners) links = links.Where(r => r.Relation == "owner");
        else if (kind == NoticeAudience.Tenants) links = links.Where(r => r.Relation == "tenant");

        if (locationId is { } root)
        {
            var prefix = await db.Locations.AsNoTracking().Where(l => l.Id == root).Select(l => l.Path).SingleAsync(ct);
            var houses = from h in db.Houses.AsNoTracking() join l in db.Locations.AsNoTracking() on h.LocationId equals l.Id where l.Path.StartsWith(prefix) select h.Id;
            links = links.Where(r => houses.Contains(r.HouseId));
        }
        var people = await links.Select(r => r.UserId).Distinct().ToListAsync(ct);

        if (kind == NoticeAudience.All && locationId is null)
        {
            var residents = await db.Memberships.AsNoTracking().Where(m => m.IsActive && m.Role == Role.Resident).Select(m => m.UserId).ToListAsync(ct);
            people = people.Concat(residents).Distinct().ToList();
        }
        if (people.Count == 0) return people;
        // Only people who can actually be messaged: active users who still belong to this organization.
        var member = await db.Memberships.AsNoTracking().Where(m => m.IsActive && people.Contains(m.UserId)).Select(m => m.UserId).Distinct().ToListAsync(ct);
        return member;
    }

    /// <summary>Releases the notice now: marks it sent and stages the event that notifies the audience.</summary>
    public void Release(Notice notice, DateTimeOffset now)
    {
        notice.Status = NoticeStatus.Sent;
        notice.SentAt = now;
        bus.Publish(new NoticePublished(notice.OrganizationId, notice.Id));
    }

    public async Task Handle(NoticePublished e, CancellationToken ct)
    {
        var notice = await db.Notices.AsNoTracking().SingleOrDefaultAsync(n => n.Id == e.NoticeId, ct);
        if (notice is null) return;
        var audience = await ResolveAudienceAsync(notice.AudienceKind, notice.LocationId, ct);

        var english = new Dictionary<string, string> { ["title"] = notice.Title, ["body"] = notice.Body };
        var byLocale = new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["en"] = english,
            ["ur"] = notice.TitleUr is { Length: > 0 } && notice.BodyUr is { Length: > 0 }
                ? new Dictionary<string, string> { ["title"] = notice.TitleUr, ["body"] = notice.BodyUr }
                : english,
        };
        // Keyed on the notice, not the event: however many times a release is handled, each person gets it once.
        await notifications.SendAsync(notice.Id, e.OrganizationId,
            audience.Select(u => new Recipient(u, NotificationTemplates.Notice, english, $"/notices/{notice.Id}", notice.Id, byLocale)), ct);
    }

    /// <summary>Delivered: at least one channel took the message. Failed: every attempt failed. Read: opened in the app or read on a channel that reports it.</summary>
    public async Task<Dictionary<Guid, NoticeCounts>> CountsAsync(IReadOnlyCollection<Guid> noticeIds, CancellationToken ct)
    {
        var rows = await (from n in db.Notifications.AsNoTracking()
                          where n.NoticeId != null && noticeIds.Contains(n.NoticeId.Value)
                          select new
                          {
                              NoticeId = n.NoticeId!.Value,
                              n.ReadAt,
                              Statuses = db.NotificationDeliveries.Where(d => d.NotificationId == n.Id).Select(d => d.Status).ToList(),
                          }).ToListAsync(ct);
        return rows.GroupBy(r => r.NoticeId).ToDictionary(g => g.Key, g => new NoticeCounts(
            g.Count(),
            g.Count(r => r.Statuses.Any(s => s is "Sent" or "Delivered" or "Read")),
            g.Count(r => r.ReadAt != null || r.Statuses.Contains("Read")),
            g.Count(r => r.Statuses.Count > 0 && r.Statuses.All(s => s == "Failed"))));
    }
}

/// <summary>Releases scheduled notices when their time comes. Every minute.</summary>
public sealed class NoticeSweeper(IServiceScopeFactory scopes, IConfiguration config, IJobFailureNotifier failures)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var released = 0;
        var errors = new List<Exception>();
        foreach (var org in await OrganizationList.ReadAsync(config, ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(org, null);
                released += await ReleaseDueAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<NoticeService>(), DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                failures.JobFailed("notice-sweep", $"NoticeSweeper (organization {org})", ex.Message);
            }
        }
        if (errors.Count > 0) throw new AggregateException("Notice sweep failed", errors);
        return released;
    }

    public async Task<int> ReleaseDueAsync(AppDbContext db, NoticeService notices, DateTimeOffset now, CancellationToken ct)
    {
        var due = await db.Notices.Where(n => n.Status == NoticeStatus.Scheduled && n.ScheduledAt != null && n.ScheduledAt <= now).ToListAsync(ct);
        foreach (var notice in due) notices.Release(notice, now);
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
