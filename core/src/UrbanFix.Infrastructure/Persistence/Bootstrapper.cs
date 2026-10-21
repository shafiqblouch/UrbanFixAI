using Npgsql;
using UrbanFix.Identity;

namespace UrbanFix.Infrastructure.Persistence;

/// <summary>Creates the platform operator organization and its first admin. Runs as the owner role (outside tenant rules).</summary>
public static class Bootstrapper
{
    public const string PlatformSlug = "platform";

    public static async Task EnsurePlatformAdminAsync(string ownerConnectionString, string email, string password, PasswordService passwords, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);

        await using var existing = new NpgsqlCommand("SELECT 1 FROM organizations WHERE slug = @s", conn);
        existing.Parameters.AddWithValue("s", PlatformSlug);
        if (await existing.ExecuteScalarAsync(ct) is not null) return;

        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Run(conn, tx, "INSERT INTO organizations (id, name, slug) VALUES (@id, 'Platform', @slug)", ("id", orgId), ("slug", PlatformSlug));
        await Run(conn, tx, "INSERT INTO users (id, email, display_name, password_hash) VALUES (@id, @e, 'Platform Admin', @h) ON CONFLICT (email) DO NOTHING",
            ("id", userId), ("e", email.ToLowerInvariant()), ("h", passwords.Hash(password)));
        await Run(conn, tx, "INSERT INTO memberships (id, organization_id, user_id, role) SELECT @id, @o, id, 'PlatformAdmin' FROM users WHERE email = @e",
            ("id", Guid.NewGuid()), ("o", orgId), ("e", email.ToLowerInvariant()));
        await tx.CommitAsync(ct);
    }

    private static async Task Run(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params (string, object)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync();
    }
}
