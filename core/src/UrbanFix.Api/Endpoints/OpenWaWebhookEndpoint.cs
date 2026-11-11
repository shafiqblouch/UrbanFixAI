using System.Text;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.WhatsApp;

namespace UrbanFix.Api.Endpoints;

/// <summary>
/// The OpenWA webhook: residents message the connected WhatsApp number, the gateway POSTs each message here, and it enters the same
/// inbox and complaint flow as the Cloud API path. Open to the internet, so the HMAC signature is checked before anything is read.
/// </summary>
public static class OpenWaWebhookEndpoint
{
    public const string Path = "/webhooks/openwa";
    // Larger than the Cloud API webhook because OpenWA delivers photos and voice notes inline as base64.
    private const int MaxBody = 16 * 1024 * 1024;

    public static void MapOpenWaWebhook(this IEndpointRouteBuilder app)
    {
        app.MapPost(Path, async (HttpContext http, OpenWaOptions options, WhatsAppInbox inbox, ILogger<WhatsAppInbox> log, CancellationToken ct) =>
        {
            if (!options.InboundEnabled) return Results.NotFound();

            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length > MaxBody) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var body = buffer.ToArray();

            if (!OpenWaSignature.Verify(options.WebhookSecret, body, http.Request.Headers["X-OpenWA-Signature"].ToString()))
            {
                log.LogWarning("OpenWA webhook with a missing or wrong signature refused");
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }

            ParsedWebhook parsed;
            try { parsed = OpenWaWebhook.Parse(Encoding.UTF8.GetString(body)); }
            catch (FormatException) { return Results.BadRequest(new { error = "malformed" }); }

            var result = await inbox.AcceptAsync(parsed, ct);
            return Results.Ok(result);
        }).WithTags("WhatsApp").AllowAnonymous().RequireRateLimiting(RateLimiting.WebhookPolicy);
    }
}
