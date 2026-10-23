using System.Text;
using Npgsql;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.WhatsApp;

namespace UrbanFix.Tests.Support;

public static class WaTest
{
    public const string AppSecret = "test-app-secret";
    public const string VerifyToken = "test-verify-token";

    /// <summary>Loads a webhook body from Fixtures/whatsapp and fills in its {{PLACEHOLDERS}}.</summary>
    public static string Fixture(string name, params (string Key, string Value)[] values)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "whatsapp", name + ".json"), Encoding.UTF8);
        foreach (var (key, value) in values) text = text.Replace("{{" + key + "}}", value, StringComparison.Ordinal);
        return text;
    }

    public static string Wamid() => "wamid." + Guid.NewGuid().ToString("N");

    public static HttpRequestMessage Webhook(string body, string? signature = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/whatsapp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Hub-Signature-256", signature ?? WhatsAppSignature.Sign(AppSecret, Encoding.UTF8.GetBytes(body)));
        return request;
    }

    /// <summary>Gives a society a WhatsApp business number, the way setup will.</summary>
    public static async Task<string> AddChannelAsync(TestApp app, Guid org)
    {
        var id = "pn-" + Guid.NewGuid().ToString("N")[..10];
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO whatsapp_channels (id, organization_id, phone_number_id, display_number) VALUES (gen_random_uuid(), @o, @p, '+923001110000')", conn);
        cmd.Parameters.AddWithValue("o", org);
        cmd.Parameters.AddWithValue("p", id);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }
}

/// <summary>Stands in for the WhatsApp Cloud API: keeps everything sent, serves chosen files as media, and can be made to fail.</summary>
public sealed class FakeWhatsAppClient : IWhatsAppClient
{
    public sealed record Sent(string PhoneNumberId, string To, string Kind, string Text, IReadOnlyList<(string Id, string Title)> Buttons, string? Template = null, IReadOnlyList<string>? Parameters = null, string? Language = null, string Id = "");

    public List<Sent> Messages { get; } = [];
    public Dictionary<string, MediaFile> Media { get; } = [];
    public HashSet<string> BrokenMedia { get; } = [];
    public Dictionary<string, WhatsAppSendException> Failures { get; } = [];
    /// <summary>When set, free-form messages are refused the way WhatsApp does outside the 24-hour window.</summary>
    public bool RefuseFreeForm { get; set; }
    private int _counter;

    private string Next() => "wamid.out" + Interlocked.Increment(ref _counter) + "." + Guid.NewGuid().ToString("N")[..6];

    private string Record(Sent message)
    {
        if (Failures.TryGetValue(message.To, out var failure)) throw failure;
        var id = Next();
        lock (Messages) Messages.Add(message with { Id = id });
        return id;
    }

    public Task<string?> SendTextAsync(string phoneNumberId, string to, string body, CancellationToken ct)
    {
        if (RefuseFreeForm) throw new WhatsAppSendException(400, WhatsAppSendException.OutsideWindow, "Re-engagement message");
        return Task.FromResult<string?>(Record(new Sent(phoneNumberId, to, "text", body, [])));
    }

    public Task<string?> SendButtonsAsync(string phoneNumberId, string to, string body, IReadOnlyList<(string Id, string Title)> buttons, CancellationToken ct)
    {
        if (RefuseFreeForm) throw new WhatsAppSendException(400, WhatsAppSendException.OutsideWindow, "Re-engagement message");
        return Task.FromResult<string?>(Record(new Sent(phoneNumberId, to, "buttons", body, buttons)));
    }

    public Task<string?> SendTemplateAsync(string phoneNumberId, string to, string template, string language, IReadOnlyList<string> parameters, CancellationToken ct) =>
        Task.FromResult<string?>(Record(new Sent(phoneNumberId, to, "template", "", [], template, parameters, language)));

    public Task<MediaFile> DownloadMediaAsync(string mediaId, CancellationToken ct)
    {
        if (BrokenMedia.Contains(mediaId)) throw new HttpRequestException("media expired");
        return Media.TryGetValue(mediaId, out var file) ? Task.FromResult(file) : throw new HttpRequestException("no such media");
    }

    public IReadOnlyList<Sent> To(string waId) { lock (Messages) return Messages.Where(m => m.To == waId).ToList(); }
    public Sent LastTo(string waId) => To(waId).Last();
}

public sealed class RecordingEmailSender : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        lock (Sent) Sent.Add((to, subject, body));
        return Task.CompletedTask;
    }
}
