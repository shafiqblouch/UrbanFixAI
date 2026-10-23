using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>One message from a person, as the WhatsApp Cloud API delivers it (WA-02). <c>Raw</c> is the message exactly as received.</summary>
public sealed record InboundMessage(
    string PhoneNumberId, string From, string? ProfileName, string MessageId, DateTimeOffset SentAt, string Type,
    string? Text, string? MediaId, string? MediaMime, string? Caption, double? Latitude, double? Longitude, string? PlaceName,
    string? ButtonId, string? ButtonTitle, string Raw);

/// <summary>Sent / delivered / read / failed for a message we sent.</summary>
public sealed record StatusUpdate(
    string PhoneNumberId, string MessageId, string Status, DateTimeOffset At, string Recipient, string? ErrorCode, string? ErrorTitle);

public sealed record ParsedWebhook(IReadOnlyList<InboundMessage> Messages, IReadOnlyList<StatusUpdate> Statuses);

/// <summary>
/// Reads the webhook body. Tolerant of what it does not know (new fields, new message types) and strict about what it needs:
/// a malformed body throws <see cref="FormatException"/>, so it can be refused.
/// </summary>
public static class WhatsAppPayload
{
    public static ParsedWebhook Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Webhook body is not JSON", ex); }

        using (doc)
        {
            var messages = new List<InboundMessage>();
            var statuses = new List<StatusUpdate>();
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "object") != "whatsapp_business_account") throw new FormatException("Not a WhatsApp Business webhook");
            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array) return new ParsedWebhook(messages, statuses);

            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) continue;
                foreach (var change in changes.EnumerateArray())
                {
                    if (Str(change, "field") != "messages" || !change.TryGetProperty("value", out var value)) continue;
                    var phoneNumberId = value.TryGetProperty("metadata", out var meta) ? Str(meta, "phone_number_id") : null;
                    if (phoneNumberId is null) continue;

                    var names = new Dictionary<string, string?>();
                    if (value.TryGetProperty("contacts", out var contacts) && contacts.ValueKind == JsonValueKind.Array)
                        foreach (var c in contacts.EnumerateArray())
                            if (Str(c, "wa_id") is { } id) names[id] = c.TryGetProperty("profile", out var profile) ? Str(profile, "name") : null;

                    if (value.TryGetProperty("messages", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var m in list.EnumerateArray())
                            if (ReadMessage(m, phoneNumberId, names) is { } message) messages.Add(message);

                    if (value.TryGetProperty("statuses", out var stats) && stats.ValueKind == JsonValueKind.Array)
                        foreach (var s in stats.EnumerateArray())
                            if (ReadStatus(s, phoneNumberId) is { } status) statuses.Add(status);
                }
            }
            return new ParsedWebhook(messages, statuses);
        }
    }

    /// <summary>Reads one stored message (its raw JSON) back into the same shape. The business number is not part of a message, so it is left empty.</summary>
    public static InboundMessage? ParseSingle(string rawMessageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawMessageJson);
            return ReadMessage(doc.RootElement, "", []);
        }
        catch (JsonException) { return null; }
    }

    private static InboundMessage? ReadMessage(JsonElement m, string phoneNumberId, Dictionary<string, string?> names)
    {
        var from = Str(m, "from");
        var id = Str(m, "id");
        var type = Str(m, "type");
        if (from is null || id is null || type is null) return null;

        string? text = null, mediaId = null, mime = null, caption = null, place = null, buttonId = null, buttonTitle = null;
        double? lat = null, lon = null;
        switch (type)
        {
            case "text":
                text = m.TryGetProperty("text", out var t) ? Str(t, "body") : null;
                break;
            case "image" or "audio" or "video" or "document" or "sticker":
                if (m.TryGetProperty(type, out var media))
                {
                    mediaId = Str(media, "id"); mime = Str(media, "mime_type"); caption = Str(media, "caption");
                }
                break;
            case "location":
                if (m.TryGetProperty("location", out var loc))
                {
                    lat = Num(loc, "latitude"); lon = Num(loc, "longitude"); place = Str(loc, "name") ?? Str(loc, "address");
                }
                break;
            case "interactive":
                if (m.TryGetProperty("interactive", out var inter))
                {
                    var reply = inter.TryGetProperty("button_reply", out var b) ? b : inter.TryGetProperty("list_reply", out var l) ? l : default;
                    if (reply.ValueKind == JsonValueKind.Object) { buttonId = Str(reply, "id"); buttonTitle = Str(reply, "title"); }
                }
                break;
            case "button": // quick-reply button on a template message
                if (m.TryGetProperty("button", out var tb)) { buttonId = Str(tb, "payload"); buttonTitle = Str(tb, "text"); }
                break;
        }

        var sent = long.TryParse(Str(m, "timestamp"), CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
        return new InboundMessage(phoneNumberId, from, names.GetValueOrDefault(from), id, sent, type, text, mediaId, mime, caption, lat, lon, place, buttonId, buttonTitle, m.GetRawText());
    }

    private static StatusUpdate? ReadStatus(JsonElement s, string phoneNumberId)
    {
        var id = Str(s, "id");
        var status = Str(s, "status");
        if (id is null || status is null) return null;
        string? code = null, title = null;
        if (s.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var first = errors[0];
            code = first.TryGetProperty("code", out var c) ? c.ToString() : null;
            title = Str(first, "title") ?? Str(first, "message");
        }
        var at = long.TryParse(Str(s, "timestamp"), CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
        return new StatusUpdate(phoneNumberId, id, status, at, Str(s, "recipient_id") ?? "", code, title);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>Checks that a webhook really comes from Meta: <c>X-Hub-Signature-256: sha256=&lt;HMAC of the raw body with the app secret&gt;</c>.</summary>
public static class WhatsAppSignature
{
    public static string Sign(string appSecret, byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), body)).ToLowerInvariant();

    /// <summary>False for a missing or wrong signature, and always false when no secret is configured (fail closed).</summary>
    public static bool Verify(string appSecret, byte[] body, string? header)
    {
        if (string.IsNullOrEmpty(appSecret) || string.IsNullOrEmpty(header)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(appSecret, body));
        var presented = Encoding.ASCII.GetBytes(header.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}

public sealed class WhatsAppOptions
{
    /// <summary>Off by default: nothing is received or sent until it is configured.</summary>
    public bool Enabled { get; set; }
    /// <summary>The Meta app's secret; signs every webhook.</summary>
    public string AppSecret { get; set; } = "";
    /// <summary>The string Meta echoes back when the webhook is first registered.</summary>
    public string VerifyToken { get; set; } = "";
    /// <summary>A system-user token allowed to send messages and download media. One for the platform for now; per-organization tokens come with more societies.</summary>
    public string AccessToken { get; set; } = "";
    public string ApiBase { get; set; } = "https://graph.facebook.com/v21.0";
    /// <summary>Most messages one phone may send in <see cref="RateWindowMinutes"/>; the rest are ignored (WA-06).</summary>
    public int MaxMessagesPerWindow { get; set; } = 30;
    public int RateWindowMinutes { get; set; } = 10;
    /// <summary>After this much quiet, a half-finished report asks whether to carry on (WA-04).</summary>
    public int ResumeAfterHours { get; set; } = 6;
    /// <summary>WhatsApp template names and languages by notification template; see docs/whatsapp-templates.md.</summary>
    public Dictionary<string, WhatsAppTemplate> Templates { get; set; } = [];
}

public sealed class WhatsAppTemplate
{
    public string Name { get; set; } = "";
    /// <summary>Parameter names in the order the template body expects them ({{1}}, {{2}}…), taken from the notification's values.</summary>
    public string[] Parameters { get; set; } = [];
}
