using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.WhatsApp;

public sealed record MediaFile(byte[] Bytes, string MimeType);

/// <summary>The WhatsApp Cloud API said no. <see cref="Code"/> is Meta's error code (131047: more than 24 hours since the person last wrote).</summary>
public sealed class WhatsAppSendException(int status, string? code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    public const string OutsideWindow = "131047";
    public bool NeedsTemplate => Code == OutsideWindow;
}

/// <summary>The calls this system makes to the WhatsApp Cloud API. Phone numbers are WhatsApp ids: digits with no plus sign.</summary>
public interface IWhatsAppClient
{
    /// <returns>WhatsApp's id for the message we sent.</returns>
    Task<string?> SendTextAsync(string phoneNumberId, string to, string body, CancellationToken ct);
    Task<string?> SendButtonsAsync(string phoneNumberId, string to, string body, IReadOnlyList<(string Id, string Title)> buttons, CancellationToken ct);
    Task<string?> SendTemplateAsync(string phoneNumberId, string to, string template, string language, IReadOnlyList<string> parameters, CancellationToken ct);
    Task<MediaFile> DownloadMediaAsync(string mediaId, CancellationToken ct);
}

public sealed class HttpWhatsAppClient(HttpClient http) : IWhatsAppClient
{
    private async Task<string?> PostMessageAsync(string phoneNumberId, JsonObject body, CancellationToken ct)
    {
        body["messaging_product"] = "whatsapp";
        using var response = await http.PostAsJsonAsync($"{phoneNumberId}/messages", body, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null, message = text;
            try
            {
                var error = JsonNode.Parse(text)?["error"];
                code = error?["code"]?.ToString(); message = error?["message"]?.GetValue<string>() ?? text;
            }
            catch (JsonException) { /* keep the raw text */ }
            throw new WhatsAppSendException((int)response.StatusCode, code, message.Length > 400 ? message[..400] : message);
        }
        return JsonNode.Parse(text)?["messages"]?[0]?["id"]?.GetValue<string>();
    }

    public Task<string?> SendTextAsync(string phoneNumberId, string to, string body, CancellationToken ct) =>
        PostMessageAsync(phoneNumberId, new JsonObject { ["to"] = to, ["type"] = "text", ["text"] = new JsonObject { ["body"] = body, ["preview_url"] = false } }, ct);

    public Task<string?> SendButtonsAsync(string phoneNumberId, string to, string body, IReadOnlyList<(string Id, string Title)> buttons, CancellationToken ct)
    {
        var rows = new JsonArray();
        foreach (var (id, title) in buttons) rows.Add(new JsonObject { ["type"] = "reply", ["reply"] = new JsonObject { ["id"] = id, ["title"] = title } });
        return PostMessageAsync(phoneNumberId, new JsonObject
        {
            ["to"] = to, ["type"] = "interactive",
            ["interactive"] = new JsonObject { ["type"] = "button", ["body"] = new JsonObject { ["text"] = body }, ["action"] = new JsonObject { ["buttons"] = rows } },
        }, ct);
    }

    public Task<string?> SendTemplateAsync(string phoneNumberId, string to, string template, string language, IReadOnlyList<string> parameters, CancellationToken ct)
    {
        var inner = new JsonObject { ["name"] = template, ["language"] = new JsonObject { ["code"] = language } };
        if (parameters.Count > 0)
        {
            var values = new JsonArray();
            foreach (var p in parameters) values.Add(new JsonObject { ["type"] = "text", ["text"] = p });
            inner["components"] = new JsonArray(new JsonObject { ["type"] = "body", ["parameters"] = values });
        }
        return PostMessageAsync(phoneNumberId, new JsonObject { ["to"] = to, ["type"] = "template", ["template"] = inner }, ct);
    }

    public async Task<MediaFile> DownloadMediaAsync(string mediaId, CancellationToken ct)
    {
        // Two steps: the media id gives a short-lived address, which then serves the bytes (both need the token).
        using var meta = await http.GetAsync(mediaId, ct);
        meta.EnsureSuccessStatusCode();
        var info = JsonNode.Parse(await meta.Content.ReadAsStringAsync(ct)) ?? throw new HttpRequestException("Media lookup returned nothing");
        var url = info["url"]?.GetValue<string>() ?? throw new HttpRequestException("Media lookup had no address");
        using var file = await http.GetAsync(url, ct);
        file.EnsureSuccessStatusCode();
        return new MediaFile(await file.Content.ReadAsByteArrayAsync(ct), info["mime_type"]?.GetValue<string>() ?? file.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
    }

    public static void Configure(HttpClient http, WhatsAppOptions options)
    {
        http.BaseAddress = new Uri(options.ApiBase.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(options.AccessToken)) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
    }
}

public enum SendOutcome { Sent, NeedsTemplate, OptedOut, NoChannel, Failed }

public sealed record SendResult(SendOutcome Outcome, string? ProviderMessageId = null, string? Error = null);

/// <summary>
/// Sends to people and keeps the record (WA-06, NT-02). Replies inside a chat are always free-form. Anything we start ourselves
/// (a status update, a reminder) respects consent and WhatsApp's 24-hour rule: within a day of the person's last message it is a
/// normal message; after that only an approved template may be sent, and with no template it is not sent at all.
/// </summary>
public sealed class WhatsAppSender(AppDbContext db, IWhatsAppClient client, ITenantContext tenant, WhatsAppOptions options, ILogger<WhatsAppSender> log)
{
    public async Task<WhatsAppChannel?> ChannelAsync(CancellationToken ct) => await db.WhatsAppChannels.AsNoTracking().FirstOrDefaultAsync(c => c.IsActive, ct);

