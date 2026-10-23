using System.Security.Cryptography;
using System.Text;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.WhatsApp;

namespace UrbanFix.Api.Endpoints;

/// <summary>The WhatsApp Cloud API webhook (WA-02). Open to the internet, so everything is checked before anything is stored.</summary>
public static class WhatsAppEndpoints
{
    public const string Path = "/webhooks/whatsapp";
    private const int MaxBody = 1024 * 1024;

    public static void MapWhatsAppEndpoints(this IEndpointRouteBuilder app)
    {
        // Meta calls this once, when the webhook is registered, to check we own it.
        app.MapGet(Path, (HttpRequest request, WhatsAppOptions options) =>
        {
            if (!options.Enabled || string.IsNullOrEmpty(options.VerifyToken)) return Results.NotFound();
            var token = request.Query["hub.verify_token"].ToString();
            var ok = request.Query["hub.mode"] == "subscribe"
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(options.VerifyToken));
            return ok ? Results.Text(request.Query["hub.challenge"].ToString()) : Results.StatusCode(StatusCodes.Status403Forbidden);
        }).WithTags("WhatsApp").AllowAnonymous().RequireRateLimiting(RateLimiting.WebhookPolicy);

        app.MapPost(Path, async (HttpContext http, WhatsAppOptions options, WhatsAppInbox inbox, ILogger<WhatsAppInbox> log, CancellationToken ct) =>
        {
            if (!options.Enabled) return Results.NotFound();

            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var body = buffer.ToArray();

            // The signature covers the exact bytes received. A forged or altered body is refused before it is read.
            if (!WhatsAppSignature.Verify(options.AppSecret, body, http.Request.Headers["X-Hub-Signature-256"].ToString()))
            {
                log.LogWarning("WhatsApp webhook with a missing or wrong signature refused");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            ParsedWebhook parsed;
            try { parsed = WhatsAppPayload.Parse(Encoding.UTF8.GetString(body)); }
            catch (FormatException) { return Results.BadRequest(new { error = "malformed" }); }

            var result = await inbox.AcceptAsync(parsed, ct);
            return Results.Ok(result);
        }).WithTags("WhatsApp").AllowAnonymous().RequireRateLimiting(RateLimiting.WebhookPolicy);
    }
}
