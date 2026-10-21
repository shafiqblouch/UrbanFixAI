using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.Auth;
using UrbanFix.Infrastructure.Persistence;

namespace UrbanFix.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string Email, string Password, string? TotpCode, Guid? OrganizationId);
    public sealed record LoginResponse(string Token, Guid? OrganizationId, bool TotpEnrollmentRequired);
    public sealed record SwitchOrganizationRequest(Guid OrganizationId);
    public sealed record TotpEnrollmentResponse(string Secret, string ProvisioningUri);
    public sealed record TotpConfirmRequest(string Code);
    public sealed record MembershipDto(Guid Id, Guid OrganizationId, string OrganizationName, string Role, Guid? DepartmentId, Guid? GateId);
    public sealed record MeResponse(Guid UserId, string Email, string DisplayName, Guid? OrganizationId, IReadOnlyList<MembershipDto> Memberships);

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
        }).AllowAnonymous();

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
        }).RequireAuthorization(AuthPolicies.SignedIn);

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
            return Results.Ok(new MeResponse(userId, me.Email, me.DisplayName, org, memberships));
        }).WithTags("Auth").RequireAuthorization(AuthPolicies.Verified);
    }
}
