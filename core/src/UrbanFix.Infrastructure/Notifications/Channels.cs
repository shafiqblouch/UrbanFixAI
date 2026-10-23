using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Infrastructure.Auth;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.WhatsApp;

namespace UrbanFix.Infrastructure.Notifications;

public static class ChannelIds
{
    public const string WhatsApp = "whatsapp";
    public const string Sms = "sms";
    public const string Email = "email";
}

/// <summary>
/// Notifications over WhatsApp (NT-02). Inside the 24-hour window the text goes as it is; outside it the approved template does;
/// without consent, a phone number or a template it is a permanent failure and the text-message fallback takes over.
/// </summary>
public sealed class WhatsAppNotificationChannel(AppDbContext db, WhatsAppSender sender) : INotificationChannel
{
    public string Name => ChannelIds.WhatsApp;

    public async Task<string?> SendAsync(Notification n, CancellationToken ct)
    {
        var phone = await db.Users.AsNoTracking().Where(u => u.Id == n.UserId).Select(u => u.Phone).SingleOrDefaultAsync(ct)
            ?? throw new PermanentChannelFailure("no_phone_number");
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(n.TemplateValues) ?? [];

        var result = await sender.NotifyAsync(phone, $"{n.Title}\n{n.Body}", n.Template, values, n.Locale, ct);
        return result.Outcome switch
        {
            SendOutcome.Sent => result.ProviderMessageId,
            SendOutcome.NeedsTemplate => throw new PermanentChannelFailure("outside_24h_and_no_template"),
            SendOutcome.OptedOut => throw new PermanentChannelFailure("opted_out"),
            SendOutcome.NoChannel => throw new PermanentChannelFailure("no_whatsapp_number_for_society"),
            _ => throw new PermanentChannelFailure(result.Error ?? "whatsapp_send_failed"),
        };
    }
}

/// <summary>Notifications as text messages (NT-03): the fallback when WhatsApp fails, or a person's own choice.</summary>
public sealed class SmsNotificationChannel(AppDbContext db, ISmsSender sms) : INotificationChannel
{
    public string Name => ChannelIds.Sms;

    public async Task<string?> SendAsync(Notification n, CancellationToken ct)
    {
        var phone = await db.Users.AsNoTracking().Where(u => u.Id == n.UserId).Select(u => u.Phone).SingleOrDefaultAsync(ct)
            ?? throw new PermanentChannelFailure("no_phone_number");
        await sms.SendAsync(phone, $"{n.Title}: {n.Body}", ct);
        return null;
    }
}

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public sealed class EmailOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public bool UseSsl { get; set; } = true;
}

/// <summary>Plain SMTP. Not verified against a real mail server yet; see docs/wave-4-status.md.</summary>
public sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        using var client = new System.Net.Mail.SmtpClient(options.Host, options.Port) { EnableSsl = options.UseSsl };
        if (!string.IsNullOrEmpty(options.User)) client.Credentials = new System.Net.NetworkCredential(options.User, options.Password);
        using var message = new System.Net.Mail.MailMessage(options.From, to, subject, body) { SubjectEncoding = Encoding.UTF8, BodyEncoding = Encoding.UTF8 };
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>Notifications by email (NT-04), for people who chose it.</summary>
public sealed class EmailNotificationChannel(AppDbContext db, IEmailSender email) : INotificationChannel
{
    public string Name => ChannelIds.Email;

    public async Task<string?> SendAsync(Notification n, CancellationToken ct)
    {
        var address = await db.Users.AsNoTracking().Where(u => u.Id == n.UserId).Select(u => u.Email).SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(address) || !address.Contains('@')) throw new PermanentChannelFailure("no_email_address");
        await email.SendAsync(address, n.Title, n.Body, ct);
        return null;
    }
}

public sealed class SmsGatewayOptions
{
    /// <summary>The gateway's endpoint. Empty means no gateway: messages only go to the log.</summary>
    public string Url { get; set; } = "";
    /// <summary>Request body with <c>{to}</c> and <c>{message}</c> in it, for example <c>{"to":"{to}","text":"{message}","from":"UrbanFix"}</c>. The values are escaped for JSON.</summary>
    public string BodyTemplate { get; set; } = "{\"to\":\"{to}\",\"message\":\"{message}\"}";
    /// <summary>Header carrying the credential, for example <c>Authorization</c>.</summary>
    public string AuthHeader { get; set; } = "Authorization";
    public string AuthValue { get; set; } = "";
}

/// <summary>
/// Sends text messages through a gateway that takes a JSON request (NT-03). No provider is chosen yet, so the request shape is
/// configuration, not code. Not verified against a real gateway: the pilot's provider decides whether this shape fits.
/// </summary>
public sealed class HttpSmsSender(HttpClient http, SmsGatewayOptions options) : ISmsSender
{
    private static string Escape(string s) => JsonEncodedText.Encode(s).ToString();

    public async Task SendAsync(string phone, string message, CancellationToken ct)
    {
        var body = options.BodyTemplate.Replace("{to}", Escape(phone), StringComparison.Ordinal).Replace("{message}", Escape(message), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(options.AuthValue)) request.Headers.TryAddWithoutValidation(options.AuthHeader, options.AuthValue);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"SMS gateway answered {(int)response.StatusCode}");
    }
}
