using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using OtpNet;

namespace UrbanFix.Identity;

public sealed class PasswordService
{
    private static readonly PasswordHasher<User> Hasher = new();

    public string Hash(string password) => Hasher.HashPassword(null!, password);

    public bool Verify(User user, string password)
        => Hasher.VerifyHashedPassword(user, user.PasswordHash, password) is not PasswordVerificationResult.Failed;

    /// <summary>Staff passwords: at least 12 characters and not trivially one character class.</summary>
    public static string? Validate(string password)
    {
        if (password.Length < 12) return "Password must be at least 12 characters.";
        var classes = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) };
        return classes.Count(c => c) < 3 ? "Use at least three of: lowercase, uppercase, digit, symbol." : null;
    }
}

public static class TotpService
{
    public static string NewSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    public static string ProvisioningUri(string issuer, string account, string secret)
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}";

    /// <summary>Accepts the current code and one step either side for clock drift.</summary>
    public static bool Verify(string base32Secret, string code, DateTime? utcNow = null)
    {
        if (code.Length != 6 || !code.All(char.IsDigit)) return false;
        var totp = new Totp(Base32Encoding.ToBytes(base32Secret));
        return totp.VerifyTotp(utcNow ?? DateTime.UtcNow, code, out _, new VerificationWindow(previous: 1, future: 1));
    }

    public static string CodeAt(string base32Secret, DateTime utcNow)
        => new Totp(Base32Encoding.ToBytes(base32Secret)).ComputeTotp(utcNow);
}

public static class SessionTokens
{
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>Only the hash is stored, so a database leak does not yield usable sessions.</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
