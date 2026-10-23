using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Auth;

public sealed class AuthOptions
{
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);
    public string Issuer { get; set; } = "UrbanFix";
}

public enum LoginOutcome { Success, InvalidCredentials, TotpRequired, TotpEnrollmentRequired }

public sealed record LoginResult(LoginOutcome Outcome, string? Token = null, Guid? OrganizationId = null);

public sealed record SessionPrincipal(Guid SessionId, Guid UserId, Guid? OrganizationId, bool TotpVerified, Guid? SupportSessionId = null, DateTimeOffset? ExpiresAt = null);

public sealed class AuthService(
    AppDbContext db, PasswordService passwords, IDataProtectionProvider protection, AuthOptions options, RequestInfo request)
{
    private IDataProtector Protector => protection.CreateProtector("UrbanFix.Totp.v1");

    /// <summary>
    /// Password then TOTP. A user without TOTP enrolled still gets a session, but one limited to enrolment
    /// (<c>TotpVerified</c> false) until the first code is confirmed.
    /// </summary>
    public async Task<LoginResult> LoginAsync(string email, string password, string? totpCode, Guid? organizationId, CancellationToken ct)
    {
        var user = await db.Users.FromSql($"SELECT * FROM find_user_for_login({email})").AsNoTracking().SingleOrDefaultAsync(ct);

        // Always run the hash to keep timing similar for unknown emails.
        if (user is null)
        {
            passwords.Hash(password);
            return new LoginResult(LoginOutcome.InvalidCredentials);
        }
        if (!user.IsActive || !passwords.Verify(user, password)) return new LoginResult(LoginOutcome.InvalidCredentials);

        if (user.TotpEnabled)
        {
            if (string.IsNullOrEmpty(totpCode)) return new LoginResult(LoginOutcome.TotpRequired);
            if (!TotpService.Verify(Protector.Unprotect(user.TotpSecretProtected!), totpCode))
                return new LoginResult(LoginOutcome.InvalidCredentials);
        }

        await db.ChangeTenantAsync(null, user.Id, ct);
        var memberships = await db.Memberships.Where(m => m.UserId == user.Id && m.IsActive).ToListAsync(ct);
        var chosenOrg = organizationId is { } wanted
            ? memberships.Any(m => m.OrganizationId == wanted) ? wanted : (Guid?)null
            : memberships.Select(m => (Guid?)m.OrganizationId).FirstOrDefault();
        if (organizationId is not null && chosenOrg is null) return new LoginResult(LoginOutcome.InvalidCredentials);

        var token = await CreateSessionAsync(user.Id, chosenOrg, user.TotpEnabled, ct);
        return new LoginResult(user.TotpEnabled ? LoginOutcome.Success : LoginOutcome.TotpEnrollmentRequired, token, chosenOrg);
    }

    /// <summary>A session for someone already proven by other means (phone OTP). No second factor applies to residents.</summary>
    public Task<string> StartSessionAsync(Guid userId, Guid? organizationId, CancellationToken ct) => CreateSessionAsync(userId, organizationId, true, ct);

    private async Task<string> CreateSessionAsync(Guid userId, Guid? orgId, bool totpVerified, CancellationToken ct)
    {
        var token = SessionTokens.NewToken();
        db.Sessions.Add(new Session
        {
            UserId = userId,
            OrganizationId = orgId,
            TokenHash = SessionTokens.Hash(token),
            TotpVerified = totpVerified,
            ExpiresAt = DateTimeOffset.UtcNow + options.SessionLifetime,
            Ip = request.Ip,
            Device = request.Device,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>Looks the token up on every request, so a revoked or expired session stops working immediately.</summary>
    public async Task<SessionPrincipal?> AuthenticateAsync(string token, CancellationToken ct)
    {
        var hash = SessionTokens.Hash(token);
        var session = await db.Sessions.FromSql($"SELECT * FROM find_session_by_token({hash})").AsNoTracking().SingleOrDefaultAsync(ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= DateTimeOffset.UtcNow) return null;

        await db.ChangeTenantAsync(session.OrganizationId, session.UserId, ct);
        var active = await db.Users.AsNoTracking().AnyAsync(u => u.Id == session.UserId && u.IsActive, ct);
        return active ? new SessionPrincipal(session.Id, session.UserId, session.OrganizationId, session.TotpVerified, session.SupportSessionId, session.ExpiresAt) : null;
    }

    public async Task RevokeAsync(Guid sessionId, CancellationToken ct)
    {
        await db.Sessions.Where(s => s.Id == sessionId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        await db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), ct);
    }

    /// <summary>Moves a session to another organization the user belongs to.</summary>
    public async Task<bool> SwitchOrganizationAsync(SessionPrincipal principal, Guid organizationId, CancellationToken ct)
    {
        var isMember = await db.Memberships.AnyAsync(m => m.UserId == principal.UserId && m.OrganizationId == organizationId && m.IsActive, ct);
        if (!isMember) return false;
        await db.Sessions.Where(s => s.Id == principal.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.OrganizationId, organizationId), ct);
        return true;
    }

    public async Task<(string Secret, string Uri)?> BeginTotpEnrollmentAsync(Guid userId, string email, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || user.TotpEnabled) return null;
        var secret = TotpService.NewSecret();
        user.TotpSecretProtected = Protector.Protect(secret);
        await db.SaveChangesAsync(ct);
        return (secret, TotpService.ProvisioningUri(options.Issuer, email, secret));
    }

    public async Task<bool> ConfirmTotpEnrollmentAsync(SessionPrincipal principal, string code, CancellationToken ct)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == principal.UserId, ct);
        if (user?.TotpSecretProtected is null || user.TotpEnabled) return false;
        if (!TotpService.Verify(Protector.Unprotect(user.TotpSecretProtected), code)) return false;

        user.TotpEnabled = true;
        await db.SaveChangesAsync(ct);
        await db.Sessions.Where(s => s.Id == principal.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotpVerified, true), ct);
        return true;
    }

    public async Task<string?> UserEmailAsync(Guid userId, CancellationToken ct)
        => await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(ct);
}
