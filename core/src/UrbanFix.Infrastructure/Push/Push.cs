using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;

namespace UrbanFix.Infrastructure.Push;

public static class PushKinds
{
    public const string Fcm = "fcm";
    public const string Web = "web";
    public static readonly string[] All = [Fcm, Web];
}

/// <summary>A phone or browser that may be sent notifications (FD-16).</summary>
public class PushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Kind { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>Browsers: JSON <c>{"p256dh": "...", "auth": "..."}</c>, base64url, as the browser gave them.</summary>
    public string? Keys { get; set; }
    public string? Label { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSentAt { get; set; }
    public int Failures { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class FcmOptions
{
    public bool Enabled { get; set; }
    public string ProjectId { get; set; } = "";
    /// <summary>The service-account key file's contents (JSON). It goes in the secret vault, never in the repository.</summary>
    public string ServiceAccountJson { get; set; } = "";
    public string ApiBase { get; set; } = "https://fcm.googleapis.com";
}

public sealed class WebPushOptions
{
    public bool Enabled { get; set; }
    /// <summary>A contact for the push services, such as <c>mailto:ops@example.org</c>.</summary>
    public string Subject { get; set; } = "";
    /// <summary>VAPID public key: base64url of the 65-byte uncompressed P-256 point. Browsers need it to subscribe.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>VAPID private key: base64url of the 32-byte scalar.</summary>
    public string PrivateKey { get; set; } = "";
}

public sealed class PushOptions
{
    public FcmOptions Fcm { get; set; } = new();
    public WebPushOptions Web { get; set; } = new();
    /// <summary>Development only: log what would be pushed instead of calling FCM or a browser's push service.</summary>
    public bool LogOnly { get; set; }
}

public sealed record PushMessage(string Title, string Body, string? Link, Guid? NotificationId);

public enum PushOutcome
{
    Sent,
    /// <summary>The service says this device no longer exists (app removed, permission withdrawn). Retrying cannot help.</summary>
    Gone,
    /// <summary>Trouble that may pass: try again later.</summary>
    Retry,
}

public sealed record PushResult(PushOutcome Outcome, string? MessageId = null, string? Detail = null);

/// <summary>Delivers one message to one device through its push service. Never throws for a delivery problem; it says which kind.</summary>
public interface IPushSender
{
    string Kind { get; }
    Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct);
}

/// <summary>For development: shows what a real sender would have sent.</summary>
public sealed class LogPushSender(string kind, ILogger<LogPushSender> log) : IPushSender
{
    public string Kind => kind;

    public Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct)
    {
        log.LogInformation("Push ({Kind}) to {Device}: {Title} - {Body}", kind, device.Label ?? device.Id.ToString(), message.Title, message.Body);
        return Task.FromResult(new PushResult(PushOutcome.Sent, "log-" + Guid.NewGuid().ToString("N")[..8]));
    }
}

public sealed record PushDeviceResult(Guid DeviceId, string Kind, string? Label, PushOutcome Outcome, string? Detail);
public sealed record PushSummary(IReadOnlyList<PushDeviceResult> Devices)
{
    public int Sent => Devices.Count(d => d.Outcome == PushOutcome.Sent);
    public int Gone => Devices.Count(d => d.Outcome == PushOutcome.Gone);
    public int Retry => Devices.Count(d => d.Outcome == PushOutcome.Retry);
}

/// <summary>Registers devices and sends to a person's devices (FD-16). The notification channel and the "send me a test" button both use it.</summary>
public sealed class PushService(AppDbContext db, IEnumerable<IPushSender> senders)
{
    // If a kind is registered twice, the later one wins (a stand-in in tests, or log-only beside a real one).
    private readonly Dictionary<string, IPushSender> _senders = senders.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Last());

    public IReadOnlyCollection<string> Kinds => _senders.Keys;

    /// <summary>
    /// Remembers a device for a person. The same device registering again just refreshes it, and a device that changes hands
    /// (someone signs in on a phone that another person used) goes to the new person: it must never keep notifying the old one.
    /// </summary>
    public async Task<PushDevice> RegisterAsync(Guid organizationId, Guid userId, string kind, string token, string? keys, string? label, CancellationToken ct)
    {
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Token == token, ct);
        if (device is null)
        {
            device = new PushDevice { OrganizationId = organizationId, UserId = userId, Kind = kind, Token = token };
            db.PushDevices.Add(device);
        }
        device.UserId = userId; device.Kind = kind; device.Keys = keys; device.Label = label ?? device.Label;
        device.RevokedAt = null; device.Failures = 0; device.LastError = null;
        // Allowing notifications on a device means the person wants them: switch the push channel on for them.
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (!user.NotifyChannels.Contains(ChannelNames.Push)) user.NotifyChannels = [.. user.NotifyChannels, ChannelNames.Push];
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<bool> RevokeAsync(Guid userId, Guid deviceId, CancellationToken ct)
    {
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Id == deviceId && d.UserId == userId && d.RevokedAt == null, ct);
        if (device is null) return false;
        device.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<PushSummary> SendToUserAsync(Guid userId, PushMessage message, CancellationToken ct)
    {
        var devices = await db.PushDevices.Where(d => d.UserId == userId && d.RevokedAt == null).ToListAsync(ct);
        var results = new List<PushDeviceResult>();
        foreach (var device in devices)
        {
            PushResult result;
            if (!_senders.TryGetValue(device.Kind, out var sender)) result = new PushResult(PushOutcome.Retry, null, "no_sender_for_" + device.Kind);
            else
            {
                try { result = await sender.SendAsync(device, message, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or CryptographicException) { result = new PushResult(PushOutcome.Retry, null, ex.Message); }
            }

            switch (result.Outcome)
            {
                case PushOutcome.Sent: device.LastSentAt = DateTimeOffset.UtcNow; device.Failures = 0; device.LastError = null; break;
                case PushOutcome.Gone: device.RevokedAt = DateTimeOffset.UtcNow; device.LastError = result.Detail; break;
                default: device.Failures++; device.LastError = result.Detail; break;
            }
            results.Add(new PushDeviceResult(device.Id, device.Kind, device.Label, result.Outcome, result.Detail ?? result.MessageId));
        }
        await db.SaveChangesAsync(ct);
        return new PushSummary(results);
    }
}

/// <summary>Notifications as push messages to the person's phones and browsers (FD-16).</summary>
public sealed class PushChannel(PushService push) : INotificationChannel
{
    public string Name => ChannelNames.Push;

    public async Task<string?> SendAsync(Notification n, CancellationToken ct)
    {
        var summary = await push.SendToUserAsync(n.UserId, new PushMessage(n.Title, n.Body, n.Link, n.Id), ct);
        if (summary.Devices.Count == 0) throw new PermanentChannelFailure("no_push_device");
        if (summary.Sent > 0) return summary.Devices.First(d => d.Outcome == PushOutcome.Sent).Detail;
        if (summary.Retry == 0) throw new PermanentChannelFailure("devices_unregistered");
        throw new InvalidOperationException("Push not delivered: " + string.Join("; ", summary.Devices.Select(d => $"{d.Kind}: {d.Detail}")));
    }
}
