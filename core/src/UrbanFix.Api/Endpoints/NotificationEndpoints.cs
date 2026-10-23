using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Live channel for the open app and portal (NT-05). Clients connect with the session token as <c>access_token</c>.</summary>
public sealed class NotificationHub : Hub
{
    public const string Path = "/hubs/notifications";
    public static string GroupFor(Guid organizationId, Guid userId) => $"org:{organizationId:N}:user:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        if (Guid.TryParse(user.FindFirstValue("org"), out var org) && Guid.TryParse(user.FindFirstValue("sub"), out var id))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(org, id));
        await base.OnConnectedAsync();
    }
}

public sealed class SignalRNotificationPusher(IHubContext<NotificationHub> hub) : INotificationPusher
{
    public Task PushAsync(Notification n, CancellationToken ct) =>
        hub.Clients.Group(NotificationHub.GroupFor(n.OrganizationId, n.UserId)).SendAsync("notification", NotificationEndpoints.ToDto(n), ct);
}

/// <summary>Inbox, preferences (NT-01) and the delivery log.</summary>
public static class NotificationEndpoints
{
    public sealed record NotificationDto(Guid Id, string Template, string Title, string Body, string? Link, string Locale, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);
    public sealed record PreferencesDto(string? Locale, string[] Channels, string[] AvailableChannels);
    public sealed record SavePreferences(string? Locale, string[]? Channels);
    public sealed record ChannelStats(string Channel, int Total, int Sent, int Delivered, int Read, int Failed, double FailureRate);
    public sealed record InboxDto(int Unread, IReadOnlyList<NotificationDto> Items);
    public sealed record DeliveryDto(Guid Id, Guid NotificationId, Guid UserId, string Template, string Channel, string Status, int Attempts, string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? SentAt);

    internal static NotificationDto ToDto(Notification n) => new(n.Id, n.Template, n.Title, n.Body, n.Link, n.Locale, n.CreatedAt, n.ReadAt);

    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Notifications");
        g.MapGet("/reports/delivery", async (DateTimeOffset? from, DateTimeOffset? to, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.NotificationDeliveries.AsNoTracking().AsQueryable();
            if (from is { } f) q = q.Where(d => d.CreatedAt >= f);
            if (to is { } t) q = q.Where(d => d.CreatedAt <= t);
            var rows = await q.Select(d => new { d.Channel, d.Status }).ToListAsync(ct);
            return Results.Ok(rows.GroupBy(r => r.Channel).OrderBy(g2 => g2.Key).Select(g2 =>
            {
                var total = g2.Count();
                var failed = g2.Count(r => r.Status == "Failed");
                return new ChannelStats(g2.Key, total, g2.Count(r => r.Status == "Sent"), g2.Count(r => r.Status == "Delivered"), g2.Count(r => r.Status == "Read"), failed,
                    total == 0 ? 0 : Math.Round(failed / (double)total, 4));
            }).ToList());
        }).Produces<List<ChannelStats>>(200).RequireAuthorization(AuthPolicies.For(Permissions.DashboardRead));
        var verified = AuthPolicies.Verified;

        g.MapGet("/notifications", async (bool? unreadOnly, int? limit, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var me = Guid.Parse(user.FindFirstValue("sub")!);
            var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == me);
            var unread = await mine.CountAsync(n => n.ReadAt == null, ct);
            var q = unreadOnly == true ? mine.Where(n => n.ReadAt == null) : mine;
            var items = await q.OrderByDescending(n => n.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
            return Results.Ok(new InboxDto(unread, items.Select(ToDto).ToList()));
        }).Produces<InboxDto>(200).RequireAuthorization(verified);

        g.MapPost("/notifications/{id:guid}/read", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var me = Guid.Parse(user.FindFirstValue("sub")!);
            var n = await db.Notifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == me, ct);
            if (n is null) return Results.NotFound();
            n.ReadAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(n));
        }).Produces<NotificationDto>(200).RequireAuthorization(verified);

        g.MapPost("/notifications/read-all", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var me = Guid.Parse(user.FindFirstValue("sub")!);
            var count = await db.Notifications.Where(n => n.UserId == me && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, DateTimeOffset.UtcNow), ct);
            return Results.Ok(new { marked = count });
        }).RequireAuthorization(verified);

        g.MapGet("/me/preferences", async (ClaimsPrincipal user, AppDbContext db, NotificationService service, CancellationToken ct) =>
        {
            var me = Guid.Parse(user.FindFirstValue("sub")!);
            var u = await db.Users.AsNoTracking().SingleAsync(x => x.Id == me, ct);
            return Results.Ok(new PreferencesDto(u.PreferredLocale, u.NotifyChannels, service.AvailableChannels.Where(c => c != ChannelNames.InApp).Order().ToArray()));
        }).Produces<PreferencesDto>(200).RequireAuthorization(verified);

        g.MapPut("/me/preferences", async (SavePreferences req, ClaimsPrincipal user, AppDbContext db, NotificationService service, CancellationToken ct) =>
        {
            if (req.Locale is not null and not ("en" or "ur")) return Results.BadRequest(new { error = "invalid_locale" });
            var optional = service.AvailableChannels.Where(c => c != ChannelNames.InApp).ToHashSet();
            var channels = (req.Channels ?? []).Distinct().ToArray();
            if (channels.Any(c => !optional.Contains(c))) return Results.BadRequest(new { error = "unknown_channel" });

            var me = Guid.Parse(user.FindFirstValue("sub")!);
            var u = await db.Users.SingleAsync(x => x.Id == me, ct);
            u.PreferredLocale = req.Locale;
            u.NotifyChannels = channels;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PreferencesDto(u.PreferredLocale, u.NotifyChannels, optional.Order().ToArray()));
        }).Produces<PreferencesDto>(200).RequireAuthorization(verified);

        // Failed sends stay visible here until a retry succeeds (NT-01).
        g.MapGet("/notifications/log", async (string? status, int? limit, AppDbContext db, CancellationToken ct) =>
        {
            var q = from d in db.NotificationDeliveries.AsNoTracking()
                    join n in db.Notifications.AsNoTracking() on d.NotificationId equals n.Id
                    where status == null || d.Status == status
                    orderby d.CreatedAt descending
                    select new DeliveryDto(d.Id, n.Id, n.UserId, n.Template, d.Channel, d.Status, d.Attempts, d.LastError, d.CreatedAt, d.LastAttemptAt, d.SentAt);
            return Results.Ok(await q.Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct));
        }).Produces<List<DeliveryDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.AuditRead));

        app.MapHub<NotificationHub>(NotificationHub.Path).RequireAuthorization(verified);
    }
}
