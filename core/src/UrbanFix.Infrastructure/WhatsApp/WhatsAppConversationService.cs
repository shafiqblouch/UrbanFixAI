using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>
/// Handles one stored WhatsApp message (WA-03 to WA-06): applies the conversation rules, then does what they say, saving photos
/// and voice notes to the complaint, finding status by number, and answering in the person's language. Replies are sent last, so
/// a failure to send never loses the complaint.
/// </summary>
public sealed class WhatsAppConversationService(
    AppDbContext db, ITenantContext tenant, IWhatsAppClient client, WhatsAppSender sender, ComplaintIntake intake, ComplaintPlanner planner,
    IMediaProcessor media, IEventBus bus, WhatsAppOptions options, ILogger<WhatsAppConversationService> log)
{
    /// <summary>Stands in for the description of a complaint sent as just a photo or a voice note; replaced when a voice note is transcribed.</summary>
    public const string PlaceholderDescription = IntakeText.MediaOnly;

    /// <summary>A pin within this distance of a known place is taken to be that place (CP-05).</summary>
    public const double PinMatchMetres = 150;

    private static readonly (string Id, string TitleKey)[] MenuButtons = [("report", "btn.report"), ("status", "btn.status"), ("lang", "btn.language")];
    private static readonly (string Id, string TitleKey)[] DoneButton = [("done", "btn.done")];

    private sealed record Reply(string Text, IReadOnlyList<(string Id, string Title)>? Buttons);

    /// <summary>The replies being built for one message, in the person's language.</summary>
    private sealed class Turn(string language, string society)
    {
        public string Language { get; set; } = language;
        public List<Reply> Replies { get; } = [];
        private string Text(string key, (string, string?)[] args) => WhatsAppScripts.Get(key, Language, [.. args, ("society", society)]);
        public void Say(string key, params (string, string?)[] args) => Replies.Add(new Reply(Text(key, args), null));
        public void Buttons(string key, IEnumerable<(string Id, string TitleKey)> buttons, params (string, string?)[] args) =>
            Replies.Add(new Reply(Text(key, args), buttons.Select(b => (b.Id, WhatsAppScripts.Get(b.TitleKey, Language))).ToList()));
    }

    public async Task ProcessAsync(Guid organizationId, Guid messageId, CancellationToken ct)
    {
        await db.ChangeTenantAsync(organizationId, null, ct);
        var message = await db.WhatsAppMessages.SingleOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || message.Status != MessageStatus.Received) return; // done already: safe to run twice

        var phone = "+" + message.WaId;
        var conversation = await sender.ConversationAsync(phone, ct);
        var now = DateTimeOffset.UtcNow;

        // Too many messages from one phone in a short time: ignore the rest, and say so once (WA-06).
        var since = now.AddMinutes(-options.RateWindowMinutes);
        var recent = await db.WhatsAppMessages.CountAsync(m => m.Direction == "in" && m.WaId == message.WaId && m.CreatedAt > since && m.Id != message.Id, ct);
        if (recent >= options.MaxMessagesPerWindow)
        {
            message.Status = MessageStatus.Ignored; message.Error = "rate_limited"; message.ProcessedAt = now;
            var told = await db.WhatsAppMessages.AnyAsync(m => m.Direction == "out" && m.WaId == message.WaId && m.Error == "rate_limit_notice" && m.CreatedAt > since, ct);
            if (!told)
            {
                var r = await sender.ReplyAsync(phone, WhatsAppScripts.Get("slow_down", conversation.Language ?? "en"), null, ct);
                if (r.Outcome == SendOutcome.Sent)
                    db.WhatsAppMessages.Local.Where(m => m.Direction == "out" && m.WaMessageId == r.ProviderMessageId).ToList().ForEach(m => m.Error = "rate_limit_notice");
            }
            await db.SaveChangesAsync(ct);
            return;
        }

        var turn = new Turn(conversation.Language ?? "en", await SocietyNameAsync(ct));
        try
        {
            var data = ChatData.FromJson(conversation.StateData);
            var gap = conversation.LastInboundAt is { } last ? now - last : TimeSpan.Zero;
            var step = ConversationMachine.Advance(new ChatSnapshot(conversation.State, conversation.Language, data, gap), ToInput(message), options.ResumeAfterHours);

            turn.Language = step.Language;
            var state = step.State;
            var newData = step.Data;

            foreach (var action in step.Actions)
            {
                switch (action)
                {
                    case Say s: turn.Say(s.Key, s.Args); break;
                    case SayWithButtons sb: turn.Buttons(sb.Key, sb.Buttons, sb.Args); break;
                    case OptOut: conversation.OptedOutAt = now; break;
                    case OptIn: conversation.OptedOutAt = null; conversation.OptedInAt ??= now; break;
                    case OpenComplaint c: (state, newData) = await CreateAsync(c, phone, turn, state, newData, ct); break;
                    case AppendDescription a: await AppendAsync(a.Text, data, turn, ct); break;
                    case AttachMedia m: await AttachAsync(m.Media, data, turn, ct); break;
                    case SetPin p:
                        newData.AwaitingAddress = await PinAsync(await LoadComplaintAsync(data.ComplaintId, ct), p.Latitude, p.Longitude, p.Name, ct);
                        turn.Say("location_pin");
                        if (newData.AwaitingAddress) turn.Say("needs_location");
                        break;
                    case SetAddressText t:
                        (await LoadComplaintAsync(data.ComplaintId, ct)).LocationText = t.Text.Length > 300 ? t.Text[..300] : t.Text;
                        turn.Say("location_text");
                        break;
                    case LookupStatus l: await StatusAsync(l.Reference, phone, turn, ct); break;
                    case FinishComplaint: turn.Say("done", ("reference", data.Reference ?? "")); break;
                }
            }

            conversation.State = state;
            conversation.StateData = newData.ToJson();
            conversation.Language = turn.Language;
            conversation.ComplaintId = newData.ComplaintId ?? conversation.ComplaintId;
            message.Status = MessageStatus.Processed;
            message.ComplaintId = conversation.ComplaintId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never retried blindly: a half-done action must not run twice (a second complaint). The person is told, the row shows why.
            log.LogError(ex, "WhatsApp message {Id} could not be handled", messageId);
            message.Status = MessageStatus.Failed;
            message.Error = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            turn.Replies.Clear();
            turn.Say("error");
        }

        conversation.LastInboundAt = now;
        conversation.OptedInAt ??= now; // they wrote to us first, so replies are expected
        conversation.UpdatedAt = now;
        message.ProcessedAt = now;
        await db.SaveChangesAsync(ct);

        foreach (var reply in turn.Replies) await sender.ReplyAsync(phone, reply.Text, reply.Buttons, ct);
        await db.SaveChangesAsync(ct);
    }

    private static ChatInput ToInput(WhatsAppMessage m)
    {
        var parsed = m.Raw is null ? null : WhatsAppPayload.ParseSingle(m.Raw);
        return m.Type switch
        {
            "text" => new ChatInput(InputKind.Text, m.Body),
            "interactive" or "button" => new ChatInput(InputKind.Button, m.Body, parsed?.ButtonId),
            "image" when m.MediaId is not null => new ChatInput(InputKind.Image, m.Body, MediaId: m.MediaId),
            "audio" when m.MediaId is not null => new ChatInput(InputKind.Audio, MediaId: m.MediaId),
            "location" when parsed is { Latitude: not null, Longitude: not null } => new ChatInput(InputKind.Location, Latitude: parsed.Latitude, Longitude: parsed.Longitude, PlaceName: parsed.PlaceName),
            _ => new ChatInput(InputKind.Unsupported),
        };
    }

    private async Task<string> SocietyNameAsync(CancellationToken ct)
    {
        var shown = await db.OrgSettings.AsNoTracking().Select(s => s.DisplayName).SingleOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(shown)) return shown;
        var org = tenant.OrganizationId;
        return await db.Organizations.AsNoTracking().Where(o => o.Id == org).Select(o => o.Name).SingleOrDefaultAsync(ct) ?? "UrbanFix";
    }

    private async Task<Complaint> LoadComplaintAsync(Guid? id, CancellationToken ct) =>
        id is { } cid && await db.Complaints.SingleOrDefaultAsync(c => c.Id == cid, ct) is { } c ? c : throw new InvalidOperationException("The chat has no complaint to add to");

    // Creating a complaint ---------------------------------------------------------------------------------------------

    private async Task<(string State, ChatData Data)> CreateAsync(OpenComplaint c, string phone, Turn turn, string state, ChatData data, CancellationToken ct)
    {
        var settings = await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct);
        var decision = await SenderResolver.ResolveAsync(db, phone, OrgPolicy.From(settings ?? "{}"), ct);
        if (decision.Kind == SenderKind.RegistrationRequired)
        {
            turn.Say("not_registered");
            return (ChatStates.Idle, new ChatData());
        }

        var matched = decision.Kind == SenderKind.Matched;
        var organizationId = tenant.OrganizationId!.Value;
        var (made, error) = await intake.CreateAsync(new IntakeRequest(
            organizationId, "whatsapp", c.DescriptionIsPlaceholder ? PlaceholderDescription : c.Description, null, null, null, null,
            matched ? decision.UserId : null, null, phone, matched, decision.Kind == SenderKind.UnverifiedQueue, null, null), ct);
        if (error is not null) throw new InvalidOperationException($"Complaint could not be stored: {error}");
        var complaint = made!.Complaint;

        var failedMedia = 0;
        foreach (var m in c.Media) if (!await SaveMediaAsync(complaint, m, ct)) failedMedia++;
        var awaitingAddress = c.PinLatitude is { } lat && c.PinLongitude is { } lon
            ? await PinAsync(complaint, lat, lon, c.PinName, ct)
            : complaint.HoldReason == "location_missing";

        turn.Buttons("created", DoneButton, ("reference", complaint.Reference));
        if (failedMedia > 0) turn.Say("media_failed");
        if (awaitingAddress) turn.Say("needs_location");
        if (decision.Kind == SenderKind.UnverifiedQueue) turn.Say("sender_check");
        if (complaint.SafetyFlag) turn.Say("emergency");

        return (ChatStates.Collecting, new ChatData { ComplaintId = complaint.Id, Reference = complaint.Reference, AwaitingAddress = awaitingAddress });
    }

    private async Task AppendAsync(string text, ChatData data, Turn turn, CancellationToken ct)
    {
        var complaint = await LoadComplaintAsync(data.ComplaintId, ct);
        var combined = complaint.Description.TrimEnd() + "\n" + text;
        complaint.Description = combined.Length > 4000 ? combined[..4000] : combined;
        complaint.UpdatedAt = DateTimeOffset.UtcNow;
        var danger = SafetyTerms.IsSafety(text) && !complaint.SafetyFlag;
        if (danger) await planner.FlagSafetyAsync(complaint, ct);
        turn.Buttons("detail_added", DoneButton, ("reference", complaint.Reference));
        if (danger) turn.Say("emergency");
    }

    private async Task AttachAsync(PendingMedia pending, ChatData data, Turn turn, CancellationToken ct)
    {
        var complaint = await LoadComplaintAsync(data.ComplaintId, ct);
        if (await SaveMediaAsync(complaint, pending, ct)) turn.Buttons("media_added", DoneButton, ("reference", complaint.Reference));
        else turn.Say("media_failed");
    }

    /// <summary>Fetches a photo or voice note from WhatsApp and attaches it, the same way an uploaded one is (WA-03).</summary>
    private async Task<bool> SaveMediaAsync(Complaint complaint, PendingMedia pending, CancellationToken ct)
    {
        try
        {
            var file = await client.DownloadMediaAsync(pending.MediaId, ct);
            var (saved, error) = await media.ProcessAsync(file.Bytes, complaint.OrganizationId, complaint.Id, complaint.ReportedByUserId,
                async sha => await db.ComplaintMedia.AsNoTracking().SingleOrDefaultAsync(m => m.ComplaintId == complaint.Id && m.Sha256 == sha, ct), ct);
            if (error == "duplicate") return true;
            if (error is not null || saved is null) { log.LogWarning("WhatsApp media {Id} refused: {Error}", pending.MediaId, error); return false; }
            db.ComplaintMedia.Add(saved);
            bus.Publish(new MediaAdded(complaint.OrganizationId, saved.Id, complaint.Id, saved.Kind));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "WhatsApp media {Id} could not be downloaded", pending.MediaId);
            return false;
        }
    }

    // Location pin (CP-05) ---------------------------------------------------------------------------------------------

    public static double DistanceMetres(double lat1, double lon1, double lat2, double lon2) => Geo.DistanceMetres(lat1, lon1, lat2, lon2);

    /// <returns>True when the complaint is still without a place, so the address should be asked for in words.</returns>
    private async Task<bool> PinAsync(Complaint complaint, double latitude, double longitude, string? name, CancellationToken ct)
    {
        var places = await db.Locations.AsNoTracking().Where(l => l.IsActive && l.Latitude != null && l.Longitude != null)
            .Select(l => new { l.Id, l.Latitude, l.Longitude, l.Depth }).ToListAsync(ct);
        var nearest = places.Select(p => (Place: p, Metres: DistanceMetres(latitude, longitude, p.Latitude!.Value, p.Longitude!.Value)))
            .Where(x => x.Metres <= PinMatchMetres).OrderBy(x => x.Metres).ThenByDescending(x => x.Place.Depth).FirstOrDefault();

        complaint.LocationText ??= $"WhatsApp pin {latitude:F5}, {longitude:F5}" + (string.IsNullOrWhiteSpace(name) ? "" : $" ({name})");
        if (nearest.Place is { } match && complaint.LocationId is null)
        {
            complaint.LocationId = match.Id;
            var houses = await db.Houses.AsNoTracking().Where(h => h.LocationId == match.Id).Select(h => h.Id).Take(2).ToListAsync(ct);
            if (houses.Count == 1 && complaint.HouseId is null) complaint.HouseId = houses[0];
        }
        complaint.UpdatedAt = DateTimeOffset.UtcNow;

        // A place is known now: the complaint no longer waits for it.
        if (complaint.LocationId is not null && complaint.Status == ComplaintStatus.OnHold && complaint.HoldReason == "location_missing")
        {
            complaint.Status = ComplaintStatus.New;
            complaint.HoldReason = null;
            db.ComplaintHistory.Add(new ComplaintStatusHistory
            {
                OrganizationId = complaint.OrganizationId, ComplaintId = complaint.Id, FromStatus = ComplaintStatus.OnHold, ToStatus = ComplaintStatus.New, Reason = "Location pin received",
            });
            bus.Publish(new ComplaintStatusChanged(complaint.OrganizationId, complaint.Id, complaint.Reference, ComplaintStatus.OnHold, ComplaintStatus.New, null, "Location pin received"));
        }
        return complaint.LocationId is null && complaint.HouseId is null;
    }

    // Status (WA-05) ---------------------------------------------------------------------------------------------------

    private async Task StatusAsync(string reference, string phone, Turn turn, CancellationToken ct)
    {
        // Only the person who reported it, from the number that reported it, learns the status.
        var view = await ComplaintLookup.StatusByPhoneAsync(db, reference, phone, ct);
        if (view is null) turn.Say("status_not_found");
        else
        {
            var reason = string.IsNullOrWhiteSpace(view.StatusReason) ? "" : WhatsAppScripts.Get("status_reason", turn.Language, ("reason", view.StatusReason));
            turn.Say("status", ("reference", view.Reference), ("status", WhatsAppScripts.StatusText(view.Status, turn.Language)), ("reason", reason));
        }
        turn.Buttons("menu", MenuButtons);
    }
}
