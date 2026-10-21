using System.Reflection;
using Npgsql;

namespace UrbanFix.Infrastructure.Persistence;

/// <summary>
/// Applies the embedded SQL migrations in name order, once each. Runs as the owner role; the application itself
/// connects as <c>urbanfix_app</c>, a role that is neither owner nor superuser, so row-level security always applies.
/// </summary>
public static class Migrator
{
    public const string AppRole = "urbanfix_app";

    public static async Task MigrateAsync(string ownerConnectionString, string appRolePassword, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(ownerConnectionString);
        await conn.OpenAsync(ct);

        // One migrator at a time across instances.
        await Exec(conn, "SELECT pg_advisory_lock(727274)", ct);
        try
        {
            await EnsureAppRole(conn, appRolePassword, ct);
            await Exec(conn, "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);

            var assembly = Assembly.GetExecutingAssembly();
            var scripts = assembly.GetManifestResourceNames()
                .Where(n => n.EndsWith(".sql", StringComparison.Ordinal))
                .OrderBy(n => n, StringComparer.Ordinal);

            foreach (var resource in scripts)
            {
                var parts = resource.Split('.');
                var version = parts[^2];
                await using (var check = new NpgsqlCommand("SELECT 1 FROM schema_migrations WHERE version = @v", conn))
                {
                    check.Parameters.AddWithValue("v", version);
                    if (await check.ExecuteScalarAsync(ct) is not null) continue;
                }

                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                var sql = await reader.ReadToEndAsync(ct);

                await using var tx = await conn.BeginTransactionAsync(ct);
                await using (var run = new NpgsqlCommand(sql, conn, tx)) await run.ExecuteNonQueryAsync(ct);
                await using (var mark = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES (@v)", conn, tx))
                {
                    mark.Parameters.AddWithValue("v", version);
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await Exec(conn, "SELECT pg_advisory_unlock(727274)", ct);
        }
    }

    private static async Task EnsureAppRole(NpgsqlConnection conn, string password, CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_roles WHERE rolname = @r", conn);
        exists.Parameters.AddWithValue("r", AppRole);
        var literal = "'" + password.Replace("'", "''") + "'";
        var verb = await exists.ExecuteScalarAsync(ct) is null ? "CREATE" : "ALTER";
        await Exec(conn, $"{verb} ROLE {AppRole} LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD {literal}", ct);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
