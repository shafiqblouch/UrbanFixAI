using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Auth;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Security;

public static class AuthPolicies
{
    /// <summary>Signed in, second factor possibly still pending. Only for TOTP enrolment endpoints.</summary>
    public const string SignedIn = "signed-in";
    /// <summary>Signed in with the second factor verified.</summary>
    public const string Verified = "verified";
    public static string For(string permission) => "perm:" + permission;
}

public sealed class SessionAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    AuthService auth, RequestInfo request)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Session";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        request.Ip = Context.Connection.RemoteIpAddress?.ToString();
        request.Device = Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 300)] : null;

        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();

        var principal = await auth.AuthenticateAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted);
        if (principal is null) return AuthenticateResult.Fail("Invalid or revoked session");

        var claims = new List<Claim>
        {
            new("sub", principal.UserId.ToString()),
            new("sid", principal.SessionId.ToString()),
            new("totp", principal.TotpVerified ? "true" : "false"),
        };
        if (principal.OrganizationId is { } org) claims.Add(new Claim("org", org.ToString()));

        var identity = new ClaimsIdentity(claims, Scheme, "sub", ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme));
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>The caller's active memberships in the current organization (row-level security already scopes the query).</summary>
public sealed class CurrentMemberships(AppDbContext db, ITenantContext tenant)
{
    private IReadOnlyList<Membership>? _cache;

    public async Task<IReadOnlyList<Membership>> GetAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        if (tenant.OrganizationId is not { } org || tenant.UserId is not { } user) return _cache = [];
        return _cache = await db.Memberships.AsNoTracking()
            .Where(m => m.OrganizationId == org && m.UserId == user && m.IsActive).ToListAsync(ct);
    }

    /// <summary>
    /// Scoped grants: a membership with a department or gate applies only to that department or gate; a
    /// membership without either applies organization-wide.
    /// </summary>
    public async Task<bool> CanAsync(string permission, Guid? departmentId, Guid? gateId, CancellationToken ct)
    {
        foreach (var m in await GetAsync(ct))
        {
            if (!RolePermissions.Allows(m.Role, permission)) continue;
            if (m.DepartmentId is { } d && d != departmentId) continue;
            if (m.GateId is { } g && g != gateId) continue;
            return true;
        }
        return false;
    }
}

public sealed class PermissionHandler(CurrentMemberships memberships) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.FindFirst("totp")?.Value != "true") return;

        // Endpoint-level checks ignore department/gate scope (the resource is not loaded yet); a membership
        // carrying a scope still qualifies, and handlers narrow it with CanAsync on the actual resource.
        foreach (var m in await memberships.GetAsync(CancellationToken.None))
        {
            if (RolePermissions.Allows(m.Role, requirement.Permission))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}

public static class AuthorizationSetup
{
    public static void AddUrbanFixAuthorization(this IServiceCollection services)
    {
        services.AddAuthentication(SessionAuthHandler.Scheme).AddScheme<AuthenticationSchemeOptions, SessionAuthHandler>(SessionAuthHandler.Scheme, null);
        services.AddScoped<CurrentMemberships>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        services.AddAuthorization(o =>
        {
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(AuthPolicies.SignedIn, p => p.RequireAuthenticatedUser());
            o.AddPolicy(AuthPolicies.Verified, p => p.RequireAuthenticatedUser().RequireClaim("totp", "true"));
            foreach (var permission in Permissions.All)
                o.AddPolicy(AuthPolicies.For(permission), p => p.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)));
        });
    }
}
