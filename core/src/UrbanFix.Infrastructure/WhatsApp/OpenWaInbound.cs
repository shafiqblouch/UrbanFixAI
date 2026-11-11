using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>
/// Reads an OpenWA webhook (a <c>message.received</c> event) into the same <see cref="ParsedWebhook"/> the Cloud API path uses,
/// so the one inbox, conversation machine and complaint intake serve both providers. Tolerant of fields it does not know; a body
/// that is not JSON throws <see cref="FormatException"/> so it can be refused.
/// </summary>
public static class OpenWaWebhook
{
    public static ParsedWebhook Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Webhook body is not JSON", ex); }

        using (doc)
        {
            var messages = new List<InboundMessage>();
            // OpenWA posts one event per request, but accept an array of them too.
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray()) AddFrom(e, messages);
            else AddFrom(doc.RootElement, messages);
            return new ParsedWebhook(messages, []);
        }
    }

    private static void AddFrom(JsonElement envelope, List<InboundMessage> into)
    {
        if (envelope.ValueKind != JsonValueKind.Object) return;
        // We only create complaints from inbound messages; session/status/receipt events are ignored.
        if (Str(envelope, "event") != "message.received") return;
        var sessionId = Str(envelope, "sessionId");
        if (sessionId is null || !envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return;
        if (ReadMessage(data, sessionId) is { } message) into.Add(message);
    }

    private static InboundMessage? ReadMessage(JsonElement data, string sessionId)
    {
        if (Bool(data, "fromMe") == true || Bool(data, "isGroup") == true) return null; // our own echoes and group chats are not reports

        // Modern WhatsApp may send the sender as a privacy "@lid" rather than a phone number. When the gateway resolves it
        // (RESOLVE_LID_TO_PHONE), the real number is in senderPhone (top-level or under contact); prefer that. Otherwise fall
        // back to the digits of from/chatId (a plain 92…@c.us number works; a bare @lid will not match a resident or reply).
        var from = Digits(Str(data, "senderPhone") ?? Str(Child(data, "contact"), "senderPhone") ?? Str(data, "from") ?? Str(data, "chatId"));
        var id = Str(data, "id");
        if (string.IsNullOrEmpty(from) || id is null) return null;

        var rawType = (Str(data, "type") ?? "text").ToLowerInvariant();
        var body = Str(data, "body");

        // A tapped reply button (Baileys) is treated as if the person typed its text, so the usual word commands still apply.
        if (Child(data, "button") is { ValueKind: JsonValueKind.Object } button)
            return Make(sessionId, from, id, data, "text", text: Str(button, "text") ?? body);

        var media = Child(data, "media");
        var hasMedia = media is { ValueKind: JsonValueKind.Object }
            && Bool(media.Value, "omitted") != true
            && !string.IsNullOrEmpty(Str(media.Value, "data"));

        return rawType switch
        {
            "text" or "chat" => Make(sessionId, from, id, data, "text", text: body),
            "image" or "sticker" when hasMedia => Make(sessionId, from, id, data, "image", caption: body, mediaId: id, mime: Str(media!.Value, "mimetype")),
            "audio" or "voice" or "ptt" when hasMedia => Make(sessionId, from, id, data, "audio", mediaId: id, mime: Str(media!.Value, "mimetype")),
            "location" when Num(Child(data, "location"), "latitude") is { } lat && Num(Child(data, "location"), "longitude") is { } lon
                => Make(sessionId, from, id, data, "location", lat: lat, lon: lon, place: Str(Child(data, "location"), "name") ?? Str(Child(data, "location"), "address")),
            _ => Make(sessionId, from, id, data, "unsupported", text: body), // video, document and anything new: the machine answers "unsupported"
        };
    }

    private static InboundMessage Make(string sessionId, string from, string id, JsonElement data, string type,
        string? text = null, string? caption = null, string? mediaId = null, string? mime = null, double? lat = null, double? lon = null, string? place = null)
    {
        var sent = long.TryParse(Str(data, "timestamp"), CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : DateTimeOffset.UtcNow;
        // Raw is OpenWA's own message object (base64 media included), kept so the client can attach the media later.
        return new InboundMessage(sessionId, from, Str(Child(data, "contact"), "name"), id, sent, type, text, mediaId, mime, caption, lat, lon, place, null, null, data.GetRawText());
    }

    private static JsonElement? Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    private static string? Str(JsonElement? e, string name) => e is { } el ? Str(el, name) : null;
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.ToString() : null : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static double? Num(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } el && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Digits(string? s) => s is null ? "" : new string(s.Where(char.IsAsciiDigit).ToArray());
}

/// <summary>Checks a webhook really comes from our OpenWA gateway: <c>X-OpenWA-Signature: sha256=&lt;HMAC of the raw body with the shared secret&gt;</c>.</summary>
public static class OpenWaSignature
{
    public static string Sign(string secret, byte[] body) => "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    /// <summary>False for a missing or wrong signature, and always false when no secret is configured (fail closed).</summary>
    public static bool Verify(string secret, byte[] body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header)) return false;
        var expected = Encoding.ASCII.GetBytes(Sign(secret, body));
        var presented = Encoding.ASCII.GetBytes(header.Trim().ToLowerInvariant());
        return CryptographicOperations.FixedTimeEquals(expected, presented);
    }
}
