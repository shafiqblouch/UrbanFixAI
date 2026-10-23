using Npgsql;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

[Collection("db")]
public class TenantIsolationTests(TestApp app)
{
    // Tables that legitimately have no organization_id, each with the reason.
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["organizations"] = "tenant root",
        ["users"] = "global identity; visibility enforced by RLS through memberships",
        ["sessions"] = "per-user; RLS by user id",
        ["outbox_messages"] = "infrastructure queue; each message carries its organization",
        ["otp_challenges"] = "read before anyone is signed in; short-lived, keyed by phone, holds only a hash of the code",
        ["schema_migrations"] = "infrastructure",
        ["support_sessions"] = "platform-only: the application role has no privileges on it at all (PlatformTests proves it); only the platform service on the owner connection reads it",
    };

    private async Task<(Guid OrgA, Guid OrgB)> TwoOrgsWithMembers()
    {
        var (a, _) = await app.CreateOrganizationAsync();
        var (b, _) = await app.CreateOrganizationAsync();
        return (a, b);
    }

    [Fact]
    public async Task Query_without_tenant_context_returns_nothing()
    {
        await TwoOrgsWithMembers();
        await using var conn = await app.OpenAppConnectionAsync(null);
        Assert.Equal(0L, await app.ScalarAsync(conn, "SELECT count(*) FROM memberships"));
        Assert.Equal(0L, await app.ScalarAsync(conn, "SELECT count(*) FROM audit_log"));
        Assert.Equal(0L, await app.ScalarAsync(conn, "SELECT count(*) FROM users"));
        Assert.Equal(0L, await app.ScalarAsync(conn, "SELECT count(*) FROM organizations"));
    }

    [Fact]
    public async Task Tenant_sees_only_its_own_rows()
    {
        var (a, b) = await TwoOrgsWithMembers();
        await using var conn = await app.OpenAppConnectionAsync(a);
        Assert.Equal(0L, await app.ScalarAsync(conn, "SELECT count(*) FROM memberships WHERE organization_id = @b", ("b", b)));
        Assert.True((long)(await app.ScalarAsync(conn, "SELECT count(*) FROM memberships WHERE organization_id = @a", ("a", a)))! > 0);
        Assert.Equal(0L, await app.ScalarAsync(conn, "SELECT count(*) FROM audit_log WHERE organization_id = @b", ("b", b)));
        Assert.Equal(1L, await app.ScalarAsync(conn, "SELECT count(*) FROM organizations"));
    }

    [Fact]
    public async Task Tenant_cannot_write_into_another_organization()
    {
        var (a, b) = await TwoOrgsWithMembers();
        await using var conn = await app.OpenAppConnectionAsync(a);

        var userId = (Guid)(await app.ScalarAsync(conn, "SELECT user_id FROM memberships WHERE organization_id = @a LIMIT 1", ("a", a)))!;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => app.ScalarAsync(conn,
            "INSERT INTO memberships (id, organization_id, user_id, role) VALUES (gen_random_uuid(), @b, @u, 'Resident')", ("b", b), ("u", userId)));
        Assert.Equal("42501", ex.SqlState); // row-level security violation

        // Updates and deletes of another tenant's rows silently match nothing.
        await using var cmd = new NpgsqlCommand("UPDATE memberships SET is_active = false WHERE organization_id = @b", conn);
        cmd.Parameters.AddWithValue("b", b);
        Assert.Equal(0, await cmd.ExecuteNonQueryAsync());
        await using var del = new NpgsqlCommand("DELETE FROM memberships WHERE organization_id = @b", conn);
        del.Parameters.AddWithValue("b", b);
        Assert.Equal(0, await del.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Cross_tenant_http_read_is_empty()
    {
        var (a, managerA) = await app.CreateOrganizationAsync();
        var (b, managerB) = await app.CreateOrganizationAsync();

        var members = await managerA.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.MemberDto>>("/members");
        Assert.NotEmpty(members!);
        Assert.DoesNotContain(members!, m => m.Email.Contains($"@{b}")); // none of B's people

        var audit = await managerA.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.AuditDto>>("/audit?limit=200");
        Assert.NotEmpty(audit!);
        // Manager A holds no role in B: switching must be refused.
        var switchRes = await managerA.Client.PostAsJsonAsync("/auth/switch-organization", new UrbanFix.Api.Endpoints.AuthEndpoints.SwitchOrganizationRequest(b));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, switchRes.StatusCode);
        Assert.NotEqual(a, b);
        Assert.NotEmpty((await managerB.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.MemberDto>>("/members"))!);
    }

    [Fact]
    public async Task Every_table_has_organization_id_or_a_documented_exemption_and_tenant_tables_enforce_rls()
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();

        var tables = new List<(string Name, bool HasOrg, bool Rls, bool Forced)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.relname,
                   EXISTS (SELECT 1 FROM information_schema.columns k WHERE k.table_schema='public' AND k.table_name=c.relname AND k.column_name='organization_id'),
                   c.relrowsecurity, c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname='public' AND c.relkind='r'
            """, conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) tables.Add((r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3)));

        Assert.NotEmpty(tables);
        foreach (var t in tables)
        {
            if (Exempt.ContainsKey(t.Name)) continue;
            Assert.True(t.HasOrg, $"table {t.Name} has no organization_id and is not in the exemption list");
            Assert.True(t.Rls && t.Forced, $"table {t.Name} must ENABLE and FORCE row-level security");
        }
    }

    [Fact]
    public async Task Application_role_is_not_a_superuser_and_cannot_bypass_rls()
    {
        await using var conn = await app.OpenAppConnectionAsync(null);
        Assert.Equal(false, await app.ScalarAsync(conn, "SELECT rolsuper OR rolbypassrls FROM pg_roles WHERE rolname = current_user"));
    }
}
