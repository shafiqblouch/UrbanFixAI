using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Infrastructure.Persistence;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>
/// Talks to a self-hosted OpenWA gateway in place of the WhatsApp Cloud API, so the existing conversation and complaint flow can
/// run over an unofficial WhatsApp number. <c>phoneNumberId</c> here is the OpenWA session id (that is what the channel row stores).
/// OpenWA cannot render interactive buttons, so button and template replies are flattened to plain text the person can type back.
/// Inbound media arrives inline (base64) on the webhook, so <see cref="DownloadMediaAsync"/> reads it from the stored message.
/// </summary>
public sealed class OpenWaWhatsAppClient(HttpClient http, OpenWaOptions options, AppDbContext db) : IWhatsAppClient
{
    private string ChatId(string to) => to.TrimStart('+') + options.ChatSuffix;

    private async Task<string?> PostTextAsync(string sessionId, string chatId, string text, CancellationToken ct)
    {
        var body = new JsonObject { ["chatId"] = chatId, ["text"] = text };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/sessions/{sessionId}/messages/send-text") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("X-API-Key", options.ApiKey);
        using var response = await http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string message = payload;
            try { message = JsonNode.Parse(payload)?["message"]?.ToString() ?? payload; } catch (JsonException) { /* keep raw */ }
            throw new WhatsAppSendException((int)response.StatusCode, null, message.Length > 400 ? message[..400] : message);
        }
        try { return JsonNode.Parse(payload)?["messageId"]?.GetValue<string>(); } catch (JsonException) { return null; }
    }

    public Task<string?> SendTextAsync(string phoneNumberId, string to, string body, CancellationToken ct) =>
        PostTextAsync(phoneNumberId, ChatId(to), body, ct);

    /// <summary>No real buttons: the question is sent with the choices as text, and the person types one back.</summary>
    public Task<string?> SendButtonsAsync(string phoneNumberId, string to, string body, IReadOnlyList<(string Id, string Title)> buttons, CancellationToken ct)
    {
        var text = buttons.Count == 0 ? body : body + "\n\n" + string.Join("\n", buttons.Select(b => $"• {b.Title}"));
        return PostTextAsync(phoneNumberId, ChatId(to), text, ct);
    }

    /// <summary>OpenWA has no approved-template concept; the filled text is sent as an ordinary message.</summary>
    public Task<string?> SendTemplateAsync(string phoneNumberId, string to, string template, string language, IReadOnlyList<string> parameters, CancellationToken ct) =>
        PostTextAsync(phoneNumberId, ChatId(to), string.Join(" ", parameters), ct);

    /// <summary>The bytes were delivered inline on the webhook; <paramref name="mediaId"/> is the message id, used to find the stored copy.</summary>
    public async Task<MediaFile> DownloadMediaAsync(string mediaId, CancellationToken ct)
    {
        var raw = await db.WhatsAppMessages.AsNoTracking().Where(m => m.WaMessageId == mediaId).Select(m => m.Raw).FirstOrDefaultAsync(ct)
            ?? throw new HttpRequestException($"OpenWA message {mediaId} has no stored body");
        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Object
            || media.TryGetProperty("data", out var dataEl) is false || dataEl.ValueKind != JsonValueKind.String)
            throw new HttpRequestException($"OpenWA message {mediaId} carries no inline media");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(dataEl.GetString()!); }
        catch (FormatException ex) { throw new HttpRequestException($"OpenWA message {mediaId} has unreadable media", ex); }
        var mime = media.TryGetProperty("mimetype", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString()! : "application/octet-stream";
        return new MediaFile(bytes, mime);
    }
}
