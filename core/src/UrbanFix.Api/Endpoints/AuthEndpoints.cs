using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Auth;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Email, string Password, string? TotpCode, Guid? OrganizationId);
    public sealed record LoginResponse(string Token, Guid? OrganizationId, bool TotpEnrollmentRequired);
    public sealed record OtpRequestBody(string Phone);
    public sealed record OtpVerifyBody(string Phone, string Code, Guid? OrganizationId);
    public sealed record SwitchOrganizationRequest(Guid OrganizationId);
    public sealed record TotpEnrollmentResponse(string Secret, string ProvisioningUri);
    public sealed record TotpConfirmRequest(string Code);
    public sealed record MembershipDto(Guid Id, Guid OrganizationId, string OrganizationName, string Role, Guid? DepartmentId, Guid? GateId);
    /// <summary>Present while the person is in a support session: they are looking into this society, read-only, until <c>ExpiresAt</c>.</summary>
    public sealed record SupportInfo(Guid SessionId, DateTimeOffset ExpiresAt);
    public sealed record MeResponse(Guid UserId, string Email, string DisplayName, Guid? OrganizationId, IReadOnlyList<MembershipDto> Memberships, SupportInfo? Support = null);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/auth").WithTags("Auth");

        g.MapPost("/login", async (LoginRequest req, AuthService auth, CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(req.Email, req.Password, req.TotpCode, req.OrganizationId, ct);
            return result.Outcome switch
            {
                LoginOutcome.TotpRequired => Results.Json(new { error = "totp_required" }, statusCode: 401),
                LoginOutcome.InvalidCredentials => Results.Json(new { error = "invalid_credentials" }, statusCode: 401),
                _ => Results.Ok(new LoginResponse(result.Token!, result.OrganizationId, result.Outcome == LoginOutcome.TotpEnrollmentRequired)),
            };
        }).Produces<LoginResponse>(200).AllowAnonymous().RequireRateLimiting(RateLimiting.PublicPolicy);

        // Residents sign in with a code sent to their phone (ID-05). The answer never says whether the number is registered.
        g.MapPost("/otp/request", async (OtpRequestBody req, OtpService otp, CancellationToken ct) =>
        {
            if (PhoneNumber.Normalize(req.Phone) is not { } phone) return Results.BadRequest(new { error = "invalid_phone" });
            return await otp.RequestAsync(phone, ct) == OtpRequestOutcome.Throttled
                ? Results.Json(new { error = "too_many_requests" }, statusCode: 429)
                : Results.Accepted();
        }).AllowAnonymous().RequireRateLimiting(RateLimiting.PublicPolicy);

        g.MapPost("/otp/verify", async (OtpVerifyBody req, OtpService otp, CancellationToken ct) =>
        {
            if (PhoneNumber.Normalize(req.Phone) is not { } phone) return Results.BadRequest(new { error = "invalid_phone" });
            var result = await otp.VerifyAsync(phone, req.Code ?? "", req.OrganizationId, ct);
            return result.Outcome == LoginOutcome.Success
                ? Results.Ok(new LoginResponse(result.Token!, result.OrganizationId, false))
                : Results.Json(new { error = "invalid_code" }, statusCode: 401);
        }).Produces<LoginResponse>(200).AllowAnonymous().RequireRateLimiting(RateLimiting.PublicPolicy);

        g.MapPost("/logout", async (ClaimsPrincipal user, AuthService auth, CancellationToken ct) =>
        {
            await auth.RevokeAsync(Guid.Parse(user.FindFirstValue("sid")!), ct);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.SignedIn);

        g.MapPost("/switch-organization", async (SwitchOrganizationRequest req, ClaimsPrincipal user, AuthService auth, CancellationToken ct) =>
        {
            var principal = new SessionPrincipal(Guid.Parse(user.FindFirstValue("sid")!), Guid.Parse(user.FindFirstValue("sub")!), null, true);
            return await auth.SwitchOrganizationAsync(principal, req.OrganizationId, ct) ? Results.NoContent() : Results.Forbid();
        }).RequireAuthorization(AuthPolicies.Verified);

        g.MapPost("/totp/enroll", async (ClaimsPrincipal user, AuthService auth, CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue("sub")!);
            var email = await auth.UserEmailAsync(userId, ct);
            var enrollment = email is null ? null : await auth.BeginTotpEnrollmentAsync(userId, email, ct);
            return enrollment is null ? Results.Conflict(new { error = "already_enrolled" }) : Results.Ok(new TotpEnrollmentResponse(enrollment.Value.Secret, enrollment.Value.Uri));
        }).Produces<TotpEnrollmentResponse>(200).RequireAuthorization(AuthPolicies.SignedIn);

        g.MapPost("/totp/confirm", async (TotpConfirmRequest req, ClaimsPrincipal user, AuthService auth, CancellationToken ct) =>
        {
            var principal = new SessionPrincipal(Guid.Parse(user.FindFirstValue("sid")!), Guid.Parse(user.FindFirstValue("sub")!), null, false);
            return await auth.ConfirmTotpEnrollmentAsync(principal, req.Code, ct) ? Results.NoContent() : Results.BadRequest(new { error = "invalid_code" });
        }).RequireAuthorization(AuthPolicies.SignedIn);

        app.MapGet("/me", async (ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var userId = Guid.Parse(principal.FindFirstValue("sub")!);
            var me = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.Email, u.DisplayName }).SingleAsync(ct);
            var memberships = await db.Memberships.AsNoTracking().Where(m => m.UserId == userId && m.IsActive)
                .Join(db.Organizations, m => m.OrganizationId, o => o.Id,
                    (m, o) => new MembershipDto(m.Id, o.Id, o.Name, m.Role.ToString(), m.DepartmentId, m.GateId))
                .ToListAsync(ct);
            Guid? org = Guid.TryParse(principal.FindFirstValue("org"), out var parsed) ? parsed : null;
            SupportInfo? support = null;
            if (Guid.TryParse(principal.FindFirstValue("support"), out var supportId) && org is { } supportOrg && long.TryParse(principal.FindFirstValue("support_exp"), out var until))
            {
                // In a support session the person has no membership here; they act as Support, and the portal shows the matching read-only views.
                var orgName = await db.Organizations.AsNoTracking().Where(o => o.Id == supportOrg).Select(o => o.Name).SingleAsync(ct);
                memberships = [new MembershipDto(Guid.Empty, supportOrg, orgName, Role.Support.ToString(), null, null)];
                support = new SupportInfo(supportId, DateTimeOffset.FromUnixTimeSeconds(until));
            }
            return Results.Ok(new MeResponse(userId, me.Email, me.DisplayName, org, memberships, support));
        }).WithTags("Auth").Produces<MeResponse>(200).RequireAuthorization(AuthPolicies.Verified);
    }
}
