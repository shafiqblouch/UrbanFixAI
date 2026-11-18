using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace UrbanFix.Infrastructure.GateHardware;

/// <summary>Who a reader key belongs to: the reader, its society, its gate and the one direction it records.</summary>
public sealed record GateReaderIdentity(Guid ReaderId, Guid OrganizationId, Guid GateId, string Direction);

/// <summary>
/// Authenticates a gate reader by its API key (GT-05). The key proves which society and lane a read came from, so the
/// lookup runs on the owner connection across tenants — just like the platform dashboards — before the tenant context
/// is set for the rest of the request. The key itself is never stored; only its SHA-256 hash is.
/// </summary>
public sealed class GateReaderAuth(IConfiguration config)
{
    /// <summary>Human-readable prefix so a leaked key is recognisable in logs and can be searched for.</summary>
    public const string KeyPrefix = "grk_";

    private NpgsqlConnection Open() => new(config.GetConnectionString("Owner") ?? config.GetConnectionString("App"));

    /// <summary>The reader behind this key, or null if the key is unknown or the reader is switched off.</summary>
    public async Task<GateReaderIdentity?> ResolveAsync(string? apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;
        await using var conn = Open();
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, organization_id, gate_id, direction FROM gate_readers WHERE api_key_hash = @h AND is_active", conn);
        cmd.Parameters.AddWithValue("h", HashKey(apiKey));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new GateReaderIdentity(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3));
    }

    /// <summary>Best-effort note that a reader just called in, so the admin page can show it as online.</summary>
    public async Task TouchAsync(Guid readerId, CancellationToken ct)
    {
        try
        {
            await using var conn = Open();
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE gate_readers SET last_seen_at = now() WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("id", readerId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* a missed heartbeat must never fail a scan */ }
    }

    /// <summary>A fresh high-entropy key to hand to one reader, shown once. Format: grk_&lt;43 url-safe base64 chars&gt;.</summary>
    public static string NewKey() => KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>SHA-256 of the key, lower-case hex; what is stored and compared.</summary>
    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
