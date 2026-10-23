using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Push;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Where a person's phones and browsers are registered for push, and a button to prove it works (FD-16, MA-04).</summary>
public static class PushEndpoints
{
    public sealed record PushConfigDto(bool Fcm, bool Web, string? WebPublicKey);
    public sealed record BrowserKeys(string P256dh, string Auth);
    public sealed record RegisterDevice(string Kind, string Token, BrowserKeys? Keys, string? Label);
    public sealed record DeviceDto(Guid Id, string Kind, string? Label, DateTimeOffset CreatedAt, DateTimeOffset? LastSentAt, int Failures, string? LastError);
    public sealed record TestResultDto(int Sent, int Gone, int Failed, IReadOnlyList<DeviceResultDto> Devices);
    public sealed record DeviceResultDto(Guid DeviceId, string Kind, string? Label, string Outcome, string? Detail);
    public sealed record VapidKeyPair(string PublicKey, string PrivateKey);

    private static DeviceDto ToDto(PushDevice d) => new(d.Id, d.Kind, d.Label, d.CreatedAt, d.LastSentAt, d.Failures, d.LastError);

    public static void MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        var verified = AuthPolicies.Verified;
        var g = app.MapGroup("").WithTags("Push");

        // What this installation can push to, so an app or browser knows whether to ask the person's permission.
        g.MapGet("/push/config", (PushService push, PushOptions options) =>
            Results.Ok(new PushConfigDto(push.Kinds.Contains(PushKinds.Fcm), push.Kinds.Contains(PushKinds.Web), push.Kinds.Contains(PushKinds.Web) && options.Web.PublicKey.Length > 0 ? options.Web.PublicKey : null)))
            .Produces<PushConfigDto>(200).RequireAuthorization(verified);

        g.MapGet("/me/push-devices", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var me = Guid.Parse(user.FindFirstValue("sub")!);
            return Results.Ok((await db.PushDevices.AsNoTracking().Where(d => d.UserId == me && d.RevokedAt == null).OrderBy(d => d.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList());
        }).Produces<List<DeviceDto>>(200).RequireAuthorization(verified);

        g.MapPost("/me/push-devices", async (RegisterDevice req, ClaimsPrincipal user, ITenantContext tenant, PushService push, CancellationToken ct) =>
        {
            if (!PushKinds.All.Contains(req.Kind)) return Results.BadRequest(new { error = "invalid_kind" });
            if (!push.Kinds.Contains(req.Kind)) return Results.Conflict(new { error = "push_not_configured" });
            var token = req.Token?.Trim() ?? "";
            if (token.Length is 0 or > 4096) return Results.BadRequest(new { error = "invalid_token" });

            string? keys = null;
            if (req.Kind == PushKinds.Web)
            {
                // A browser gives an address on its own push service plus two keys. Only https addresses, and keys of the right size.
                if (!Uri.TryCreate(token, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return Results.BadRequest(new { error = "invalid_token" });
                try
                {
                    if (req.Keys is null || Base64Url.Decode(req.Keys.P256dh).Length != 65 || Base64Url.Decode(req.Keys.Auth).Length != 16) return Results.BadRequest(new { error = "invalid_keys" });
                }
                catch (FormatException) { return Results.BadRequest(new { error = "invalid_keys" }); }
                keys = JsonSerializer.Serialize(new { p256dh = req.Keys.P256dh, auth = req.Keys.Auth });
            }
            var label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim()[..Math.Min(req.Label.Trim().Length, 100)];
            var device = await push.RegisterAsync(tenant.OrganizationId!.Value, Guid.Parse(user.FindFirstValue("sub")!), req.Kind, token, keys, label, ct);
            return Results.Created($"/me/push-devices/{device.Id}", ToDto(device));
        }).Produces<DeviceDto>(201).RequireAuthorization(verified);

        g.MapDelete("/me/push-devices/{id:guid}", async (Guid id, ClaimsPrincipal user, PushService push, CancellationToken ct) =>
            await push.RevokeAsync(Guid.Parse(user.FindFirstValue("sub")!), id, ct) ? Results.NoContent() : Results.NotFound()).RequireAuthorization(verified);

        // "Send me a test": goes straight to this person's devices and says what happened on each.
        g.MapPost("/me/push-test", async (ClaimsPrincipal user, PushService push, CancellationToken ct) =>
        {
            var summary = await push.SendToUserAsync(Guid.Parse(user.FindFirstValue("sub")!), new PushMessage("UrbanFix", "This is a test notification.", "/", null), ct);
            if (summary.Devices.Count == 0) return Results.Conflict(new { error = "no_push_device" });
            return Results.Ok(new TestResultDto(summary.Sent, summary.Gone, summary.Retry,
                summary.Devices.Select(d => new DeviceResultDto(d.DeviceId, d.Kind, d.Label, d.Outcome.ToString(), d.Detail)).ToList()));
        }).Produces<TestResultDto>(200).RequireAuthorization(verified);

        // Platform setup: a fresh key pair for the server to identify itself to browsers' push services. Nothing is stored here.
        app.MapPost("/admin/push/vapid-keys", () =>
        {
            var (pub, priv) = VapidKeys.Generate();
            return Results.Ok(new VapidKeyPair(pub, priv));
        }).WithTags("Platform").Produces<VapidKeyPair>(200).RequireAuthorization(AuthPolicies.For(Permissions.OrganizationManage));
    }
}
