using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Auth;

/// <summary>Sends a text message. The real gateway arrives with NT-03; until then only the logging sender exists.</summary>
public interface ISmsSender
{
    Task SendAsync(string phone, string message, CancellationToken ct);
}

/// <summary>Development stand-in: writes the message to the log instead of sending it. Never use where real residents log in.</summary>
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> log) : ISmsSender
{
    public Task SendAsync(string phone, string message, CancellationToken ct)
    {
        log.LogWarning("SMS gateway not configured; message for {Phone} not sent: {Message}", PhoneNumber.Mask(phone), message);
        return Task.CompletedTask;
    }
}

public sealed class OtpOptions
{
    public int CodeLength { get; set; } = 6;
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int MaxAttempts { get; set; } = 5;
    /// <summary>A new code may be asked for only after this long.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(60);
    public int MaxPerHour { get; set; } = 5;
    /// <summary>Mixed into the stored hash so a leaked table cannot be reversed by trying all six-digit codes.</summary>
    public string Pepper { get; set; } = "";
}

public enum OtpRequestOutcome { Accepted, Throttled }

/// <summary>
/// Phone-OTP login for residents (ID-05). A code is single-use, expires, and dies after too many wrong tries.
/// The answer to a request is the same for numbers we know and numbers we do not, so the form cannot be used to find out who lives here.
/// </summary>
public sealed class OtpService(AppDbContext db, ISmsSender sms, AuthService auth, OtpOptions options, RequestInfo request)
{
    private string Hash(string phone, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{options.Pepper}|{phone}|{code}")));

    private string NewCode()
    {
        var max = (int)Math.Pow(10, options.CodeLength);
        return RandomNumberGenerator.GetInt32(max).ToString().PadLeft(options.CodeLength, '0');
    }

    /// <summary>Only people whose every membership is "resident" may sign in this way; staff keep password and authenticator.</summary>
    private async Task<(User User, List<Membership> Memberships)?> EligibleAsync(string phone, CancellationToken ct)
    {
        var user = await db.Users.FromSql($"SELECT * FROM find_user_by_phone({phone})").AsNoTracking().SingleOrDefaultAsync(ct);
        if (user is null || !user.IsActive) return null;
        await db.ChangeTenantAsync(null, user.Id, ct);
        var memberships = await db.Memberships.AsNoTracking().Where(m => m.UserId == user.Id && m.IsActive).ToListAsync(ct);
        if (memberships.Count == 0 || memberships.Any(m => m.Role != Role.Resident)) return null;
        return (user, memberships);
    }

    public async Task<OtpRequestOutcome> RequestAsync(string phone, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var recent = await db.OtpChallenges.AsNoTracking().Where(c => c.Phone == phone && c.CreatedAt > now.AddHours(-1))
            .Select(c => c.CreatedAt).ToListAsync(ct);
        if (recent.Count >= options.MaxPerHour || (recent.Count > 0 && now - recent.Max() < options.Cooldown)) return OtpRequestOutcome.Throttled;

        var eligible = await EligibleAsync(phone, ct) is not null;
        var code = NewCode();
        db.OtpChallenges.Add(new OtpChallenge
        {
            Phone = phone, CodeHash = Hash(phone, code), ExpiresAt = now + options.Lifetime, Sent = eligible, Ip = request.Ip,
        });
        await db.SaveChangesAsync(ct);
        if (eligible) await sms.SendAsync(phone, $"Your UrbanFix code is {code}. It expires in {(int)options.Lifetime.TotalMinutes} minutes. Do not share it.", ct);
        return OtpRequestOutcome.Accepted;
    }

    public async Task<LoginResult> VerifyAsync(string phone, string code, Guid? organizationId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var challenge = await db.OtpChallenges.Where(c => c.Phone == phone && c.ConsumedAt == null).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (challenge is null || challenge.ExpiresAt <= now || challenge.Attempts >= options.MaxAttempts) return new LoginResult(LoginOutcome.InvalidCredentials);

        var matches = challenge.Sent && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(challenge.CodeHash), Encoding.UTF8.GetBytes(Hash(phone, code.Trim())));
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

        if (await EligibleAsync(phone, ct) is not { } who) return new LoginResult(LoginOutcome.InvalidCredentials);
        var org = organizationId is { } wanted
            ? who.Memberships.Any(m => m.OrganizationId == wanted) ? wanted : (Guid?)null
            : who.Memberships.Select(m => (Guid?)m.OrganizationId).FirstOrDefault();
        if (org is null) return new LoginResult(LoginOutcome.InvalidCredentials);

        var token = await auth.StartSessionAsync(who.User.Id, org, ct);
        return new LoginResult(LoginOutcome.Success, token, org);
    }
}