    public async Task<WhatsAppConversation> ConversationAsync(string phone, CancellationToken ct)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        var existing = await db.WhatsAppConversations.SingleOrDefaultAsync(c => c.Phone == phone, ct);
        if (existing is not null) return existing;
        var created = new WhatsAppConversation { OrganizationId = org, Phone = phone };
        db.WhatsAppConversations.Add(created);
        return created;
    }

    private async Task<SendResult> RecordAndSendAsync(WhatsAppChannel channel, string phone, string type, string body, Func<string, Task<string?>> send, CancellationToken ct)
    {
        var to = phone.TrimStart('+');
        try
        {
            var id = await send(to);
            db.WhatsAppMessages.Add(new WhatsAppMessage { OrganizationId = channel.OrganizationId, Direction = "out", WaMessageId = id, WaId = to, Type = type, Body = body, Status = MessageStatus.Sent });
            return new SendResult(SendOutcome.Sent, id);
        }
        catch (Exception ex) when (ex is WhatsAppSendException or HttpRequestException or TaskCanceledException)
        {
            var code = (ex as WhatsAppSendException)?.Code;
            log.LogWarning("WhatsApp send to {Phone} failed: {Code} {Message}", PhoneNumberMask(to), code, ex.Message);
            db.WhatsAppMessages.Add(new WhatsAppMessage { OrganizationId = channel.OrganizationId, Direction = "out", WaId = to, Type = type, Body = body, Status = MessageStatus.Failed, Error = ex.Message });
            return new SendResult(ex is WhatsAppSendException { NeedsTemplate: true } ? SendOutcome.NeedsTemplate : SendOutcome.Failed, null, ex.Message);
        }
    }

    private static string PhoneNumberMask(string to) => to.Length <= 5 ? "***" : to[..3] + new string('*', to.Length - 5) + to[^2..];

    /// <summary>A reply inside a chat the person started: always allowed, buttons optional.</summary>
    public async Task<SendResult> ReplyAsync(string phone, string text, IReadOnlyList<(string Id, string Title)>? buttons, CancellationToken ct)
    {
        if (await ChannelAsync(ct) is not { } channel) return new SendResult(SendOutcome.NoChannel);
        var result = await RecordAndSendAsync(channel, phone, buttons is { Count: > 0 } ? "interactive" : "text", text,
            to => buttons is { Count: > 0 } ? client.SendButtonsAsync(channel.PhoneNumberId, to, text, buttons, ct) : client.SendTextAsync(channel.PhoneNumberId, to, text, ct), ct);
        if (result.Outcome == SendOutcome.Sent) (await ConversationAsync(phone, ct)).LastOutboundAt = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    /// A message we start (NT-02). <paramref name="freeText"/> goes as it is inside the 24-hour window; outside it, the
    /// approved template for this notification goes instead, filled from <paramref name="values"/>.
    /// </summary>
    public async Task<SendResult> NotifyAsync(string phone, string freeText, string notificationTemplate, IReadOnlyDictionary<string, string> values, string language, CancellationToken ct)
    {
        if (await ChannelAsync(ct) is not { } channel) return new SendResult(SendOutcome.NoChannel);
        var conversation = await ConversationAsync(phone, ct);
        if (conversation.OptedOutAt is not null) return new SendResult(SendOutcome.OptedOut);

        var now = DateTimeOffset.UtcNow;
        SendResult result;
        if (conversation.InServiceWindow(now))
        {
            result = await RecordAndSendAsync(channel, phone, "text", freeText, to => client.SendTextAsync(channel.PhoneNumberId, to, freeText, ct), ct);
            if (result.Outcome != SendOutcome.NeedsTemplate) { if (result.Outcome == SendOutcome.Sent) conversation.LastOutboundAt = now; return result; }
        }
        if (!options.Templates.TryGetValue(notificationTemplate, out var template) || string.IsNullOrEmpty(template.Name)) return new SendResult(SendOutcome.NeedsTemplate);

        var parameters = template.Parameters.Select(p => values.GetValueOrDefault(p, "")).ToList();
        result = await RecordAndSendAsync(channel, phone, "template", $"{template.Name}: {string.Join(" | ", parameters)}",
            to => client.SendTemplateAsync(channel.PhoneNumberId, to, template.Name, language == "ur" ? "ur" : "en", parameters, ct), ct);
        if (result.Outcome == SendOutcome.Sent) conversation.LastOutboundAt = now;
        return result;
    }
}
