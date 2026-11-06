using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Auth;

namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>
/// Settings for delivering one-time codes (and other short messages) through a self-hosted
/// <a href="https://github.com/rmyndharis/OpenWA">OpenWA</a> gateway. This exists so a developer can see a real OTP
/// arrive on a phone without a paid SMS gateway or an approved WhatsApp Cloud API template. OpenWA drives an
/// unofficial WhatsApp client, so it is a development and demo convenience, never the production channel.
/// </summary>
public sealed class OpenWaOptions
{
    /// <summary>Off by default: the logging sender stays in place until this is turned on and a URL is given.</summary>
    public bool Enabled { get; set; }
    /// <summary>Where the OpenWA gateway listens, for example <c>http://localhost:2785</c>.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>The admin or operator key OpenWA prints on first boot (sent as the <c>X-API-Key</c> header).</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>The OpenWA session that holds the logged-in WhatsApp number.</summary>
    public string SessionId { get; set; } = "default";
    /// <summary>Suffix that turns a phone number into a one-to-one WhatsApp chat id. Groups would use <c>@g.us</c>.</summary>
    public string ChatSuffix { get; set; } = "@c.us";
}

/// <summary>
/// An <see cref="ISmsSender"/> that delivers the text over WhatsApp through an OpenWA gateway instead of over SMS.
/// Registered only when <see cref="OpenWaOptions.Enabled"/> is set, so production, which does not configure it, is unaffected.
/// </summary>
public sealed class OpenWaSmsSender(HttpClient http, OpenWaOptions options, ILogger<OpenWaSmsSender> log) : ISmsSender
{
    public async Task SendAsync(string phone, string message, CancellationToken ct)
    {
        // A WhatsApp chat id is the number with no plus sign and the one-to-one suffix, e.g. +923001234567 -> 923001234567@c.us.
        var chatId = phone.TrimStart('+') + options.ChatSuffix;
        var body = new JsonObject { ["chatId"] = chatId, ["text"] = message };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/sessions/{options.SessionId}/messages/send-text")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.TryAddWithoutValidation("X-API-Key", options.ApiKey);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            log.LogWarning("OpenWA send to {Phone} failed ({Status}): {Detail}", PhoneNumber.Mask(phone), (int)response.StatusCode,
                detail.Length > 300 ? detail[..300] : detail);
            throw new HttpRequestException($"OpenWA gateway answered {(int)response.StatusCode}");
        }
    }

    public static void Configure(HttpClient http, OpenWaOptions options)
    {
        http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
    }
}
