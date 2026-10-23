using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;

namespace UrbanFix.Infrastructure.WhatsApp;

public sealed record InboxResult(int Stored, int Duplicates, int UnknownNumbers, int StatusUpdates);

/// <summary>
/// Takes in a verified webhook (WA-02): finds the organization from the business number, stores each message once as received
/// (a replay finds it already there and does nothing), and queues the work. Delivery receipts for messages we sent are applied
/// here too. Nothing slow happens in the request: Meta expects a quick answer.
/// </summary>
public sealed class WhatsAppInbox(AppDbContext db, IBackgroundJobClient jobs, NotificationService notifications, ILogger<WhatsAppInbox> log)
{
    private readonly Dictionary<string, Guid?> _orgs = [];

    private async Task<Guid?> OrganizationOf(string phoneNumberId, CancellationToken ct)
    {
        if (_orgs.TryGetValue(phoneNumberId, out var known)) return known;
        var found = (await db.Database.SqlQuery<Guid?>($"SELECT find_whatsapp_channel({phoneNumberId}) AS \"Value\"").ToListAsync(ct)).SingleOrDefault();
        return _orgs[phoneNumberId] = found;
    }

    public async Task<InboxResult> AcceptAsync(ParsedWebhook hook, CancellationToken ct)
    {
        int stored = 0, duplicates = 0, unknown = 0, statuses = 0;

        foreach (var m in hook.Messages)
        {
            if (await OrganizationOf(m.PhoneNumberId, ct) is not { } org) { unknown++; log.LogWarning("WhatsApp message for unknown number id {Id} ignored", m.PhoneNumberId); continue; }
            await db.ChangeTenantAsync(org, null, ct);
            var row = new WhatsAppMessage
            {
                OrganizationId = org, Direction = "in", WaMessageId = m.MessageId, WaId = m.From, Type = m.Type, Raw = m.Raw,
                Body = m.Text ?? m.Caption ?? m.PlaceName ?? m.ButtonTitle, MediaId = m.MediaId, Status = MessageStatus.Received,
            };
            db.WhatsAppMessages.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear(); // seen before: a retry or a replay
                duplicates++;
                continue;
            }
            stored++;
            jobs.Enqueue<WhatsAppInboundJob>(j => j.RunAsync(org, row.Id, CancellationToken.None));
        }

        foreach (var s in hook.Statuses)
        {
            if (await OrganizationOf(s.PhoneNumberId, ct) is not { } org) { unknown++; continue; }
            await db.ChangeTenantAsync(org, null, ct);
            await ApplyStatusAsync(s, ct);
            statuses++;
        }
        return new InboxResult(stored, duplicates, unknown, statuses);
    }

    private static int Rank(string status) => status switch
    {
        MessageStatus.Sent => 1, MessageStatus.Delivered => 2, MessageStatus.Read => 3, _ => 0,
    };

    /// <summary>Sent, delivered, read or failed for something we sent: kept on the message and on the notification it belonged to (NT-02, NT-07).</summary>
    private async Task ApplyStatusAsync(StatusUpdate s, CancellationToken ct)
    {
        var message = await db.WhatsAppMessages.FirstOrDefaultAsync(m => m.Direction == "out" && m.WaMessageId == s.MessageId, ct);
        if (message is not null)
        {
            // Receipts can arrive out of order; never step back from "read" to "delivered".
            if (s.Status == "failed") { message.Status = MessageStatus.Failed; message.Error = s.ErrorTitle ?? s.ErrorCode; }
            else if (Rank(s.Status) > Rank(message.Status) && message.Status != MessageStatus.Failed) message.Status = s.Status;
            message.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var delivery = await db.NotificationDeliveries.FirstOrDefaultAsync(d => d.ProviderMessageId == s.MessageId, ct);
        if (delivery is not null)
        {
            switch (s.Status)
            {
                case "delivered" when delivery.Status is "Sent": delivery.Status = "Delivered"; delivery.DeliveredAt = s.At; break;
                case "read" when delivery.Status is "Sent" or "Delivered": delivery.Status = "Read"; delivery.DeliveredAt ??= s.At; delivery.ReadAt = s.At; break;
                case "failed":
                    delivery.Status = "Failed"; delivery.LastError = s.ErrorTitle ?? s.ErrorCode ?? "failed";
                    await notifications.FallBackToSmsAsync(delivery, ct);
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}
