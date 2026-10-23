using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Notifications;

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string Template { get; set; } = "";
    public string Locale { get; set; } = "en";
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Link { get; set; }
    public string DedupeKey { get; set; } = "";
    /// <summary>The values the text was rendered from (JSON), kept for channels that fill in a provider template.</summary>
    public string TemplateValues { get; set; } = "{}";
    /// <summary>Set when the message is a notice (NT-08), so the sender can count delivered and read per notice.</summary>
    public Guid? NoticeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReadAt { get; set; }
}

public class NotificationDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid NotificationId { get; set; }
    public string Channel { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    /// <summary>The provider's id for the message (the WhatsApp message id), so its delivery receipts can be matched back (NT-02).</summary>
    public string? ProviderMessageId { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public static class ChannelNames
{
    /// <summary>Always on: the stored inbox, pushed live to open apps and the portal (NT-05).</summary>
    public const string InApp = "in_app";
    /// <summary>Messages to the person's phones and browsers (FD-16). Installed only when a push service is configured.</summary>
    public const string Push = "push";
}

/// <summary>A way of delivering a notification. Throwing means "try again later"; the outbox retries with backoff.</summary>
public interface INotificationChannel
{
    string Name { get; }
    /// <returns>The provider's id for the message, when it has one, so delivery receipts can be matched back (NT-02).</returns>
    Task<string?> SendAsync(Notification notification, CancellationToken ct);
}

/// <summary>A failure that trying again cannot fix (no phone number, no consent, no approved template). It is recorded and the fallback is tried, not retried.</summary>
public sealed class PermanentChannelFailure(string reason) : Exception(reason);

/// <summary>Pushes a stored notification to the recipient's open sessions. The API replaces the no-op default with SignalR.</summary>
public interface INotificationPusher
{
    Task PushAsync(Notification notification, CancellationToken ct);
}

public sealed class NullNotificationPusher : INotificationPusher
{
    public Task PushAsync(Notification notification, CancellationToken ct) => Task.CompletedTask;
}

public sealed class InAppChannel(INotificationPusher pusher) : INotificationChannel
{
    public string Name => ChannelNames.InApp;
    public async Task<string?> SendAsync(Notification notification, CancellationToken ct)
    {
        await pusher.PushAsync(notification, ct);
        return null;
    }
}

/// <summary>Message text per template and language (NT-01). A missing Urdu text falls back to English.</summary>
public static class NotificationTemplates
{
    public const string ComplaintReceived = "complaint_received";
    public const string ComplaintAssigned = "complaint_assigned";
    public const string ComplaintRejected = "complaint_rejected";
    public const string ConfirmationRequest = "confirmation_request";
    public const string JobAssigned = "job_assigned";
    public const string JobAwaitingSignOff = "job_awaiting_signoff";
    public const string JobReturned = "job_returned";
    /// <summary>One template per escalation rule, so the text is right in both languages.</summary>
    public static string Escalation(string rule) => "escalation_" + rule;
    public const string ApprovalRequested = "approval_requested";
    public const string ApprovalApproved = "approval_approved";
    public const string ApprovalRejected = "approval_rejected";
    public const string CostOverrun = "cost_overrun";
    public const string IncidentCompleted = "incident_completed";
    public const string StockLow = "stock_low";
    public const string StockRequested = "stock_requested";
    public const string StockIssued = "stock_issued";
    public const string StockRefused = "stock_refused";
    public const string MaterialsMismatch = "materials_mismatch";
    public const string SafetyAlert = "safety_alert";
    public const string SafetyPrompt = "safety_prompt";
    /// <summary>A notice carries the sender's own words, so the template only places them.</summary>
    public const string Notice = "notice";
    public const string MonthlySummary = "monthly_summary";

    private static readonly Dictionary<string, Dictionary<string, (string Title, string Body)>> All = new()
    {
        [ComplaintReceived] = new()
        {
            ["en"] = ("Complaint received", "Your complaint {reference} has been received. We will review it shortly."),
            ["ur"] = ("شکایت موصول ہو گئی", "آپ کی شکایت {reference} موصول ہو گئی ہے۔ ہم جلد اس کا جائزہ لیں گے۔"),
        },
        [ComplaintAssigned] = new()
        {
            ["en"] = ("Complaint assigned", "Your complaint {reference} has been assigned to our team."),
            ["ur"] = ("شکایت تفویض کر دی گئی", "آپ کی شکایت {reference} ہماری ٹیم کو سونپ دی گئی ہے۔"),
        },
        [ComplaintRejected] = new()
        {
            ["en"] = ("Complaint not accepted", "Your complaint {reference} was not accepted. Reason: {reason}"),
            ["ur"] = ("شکایت قبول نہیں کی گئی", "آپ کی شکایت {reference} قبول نہیں کی گئی۔ وجہ: {reason}"),
        },
        [ConfirmationRequest] = new()
        {
            ["en"] = ("Please confirm", "Work on {reference} is complete. Please confirm that the problem is fixed."),
            ["ur"] = ("براہ کرم تصدیق کریں", "{reference} پر کام مکمل ہو گیا ہے۔ براہ کرم تصدیق کریں کہ مسئلہ حل ہو گیا ہے۔"),
        },
        [JobAssigned] = new()
        {
            ["en"] = ("New job", "Job {number} for complaint {reference} is assigned to you."),
            ["ur"] = ("نیا کام", "شکایت {reference} کا کام {number} آپ کو سونپا گیا ہے۔"),
        },
        [JobAwaitingSignOff] = new()
        {
            ["en"] = ("Job awaiting sign-off", "Job {number} ({reference}) is finished and needs your review."),
            ["ur"] = ("کام منظوری کا منتظر", "کام {number} ({reference}) مکمل ہو گیا ہے اور آپ کے جائزے کا منتظر ہے۔"),
        },
        [JobReturned] = new()
        {
            ["en"] = ("Job could not be completed", "Job {number} ({reference}) was handed back. Reason: {reason}"),
            ["ur"] = ("کام مکمل نہیں ہو سکا", "کام {number} ({reference}) واپس کر دیا گیا۔ وجہ: {reason}"),
        },
        [Escalation(EscalationRuleNames.NotAssigned)] = new()
        {
            ["en"] = ("Complaint not assigned", "{reference} has not been assigned in time (escalation level {level})."),
            ["ur"] = ("شکایت تفویض نہیں ہوئی", "{reference} وقت پر تفویض نہیں ہوئی (درجہ {level})۔"),
        },
        [Escalation(EscalationRuleNames.NotAccepted)] = new()
        {
            ["en"] = ("Job not accepted", "The job for {reference} has not been accepted (escalation level {level})."),
            ["ur"] = ("کام قبول نہیں ہوا", "{reference} کا کام قبول نہیں کیا گیا (درجہ {level})۔"),
        },
        [Escalation(EscalationRuleNames.TargetPassed)] = new()
        {
            ["en"] = ("Complaint overdue", "{reference} is past its target time (escalation level {level})."),
            ["ur"] = ("شکایت تاخیر کا شکار", "{reference} مقررہ وقت سے تجاوز کر گئی ہے (درجہ {level})۔"),
        },
        [Escalation(EscalationRuleNames.AwaitingSignOff)] = new()
        {
            ["en"] = ("Work waiting for sign-off", "Finished work on {reference} is still waiting for sign-off (escalation level {level})."),
            ["ur"] = ("کام منظوری کا منتظر", "{reference} کا مکمل کام ابھی تک منظوری کا منتظر ہے (درجہ {level})۔"),
        },
        [Escalation(EscalationRuleNames.EstimateWaiting)] = new()
        {
            ["en"] = ("Estimate waiting too long", "An estimate for {reference} has been waiting for approval too long (escalation level {level})."),
            ["ur"] = ("تخمینہ بہت دیر سے منتظر ہے", "{reference} کا تخمینہ منظوری کے لیے بہت دیر سے منتظر ہے (درجہ {level})۔"),
        },
        [Escalation(EscalationRuleNames.Dispute)] = new()
        {
            ["en"] = ("Resident disputed the result", "The resident reopened {reference} and is waiting for a person to act (escalation level {level})."),
            ["ur"] = ("رہائشی نے نتیجے سے اختلاف کیا", "رہائشی نے {reference} دوبارہ کھولی ہے اور کارروائی کا منتظر ہے (درجہ {level})۔"),
        },
        [ApprovalRequested] = new()
        {
            ["en"] = ("Estimate waiting for approval", "Job {number} has an estimate of Rs {amount} that needs your approval before work can start."),
            ["ur"] = ("تخمینہ منظوری کا منتظر", "کام {number} کا Rs {amount} کا تخمینہ آپ کی منظوری کا منتظر ہے، اس کے بغیر کام شروع نہیں ہو سکتا۔"),
        },
        [ApprovalApproved] = new()
        {
            ["en"] = ("Estimate approved", "The estimate for job {number} was approved. Work can start."),
            ["ur"] = ("تخمینہ منظور", "کام {number} کا تخمینہ منظور ہو گیا۔ کام شروع ہو سکتا ہے۔"),
        },
        [ApprovalRejected] = new()
        {
            ["en"] = ("Estimate not approved", "The estimate for job {number} was not approved: {reason}. Please revise it."),
            ["ur"] = ("تخمینہ منظور نہیں ہوا", "کام {number} کا تخمینہ منظور نہیں ہوا: {reason}۔ براہ کرم اسے درست کریں۔"),
        },
        [IncidentCompleted] = new()
        {
            ["en"] = ("The problem you reported is fixed", "The work on the problem you reported ({reference}) is complete. It was dealt with together with other reports of the same problem."),
            ["ur"] = ("آپ کا بتایا ہوا مسئلہ حل ہو گیا", "آپ کے بتائے ہوئے مسئلے ({reference}) پر کام مکمل ہو گیا ہے۔ اسے اسی مسئلے کی دوسری شکایات کے ساتھ حل کیا گیا۔"),
        },
        [StockLow] = new()
        {
            ["en"] = ("Low stock", "{item} is down to {quantity} (alert level {amount}). Please reorder."),
            ["ur"] = ("اسٹاک کم ہے", "{item} صرف {quantity} رہ گیا ہے (انتباہی سطح {amount})۔ براہ کرم منگوائیں۔"),
        },
        [StockRequested] = new()
        {
            ["en"] = ("Materials requested", "A technician has asked for materials for job {number}."),
            ["ur"] = ("سامان کی درخواست", "ایک ٹیکنیشن نے کام {number} کے لیے سامان مانگا ہے۔"),
        },
        [StockIssued] = new()
        {
            ["en"] = ("Materials issued", "Your materials for job {number} have been issued from the store."),
            ["ur"] = ("سامان جاری کر دیا گیا", "کام {number} کا آپ کا سامان اسٹور سے جاری کر دیا گیا ہے۔"),
        },
        [StockRefused] = new()
        {
            ["en"] = ("Materials not issued", "Your request for job {number} was not met: {reason}"),
            ["ur"] = ("سامان جاری نہیں ہوا", "کام {number} کی آپ کی درخواست پوری نہیں ہوئی: {reason}"),
        },
        [MaterialsMismatch] = new()
        {
            ["en"] = ("Materials do not add up", "On job {number}, {quantity} material line(s) differ between what was issued and what was recorded as used."),
            ["ur"] = ("سامان کا حساب نہیں ملتا", "کام {number} پر {quantity} مد میں جاری شدہ اور استعمال شدہ سامان میں فرق ہے۔"),
        },
        [CostOverrun] = new()
        {
            ["en"] = ("Job cost over the estimate", "Job {number} ({reference}) has cost {percent}% more than estimated. Please review."),
            ["ur"] = ("کام کی لاگت تخمینے سے زیادہ", "کام {number} ({reference}) کی لاگت تخمینے سے {percent}% زیادہ ہے۔ براہ کرم جائزہ لیں۔"),
        },
        [SafetyAlert] = new()
        {
            ["en"] = ("Safety complaint", "{reference} may involve a danger to people. It needs a person to act now."),
            ["ur"] = ("حفاظتی شکایت", "{reference} میں لوگوں کے لیے خطرہ ہو سکتا ہے۔ فوری طور پر کسی کو کارروائی کرنی ہے۔"),
        },
        [MonthlySummary] = new()
        {
            ["en"] = ("Monthly summary ready", "The management summary for {period} is ready to read."),
            ["ur"] = ("ماہانہ خلاصہ تیار ہے", "{period} کا انتظامی خلاصہ پڑھنے کے لیے تیار ہے۔"),
        },
        [Notice] = new()
        {
            ["en"] = ("{title}", "{body}"),
            ["ur"] = ("{title}", "{body}"),
        },
        [SafetyPrompt] = new()
        {
            ["en"] = ("If anyone is in danger", "We have flagged {reference} as urgent for safety. If anyone is in danger, call Rescue 1122 now. Keep away from the hazard."),
            ["ur"] = ("اگر کسی کی جان خطرے میں ہے", "ہم نے {reference} کو حفاظت کے لیے فوری قرار دیا ہے۔ اگر کسی کی جان خطرے میں ہو تو ابھی ریسکیو 1122 پر کال کریں۔ خطرے والی جگہ سے دور رہیں۔"),
        },
    };

    public static IEnumerable<string> Keys => All.Keys;

    public static (string Title, string Body) Render(string template, string locale, IReadOnlyDictionary<string, string> values)
    {
        var texts = All[template];
        var (title, body) = texts.TryGetValue(locale, out var t) ? t : texts["en"];
        string Fill(string s) => values.Aggregate(s, (acc, kv) => acc.Replace("{" + kv.Key + "}", kv.Value, StringComparison.Ordinal));
        return (Fill(title), Fill(body));
    }
}

/// <param name="ValuesByLocale">Values to use instead of <paramref name="Values"/> for a recipient whose language is listed (a notice written in both English and Urdu).</param>
public sealed record Recipient(Guid UserId, string Template, IReadOnlyDictionary<string, string> Values, string? Link,
    Guid? NoticeId = null, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? ValuesByLocale = null);

/// <summary>Creates the stored notification and its delivery rows, then attempts each channel (NT-01).</summary>
public sealed class NotificationService(AppDbContext db, IEnumerable<INotificationChannel> channels, ILogger<NotificationService> log)
{
    private readonly Dictionary<string, INotificationChannel> _channels = channels.ToDictionary(c => c.Name);

    public IReadOnlyCollection<string> AvailableChannels => _channels.Keys;

    /// <summary>
    /// Idempotent for one event: the same event handled twice reuses the stored notification and only retries deliveries
    /// that have not succeeded. Throws after saving if any delivery failed, so the outbox retries the event.
    /// </summary>
    public async Task SendAsync(Guid eventId, Guid organizationId, IEnumerable<Recipient> recipients, CancellationToken ct)
    {
        var list = recipients.GroupBy(r => (r.UserId, r.Template)).Select(g => g.First()).ToList();
        if (list.Count == 0) return;

        var orgLocale = await db.OrgSettings.AsNoTracking().Select(s => s.Locale).SingleOrDefaultAsync(ct) ?? "en";
        var ids = list.Select(r => r.UserId).ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id) && u.IsActive).ToDictionaryAsync(u => u.Id, ct);

        var failures = new List<string>();
        foreach (var r in list)
        {
            if (!users.TryGetValue(r.UserId, out var user)) continue;
            var key = $"{eventId:N}:{r.Template}:{r.UserId:N}";
            var n = await db.Notifications.SingleOrDefaultAsync(x => x.DedupeKey == key, ct);
            if (n is null)
            {
                var locale = user.PreferredLocale ?? orgLocale;
                var values = r.ValuesByLocale is not null && r.ValuesByLocale.TryGetValue(locale, out var own) ? own : r.Values;
                var (title, body) = NotificationTemplates.Render(r.Template, locale, values);
                n = new Notification { OrganizationId = organizationId, UserId = r.UserId, Template = r.Template, Locale = locale, Title = title, Body = body, Link = r.Link, DedupeKey = key,
                    TemplateValues = System.Text.Json.JsonSerializer.Serialize(values), NoticeId = r.NoticeId };
                db.Notifications.Add(n);
                // In-app is always on; other channels follow the user's choice and what is installed.
                foreach (var channel in user.NotifyChannels.Append(ChannelNames.InApp).Distinct())
                    db.NotificationDeliveries.Add(new NotificationDelivery { OrganizationId = organizationId, NotificationId = n.Id, Channel = channel });
                await db.SaveChangesAsync(ct);
            }

            var pending = await db.NotificationDeliveries.Where(d => d.NotificationId == n.Id && (d.Status == "Pending" || d.Status == "Failed")).ToListAsync(ct);
            foreach (var d in pending)
            {
                if (!_channels.TryGetValue(d.Channel, out var channel))
                {
                    // Not installed: retrying cannot fix it, so record it and move on.
                    d.Status = "Failed"; d.LastError = "channel_not_available"; d.LastAttemptAt = DateTimeOffset.UtcNow;
                    continue;
                }
                d.Attempts++;
                d.LastAttemptAt = DateTimeOffset.UtcNow;
                try
                {
                    d.ProviderMessageId = await channel.SendAsync(n, ct);
                    d.Status = "Sent"; d.SentAt = DateTimeOffset.UtcNow; d.LastError = null;
                }
                catch (PermanentChannelFailure permanent)
                {
                    // Retrying cannot help. WhatsApp trouble falls back to a text message (NT-03).
                    d.Status = "Failed"; d.LastError = permanent.Message;
                    log.LogInformation("Notification {Id} via {Channel} not sent: {Reason}", n.Id, d.Channel, permanent.Message);
                    if (d.Channel == "whatsapp") await FallBackToSmsAsync(d, ct);
                }
                catch (Exception ex)
                {
                    d.Status = "Failed";
                    d.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                    failures.Add($"{d.Channel}: {ex.Message}");
                    log.LogWarning(ex, "Notification {Id} via {Channel} failed (attempt {Attempt})", n.Id, d.Channel, d.Attempts);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        if (failures.Count > 0) throw new InvalidOperationException("Notification delivery failed: " + string.Join("; ", failures));
    }

    /// <summary>
    /// WhatsApp could not deliver (NT-03): send the same message as a text. Done once per notification, and only when a text
    /// gateway is installed. A failure here is recorded on its own delivery row; it never undoes the original.
    /// </summary>
    public async Task FallBackToSmsAsync(NotificationDelivery failed, CancellationToken ct)
    {
        if (!_channels.TryGetValue("sms", out var sms)) return;
        // Someone who sent STOP asked for no more messages; a text message would be the same thing by another road.
        if (failed.LastError == "opted_out") return;
        if (await db.NotificationDeliveries.AnyAsync(x => x.NotificationId == failed.NotificationId && x.Channel == "sms", ct)) return;
        var n = await db.Notifications.SingleAsync(x => x.Id == failed.NotificationId, ct);

        var fallback = new NotificationDelivery { OrganizationId = failed.OrganizationId, NotificationId = n.Id, Channel = "sms", Attempts = 1, LastAttemptAt = DateTimeOffset.UtcNow };
        db.NotificationDeliveries.Add(fallback);
        try
        {
            fallback.ProviderMessageId = await sms.SendAsync(n, ct);
            fallback.Status = "Sent"; fallback.SentAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            fallback.Status = "Failed"; fallback.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            log.LogWarning(ex, "Text-message fallback for notification {Id} failed", n.Id);
        }
    }

    public async Task<IReadOnlyList<Guid>> UsersInRoleAsync(Role role, CancellationToken ct) =>
        await db.Memberships.AsNoTracking().Where(m => m.IsActive && m.Role == role).Select(m => m.UserId).Distinct().ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> UsersWithAsync(string permission, CancellationToken ct) =>
        (await db.Memberships.AsNoTracking().Where(m => m.IsActive).Select(m => new { m.UserId, m.Role }).ToListAsync(ct))
            .Where(m => RolePermissions.Allows(m.Role, permission)).Select(m => m.UserId).Distinct().ToList();
}

/// <summary>Turns lifecycle events into messages for the right people in their own language (NT-06).</summary>
public sealed class LifecycleNotifications(NotificationService notifications, AppDbContext db) :
    IEventHandler<ComplaintCreated>, IEventHandler<ComplaintStatusChanged>, IEventHandler<JobAssigned>,
    IEventHandler<JobStatusChanged>, IEventHandler<EscalationRaised>, IEventHandler<ComplaintSafetyFlagged>, IEventHandler<ConfirmationReminder>,
    IEventHandler<ApprovalRequested>, IEventHandler<ApprovalDecided>, IEventHandler<CostOverrun>,
    IEventHandler<StockLow>, IEventHandler<StockRequested>, IEventHandler<StockRequestDecided>, IEventHandler<MaterialsMismatch>
{
    private static Dictionary<string, string> V(params (string, string?)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2 ?? "");

    private async Task<Guid?> ReporterAsync(Guid complaintId, CancellationToken ct) =>
        await db.Complaints.AsNoTracking().Where(c => c.Id == complaintId).Select(c => c.ReportedByUserId).SingleOrDefaultAsync(ct);

    private async Task ToReporter(IDomainEvent e, Guid complaintId, string template, Dictionary<string, string> values, CancellationToken ct)
    {
        if (await ReporterAsync(complaintId, ct) is { } reporter)
            await notifications.SendAsync(e.EventId, e.OrganizationId, [new Recipient(reporter, template, values, $"/complaints/{complaintId}")], ct);
    }

    public Task Handle(ComplaintCreated e, CancellationToken ct) =>
        ToReporter(e, e.ComplaintId, NotificationTemplates.ComplaintReceived, V(("reference", e.Reference)), ct);

    public Task Handle(ComplaintStatusChanged e, CancellationToken ct)
    {
        var template = (e.From, e.To) switch
        {
            (ComplaintStatus.UnderReview, ComplaintStatus.Assigned) => NotificationTemplates.ComplaintAssigned,
            (_, ComplaintStatus.Rejected) => NotificationTemplates.ComplaintRejected,
            (_, ComplaintStatus.Resolved) => NotificationTemplates.ConfirmationRequest,
            _ => null,
        };
        return template is null ? Task.CompletedTask : HandleStatus(e, template, ct);
    }

    private async Task HandleStatus(ComplaintStatusChanged e, string template, CancellationToken ct)
    {
        await ToReporter(e, e.ComplaintId, template, V(("reference", e.Reference), ("reason", e.Reason)), ct);

        // Everyone who reported the same incident is told too, in their own words and under their own reference (DP-05).
        var incident = await db.Complaints.AsNoTracking().Where(c => c.Id == e.ComplaintId).Select(c => c.IncidentId).SingleOrDefaultAsync(ct);
        if (incident is null || template is not (NotificationTemplates.ComplaintAssigned or NotificationTemplates.ConfirmationRequest)) return;
        var siblingTemplate = template == NotificationTemplates.ConfirmationRequest ? NotificationTemplates.IncidentCompleted : template;
        var siblings = await db.Complaints.AsNoTracking().Where(c => c.IncidentId == incident && c.Id != e.ComplaintId && c.ReportedByUserId != null).Select(c => new { c.Id, c.Reference, User = c.ReportedByUserId!.Value }).ToListAsync(ct);
        foreach (var sibling in siblings)
            await notifications.SendAsync(e.EventId, e.OrganizationId, [new Recipient(sibling.User, siblingTemplate, V(("reference", sibling.Reference)), $"/complaints/{sibling.Id}")], ct);
    }

    public async Task Handle(JobAssigned e, CancellationToken ct)
    {
        if (e.TechnicianId is not { } tech) return;
        var reference = await db.Complaints.AsNoTracking().Where(c => c.Id == e.ComplaintId).Select(c => c.Reference).SingleOrDefaultAsync(ct) ?? "";
        await notifications.SendAsync(e.EventId, e.OrganizationId,
            [new Recipient(tech, NotificationTemplates.JobAssigned, V(("number", e.Number), ("reference", reference)), $"/jobs/{e.JobId}")], ct);
    }

    public async Task Handle(JobStatusChanged e, CancellationToken ct)
    {
        var template = e.To switch
        {
            JobStatus.PendingVerification => NotificationTemplates.JobAwaitingSignOff,
            JobStatus.Returned => NotificationTemplates.JobReturned,
            _ => null,
        };
        if (template is null) return;
        var reference = await db.Complaints.AsNoTracking().Where(c => c.Id == e.ComplaintId).Select(c => c.Reference).SingleOrDefaultAsync(ct) ?? "";
        var supervisors = await notifications.UsersWithAsync(Permissions.ComplaintClose, ct);
        await notifications.SendAsync(e.EventId, e.OrganizationId,
            supervisors.Select(u => new Recipient(u, template, V(("number", e.Number), ("reference", reference), ("reason", e.Reason)), $"/jobs/{e.JobId}")), ct);
    }

    /// <summary>The resident gets the emergency prompt and every supervisor an alert (RT-05).</summary>
    public async Task Handle(ComplaintSafetyFlagged e, CancellationToken ct)
    {
        var values = V(("reference", e.Reference));
        var link = $"/complaints/{e.ComplaintId}";
        var recipients = (await notifications.UsersWithAsync(Permissions.ComplaintAssign, ct))
            .Select(u => new Recipient(u, NotificationTemplates.SafetyAlert, values, link)).ToList();
        if (await ReporterAsync(e.ComplaintId, ct) is { } reporter) recipients.Add(new Recipient(reporter, NotificationTemplates.SafetyPrompt, values, link));
        await notifications.SendAsync(e.EventId, e.OrganizationId, recipients, ct);
    }

    /// <summary>Managers and committee members are asked to approve (ID-10, JC-03).</summary>
    public async Task Handle(ApprovalRequested e, CancellationToken ct)
    {
        var people = (await notifications.UsersInRoleAsync(Role.Manager, ct)).Concat(await notifications.UsersInRoleAsync(Role.Committee, ct)).Distinct();
        var values = V(("number", e.JobNumber), ("amount", e.Amount.ToString("0.00", CultureInfo.InvariantCulture)));
        await notifications.SendAsync(e.EventId, e.OrganizationId, people.Select(u => new Recipient(u, NotificationTemplates.ApprovalRequested, values, "/approvals")), ct);
    }

    /// <summary>The person who proposed the estimate, and the supervisors, learn the answer.</summary>
    public async Task Handle(ApprovalDecided e, CancellationToken ct)
    {
        var people = (await notifications.UsersWithAsync(Permissions.ComplaintAssign, ct)).Append(e.RequestedBy).Distinct();
        var template = e.Approved ? NotificationTemplates.ApprovalApproved : NotificationTemplates.ApprovalRejected;
        var values = V(("number", e.JobNumber), ("reason", e.Note ?? ""));
        await notifications.SendAsync(e.EventId, e.OrganizationId, people.Select(u => new Recipient(u, template, values, $"/jobs/{e.JobId}")), ct);
    }

    public async Task Handle(StockLow e, CancellationToken ct)
    {
        var values = V(("item", e.ItemName), ("quantity", e.Quantity.ToString("0.###", CultureInfo.InvariantCulture)), ("amount", e.Threshold.ToString("0.###", CultureInfo.InvariantCulture)));
        await notifications.SendAsync(e.EventId, e.OrganizationId, (await notifications.UsersWithAsync(Permissions.StockManage, ct)).Select(u => new Recipient(u, NotificationTemplates.StockLow, values, "/stock")), ct);
    }

    public async Task Handle(StockRequested e, CancellationToken ct) =>
        await notifications.SendAsync(e.EventId, e.OrganizationId, (await notifications.UsersWithAsync(Permissions.StockManage, ct))
            .Select(u => new Recipient(u, NotificationTemplates.StockRequested, V(("number", e.JobNumber)), "/stock/requests")), ct);

    public Task Handle(StockRequestDecided e, CancellationToken ct) =>
        notifications.SendAsync(e.EventId, e.OrganizationId,
            [new Recipient(e.RequestedBy, e.Fulfilled ? NotificationTemplates.StockIssued : NotificationTemplates.StockRefused, V(("number", e.JobNumber), ("reason", e.Note)), $"/jobs/{e.JobId}")], ct);

    public async Task Handle(MaterialsMismatch e, CancellationToken ct) =>
        await notifications.SendAsync(e.EventId, e.OrganizationId, (await notifications.UsersWithAsync(Permissions.ComplaintAssign, ct))
            .Select(u => new Recipient(u, NotificationTemplates.MaterialsMismatch, V(("number", e.JobNumber), ("quantity", e.Items.ToString(CultureInfo.InvariantCulture))), $"/jobs/{e.JobId}")), ct);

    public async Task Handle(CostOverrun e, CancellationToken ct)
    {
        var people = await notifications.UsersWithAsync(Permissions.ComplaintAssign, ct);
        var values = V(("number", e.JobNumber), ("reference", e.Reference), ("percent", e.Percent.ToString("0.#", CultureInfo.InvariantCulture)));
        await notifications.SendAsync(e.EventId, e.OrganizationId, people.Select(u => new Recipient(u, NotificationTemplates.CostOverrun, values, $"/jobs/{e.JobId}")), ct);
    }

    public Task Handle(ConfirmationReminder e, CancellationToken ct) =>
        ToReporter(e, e.ComplaintId, NotificationTemplates.ConfirmationRequest, V(("reference", e.Reference)), ct);

    public async Task Handle(EscalationRaised e, CancellationToken ct)
    {
        var people = await notifications.UsersInRoleAsync(e.NotifyRole, ct);
        var values = V(("reference", e.Reference), ("level", e.Level.ToString(CultureInfo.InvariantCulture)));
        var link = e.JobId is { } j ? $"/jobs/{j}" : $"/complaints/{e.ComplaintId}";
        await notifications.SendAsync(e.EventId, e.OrganizationId,
            people.Select(u => new Recipient(u, NotificationTemplates.Escalation(e.Rule), values, link)), ct);
    }
}
