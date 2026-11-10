using System.Security.Cryptography;
using System.Text;
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
    /// <summary>
    /// Demo only: a fixed authenticator code that is always accepted, so a demo needs no authenticator app. The application refuses to start
    /// with this set outside the Development environment.
    /// </summary>
    public string? DemoTotpCode { get; set; }
}

public enum LoginOutcome { Success, InvalidCredentials, TotpRequired, TotpEnrollmentRequired }

public sealed record LoginResult(LoginOutcome Outcome, string? Token = null, Guid? OrganizationId = null);

/// <summary>Result of asking for a login code to be sent to the signer-in's registered phone (WP-01 alternative factor).</summary>
public enum LoginOtpOutcome { Sent, NoPhone, InvalidCredentials, Throttled }

public sealed record LoginOtpResult(LoginOtpOutcome Outcome, string? PhoneHint = null);

public sealed record SessionPrincipal(Guid SessionId, Guid UserId, Guid? OrganizationId, bool TotpVerified, Guid? SupportSessionId = null, DateTimeOffset? ExpiresAt = null);

public sealed class AuthService(
    AppDbContext db, PasswordService passwords, IDataProtectionProvider protection, AuthOptions options, RequestInfo request,
    ISmsSender sms, OtpOptions otpOptions)
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
            if (!IsDemoCode(totpCode) && !TotpService.Verify(Protector.Unprotect(user.TotpSecretProtected!), totpCode))
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

    /// <summary>
    /// Password-checked, then verify the user's current password (never reveal unknown emails by timing) and, if they have a
    /// phone on file, send a one-time login code to it — the alternative second factor offered on the portal (WP-01). A user
    /// with no phone gets <see cref="LoginOtpOutcome.NoPhone"/> so the caller can fall back to the authenticator.
    /// </summary>
    public async Task<LoginOtpResult> RequestLoginOtpAsync(string email, string password, CancellationToken ct)
    {
        var user = await VerifyCredentialsAsync(email, password, ct);
        if (user is null) return new LoginOtpResult(LoginOtpOutcome.InvalidCredentials);
        if (string.IsNullOrEmpty(user.Phone)) return new LoginOtpResult(LoginOtpOutcome.NoPhone);

        var phone = user.Phone;
        var now = DateTimeOffset.UtcNow;
        var recent = await db.OtpChallenges.AsNoTracking().Where(c => c.Phone == phone && c.CreatedAt > now.AddHours(-1))
            .Select(c => c.CreatedAt).ToListAsync(ct);
        if (string.IsNullOrEmpty(otpOptions.FixedCode) && (recent.Count >= otpOptions.MaxPerHour || (recent.Count > 0 && now - recent.Max() < otpOptions.Cooldown)))
            return new LoginOtpResult(LoginOtpOutcome.Throttled);

        var code = NewOtpCode();
        db.OtpChallenges.Add(new OtpChallenge
        {
            Phone = phone, CodeHash = HashOtp(phone, code), ExpiresAt = now + otpOptions.Lifetime, Sent = true, Ip = request.Ip,
        });
        await db.SaveChangesAsync(ct);
        await sms.SendAsync(phone, $"Your UrbanFix code is {code}. It expires in {(int)otpOptions.Lifetime.TotalMinutes} minutes. Do not share it.", ct);
        return new LoginOtpResult(LoginOtpOutcome.Sent, PhoneNumber.Mask(phone));
    }

    /// <summary>Completes a portal sign-in with the phone login code from <see cref="RequestLoginOtpAsync"/>. Re-checks the password so the code alone is never enough.</summary>
    public async Task<LoginResult> VerifyLoginOtpAsync(string email, string password, string code, Guid? organizationId, CancellationToken ct)
    {
        var user = await VerifyCredentialsAsync(email, password, ct);
        if (user is null || string.IsNullOrEmpty(user.Phone)) return new LoginResult(LoginOutcome.InvalidCredentials);

        var phone = user.Phone;
        var now = DateTimeOffset.UtcNow;
        var challenge = await db.OtpChallenges.Where(c => c.Phone == phone && c.ConsumedAt == null).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (challenge is null || challenge.ExpiresAt <= now || challenge.Attempts >= otpOptions.MaxAttempts) return new LoginResult(LoginOutcome.InvalidCredentials);

        var matches = challenge.Sent && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(challenge.CodeHash), Encoding.UTF8.GetBytes(HashOtp(phone, code.Trim())));
        if (!matches)
        {
            challenge.Attempts++;
            await db.SaveChangesAsync(ct);
            return new LoginResult(LoginOutcome.InvalidCredentials);
        }

        // Single use even if two requests race: only one update sees ConsumedAt still empty.
        var consumed = await db.OtpChallenges.Where(c => c.Id == challenge.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, now), ct);
        if (consumed != 1) return new LoginResult(LoginOutcome.InvalidCredentials);

        await db.ChangeTenantAsync(null, user.Id, ct);
        var memberships = await db.Memberships.Where(m => m.UserId == user.Id && m.IsActive).ToListAsync(ct);
        var chosenOrg = organizationId is { } wanted
            ? memberships.Any(m => m.OrganizationId == wanted) ? wanted : (Guid?)null
            : memberships.Select(m => (Guid?)m.OrganizationId).FirstOrDefault();
        if (organizationId is not null && chosenOrg is null) return new LoginResult(LoginOutcome.InvalidCredentials);

        var token = await CreateSessionAsync(user.Id, chosenOrg, true, ct);
        return new LoginResult(LoginOutcome.Success, token, chosenOrg);
    }

    /// <summary>Email + password, with a constant-time hash on an unknown email so sign-in timing does not reveal who exists.</summary>
    private async Task<User?> VerifyCredentialsAsync(string email, string password, CancellationToken ct)
    {
        var user = await db.Users.FromSql($"SELECT * FROM find_user_for_login({email})").AsNoTracking().SingleOrDefaultAsync(ct);
        if (user is null) { passwords.Hash(password); return null; }
        return !user.IsActive || !passwords.Verify(user, password) ? null : user;
    }

    private string HashOtp(string phone, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{otpOptions.Pepper}|{phone}|{code}")));

    private string NewOtpCode()
    {
        if (!string.IsNullOrEmpty(otpOptions.FixedCode)) return otpOptions.FixedCode;
        var max = (int)Math.Pow(10, otpOptions.CodeLength);
        return RandomNumberGenerator.GetInt32(max).ToString().PadLeft(otpOptions.CodeLength, '0');
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

    private bool IsDemoCode(string? code) => !string.IsNullOrEmpty(options.DemoTotpCode) && code == options.DemoTotpCode;

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
        if (!IsDemoCode(code) && !TotpService.Verify(Protector.Unprotect(user.TotpSecretProtected), code)) return false;

        user.TotpEnabled = true;
        await db.SaveChangesAsync(ct);
        await db.Sessions.Where(s => s.Id == principal.SessionId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.TotpVerified, true), ct);
        return true;
    }

    public async Task<string?> UserEmailAsync(Guid userId, CancellationToken ct)
        => await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(ct);
}
