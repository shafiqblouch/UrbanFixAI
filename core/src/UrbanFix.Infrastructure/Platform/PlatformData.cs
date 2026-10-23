using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Npgsql;
using UrbanFix.Identity;

namespace UrbanFix.Infrastructure.Platform;

public sealed record OrganizationRow(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt, int Members, int Houses, int Complaints, DateTimeOffset? LastComplaintAt);
public sealed record OnboardingStep(string Key, bool Done, int Count);
public sealed record AiQualityRow(Guid OrganizationId, string Organization, string Category, string? Language, int Total, int Accepted, int Overridden, double AcceptRate, double OverrideRate);
public sealed record AiUsageRow(
    Guid OrganizationId, string Organization, int Calls, long InputTokens, long OutputTokens, decimal? CostUsd, bool Priced, int Complaints, decimal? CostPerComplaintUsd,
    long TokensThisMonth, long MonthlyCap, double UsedFraction, bool OverCap);
public sealed record SupportSessionRow(
    Guid Id, Guid OrganizationId, string Organization, Guid AdminUserId, string Admin, string Reason, DateTimeOffset StartedAt, DateTimeOffset ExpiresAt, DateTimeOffset? EndedAt, bool Active);
public sealed record OpenedSupportSession(Guid Id, string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// What the platform team sees across societies. Row-level security keeps every other part of the application inside one society, so
/// this is the one place that reads across them, on the owner connection, and only ever through the platform-admin endpoints.
/// It reads aggregates and writes only flags, the AI allowance and support sessions; it never returns another society's records.
/// </summary>
public sealed class PlatformData(IConfiguration config)
{
    public static readonly Guid NoOrganization = Guid.Empty;

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(config.GetConnectionString("Owner") ?? config.GetConnectionString("App"));
        await conn.OpenAsync(ct);
        return conn;
    }

    private static NpgsqlCommand Cmd(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null, params (string, object?)[] args)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    // Organizations -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OrganizationRow>> OrganizationsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, @"
            SELECT o.id, o.name, o.slug, o.created_at,
                   (SELECT count(*) FROM memberships m WHERE m.organization_id = o.id AND m.is_active),
                   (SELECT count(*) FROM houses h WHERE h.organization_id = o.id),
                   (SELECT count(*) FROM complaints c WHERE c.organization_id = o.id),
                   (SELECT max(c.created_at) FROM complaints c WHERE c.organization_id = o.id)
            FROM organizations o WHERE o.slug <> 'platform' ORDER BY o.name");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<OrganizationRow>();
        while (await r.ReadAsync(ct))
            rows.Add(new OrganizationRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6),
                r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7)));
        return rows;
    }

    public async Task<bool> OrganizationExistsAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "SELECT 1 FROM organizations WHERE id = @id AND slug <> 'platform'", null, ("id", id));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>What a new society still has to set up before people can use it (PA-01).</summary>
    public async Task<IReadOnlyList<OnboardingStep>> OnboardingAsync(Guid organizationId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        async Task<int> Count(string sql)
        {
            await using var cmd = Cmd(conn, sql, null, ("o", organizationId));
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        var manager = await Count("SELECT count(*) FROM memberships WHERE organization_id = @o AND is_active AND role = 'Manager'");
        var categories = await Count("SELECT count(*) FROM categories WHERE organization_id = @o AND is_active");
        var places = await Count("SELECT count(*) FROM locations WHERE organization_id = @o AND is_active");
        var houses = await Count("SELECT count(*) FROM houses WHERE organization_id = @o");
        var teams = await Count("SELECT count(*) FROM teams WHERE organization_id = @o AND is_active");
        var staff = await Count("SELECT count(*) FROM memberships WHERE organization_id = @o AND is_active AND role IN ('Technician', 'Supervisor', 'Office')");
        var prices = await Count("SELECT count(*) FROM price_items WHERE organization_id = @o AND is_active");
        var residents = await Count("SELECT count(*) FROM resident_links WHERE organization_id = @o AND to_date IS NULL");
        var routing = await Count("SELECT count(*) FROM routing_rules WHERE organization_id = @o AND is_active");
        return
        [
            new("manager", manager > 0, manager), new("categories", categories > 0, categories), new("places", places > 0, places), new("houses", houses > 0, houses),
            new("teams", teams > 0, teams), new("staff", staff > 0, staff), new("prices", prices > 0, prices), new("residents", residents > 0, residents), new("routing", routing > 0, routing),
        ];
    }

    // Feature flags -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyDictionary<string, bool>> FeaturesAsync(Guid organizationId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, "SELECT feature, enabled FROM organization_features WHERE organization_id = @o", null, ("o", organizationId));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var chosen = new Dictionary<string, bool>();
        while (await r.ReadAsync(ct)) chosen[r.GetString(0)] = r.GetBoolean(1);
        return Features.Defaults.ToDictionary(d => d.Key, d => chosen.TryGetValue(d.Key, out var on) ? on : d.Value);
    }

    public async Task SetFeatureAsync(Guid organizationId, string feature, bool enabled, Guid by, string? ip, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = Cmd(conn, @"
            INSERT INTO organization_features (organization_id, feature, enabled, updated_by, updated_at) VALUES (@o, @f, @e, @u, now())
            ON CONFLICT (organization_id, feature) DO UPDATE SET enabled = @e, updated_by = @u, updated_at = now()", tx, ("o", organizationId), ("f", feature), ("e", enabled), ("u", by)))
            await cmd.ExecuteNonQueryAsync(ct);
        await AuditAsync(conn, tx, organizationId, by, "feature_changed", "Feature", feature, new { enabled }, ip, ct);
        await tx.CommitAsync(ct);
    }

    // AI dashboards -------------------------------------------------------------------------------------------------

    /// <summary>How often the AI's suggestion was accepted, per society, category and language (PA-03). Straight from the suggestion log.</summary>
    public async Task<IReadOnlyList<AiQualityRow>> AiQualityAsync(Guid? organizationId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, @"
            SELECT f.organization_id, o.name, f.suggested, f.language, count(*), count(*) FILTER (WHERE f.accepted)
            FROM ai_feedback f JOIN organizations o ON o.id = f.organization_id
            WHERE f.field = 'category' AND (@org::uuid IS NULL OR f.organization_id = @org)
              AND (@from::timestamptz IS NULL OR f.created_at >= @from) AND (@to::timestamptz IS NULL OR f.created_at <= @to)
            GROUP BY f.organization_id, o.name, f.suggested, f.language ORDER BY o.name, f.suggested, f.language",
            null, ("org", organizationId), ("from", from), ("to", to));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AiQualityRow>();
        while (await r.ReadAsync(ct))
        {
            var total = (int)r.GetInt64(4);
            var accepted = (int)r.GetInt64(5);
            rows.Add(new AiQualityRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), total, accepted, total - accepted,
                Math.Round(accepted / (double)total, 3), Math.Round((total - accepted) / (double)total, 3)));
        }
        return rows;
    }

    /// <summary>What the AI cost each society this month, and how close each is to its allowance (PA-04).</summary>
    public async Task<IReadOnlyList<AiUsageRow>> AiUsageAsync(DateTimeOffset monthStart, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, @"
            SELECT o.id, o.name,
                   count(u.id), coalesce(sum(u.input_tokens), 0), coalesce(sum(u.output_tokens), 0),
                   sum(u.cost_usd), coalesce(bool_and(u.cost_usd IS NOT NULL), true),
                   count(DISTINCT u.complaint_id),
                   coalesce((s.settings #>> '{policy,aiMonthlyTokenCap}')::bigint, 2000000)
            FROM organizations o
            LEFT JOIN ai_usage u ON u.organization_id = o.id AND u.occurred_at >= @from
            LEFT JOIN organization_settings s ON s.organization_id = o.id
            WHERE o.slug <> 'platform'
            GROUP BY o.id, o.name, s.settings ORDER BY o.name", null, ("from", monthStart));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<AiUsageRow>();
        while (await r.ReadAsync(ct))
        {
            var calls = (int)r.GetInt64(2);
            var input = r.GetInt64(3); var output = r.GetInt64(4);
            var cost = r.IsDBNull(5) ? (decimal?)null : r.GetDecimal(5);
            var priced = r.GetBoolean(6);
            var complaints = (int)r.GetInt64(7);
            var cap = r.GetInt64(8);
            var tokens = input + output;
            rows.Add(new AiUsageRow(r.GetGuid(0), r.GetString(1), calls, input, output, priced ? cost : null, priced, complaints,
                priced && cost is { } c && complaints > 0 ? Math.Round(c / complaints, 6) : null, tokens, cap,
                cap <= 0 ? (tokens > 0 ? 1 : 0) : Math.Round(tokens / (double)cap, 3), cap <= 0 || tokens >= cap));
        }
        return rows;
    }

    /// <summary>Sets a society's monthly AI allowance. 0 switches the AI off for it; its complaints then use the rules only.</summary>
    public async Task SetAiCapAsync(Guid organizationId, long cap, Guid by, string? ip, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = Cmd(conn, @"
            UPDATE organization_settings
            SET settings = jsonb_set(settings, '{policy}', coalesce(settings -> 'policy', '{}'::jsonb) || jsonb_build_object('aiMonthlyTokenCap', @cap::bigint)), updated_at = now()
            WHERE organization_id = @o", tx, ("o", organizationId), ("cap", cap)))
            await cmd.ExecuteNonQueryAsync(ct);
        await AuditAsync(conn, tx, organizationId, by, "ai_cap_changed", "OrganizationSettings", organizationId.ToString(), new { aiMonthlyTokenCap = cap }, ip, ct);
        await tx.CommitAsync(ct);
    }

    // Support access ------------------------------------------------------------------------------------------------

    public const int MaxSupportMinutes = 120;

    /// <summary>
    /// Opens a read-only window into one society for the platform person (PA-05): a reason is required, it ends by itself, and the start
    /// is written to the audit log of the society it looks into and of the platform. Everything done inside is logged by the API.
    /// </summary>
    public async Task<OpenedSupportSession> OpenSupportSessionAsync(Guid organizationId, Guid adminUserId, string reason, int minutes, string? ip, string? device, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var token = SessionTokens.NewToken();
        var expires = DateTimeOffset.UtcNow.AddMinutes(minutes);
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = Cmd(conn, "INSERT INTO support_sessions (id, organization_id, admin_user_id, reason, expires_at) VALUES (@id, @o, @u, @r, @x)", tx,
            ("id", id), ("o", organizationId), ("u", adminUserId), ("r", reason), ("x", expires)))
            await cmd.ExecuteNonQueryAsync(ct);
        await using (var cmd = Cmd(conn, @"
            INSERT INTO sessions (id, user_id, organization_id, token_hash, totp_verified, created_at, expires_at, ip, device, support_session_id)
            VALUES (gen_random_uuid(), @u, @o, @h, true, now(), @x, @ip, @dev, @sid)", tx,
            ("u", adminUserId), ("o", organizationId), ("h", SessionTokens.Hash(token)), ("x", expires), ("ip", ip), ("dev", device), ("sid", id)))
            await cmd.ExecuteNonQueryAsync(ct);
        await AuditAsync(conn, tx, organizationId, adminUserId, "support_session_started", "SupportSession", id.ToString(), new { reason, expires }, ip, ct);
        await tx.CommitAsync(ct);
        return new OpenedSupportSession(id, token, expires);
    }

    /// <returns>False when there is no such session or it has already ended.</returns>
    public async Task<bool> EndSupportSessionAsync(Guid id, Guid by, string? ip, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        Guid org;
        await using (var cmd = Cmd(conn, "UPDATE support_sessions SET ended_at = now(), ended_by = @by WHERE id = @id AND ended_at IS NULL RETURNING organization_id", tx, ("id", id), ("by", by)))
        {
            if (await cmd.ExecuteScalarAsync(ct) is not Guid found) return false;
            org = found;
        }
        await using (var cmd = Cmd(conn, "UPDATE sessions SET revoked_at = now() WHERE support_session_id = @id AND revoked_at IS NULL", tx, ("id", id)))
            await cmd.ExecuteNonQueryAsync(ct);
        await AuditAsync(conn, tx, org, by, "support_session_ended", "SupportSession", id.ToString(), null, ip, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<SupportSessionRow>> SupportSessionsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = Cmd(conn, @"
            SELECT s.id, s.organization_id, o.name, s.admin_user_id, u.display_name, s.reason, s.started_at, s.expires_at, s.ended_at
            FROM support_sessions s JOIN organizations o ON o.id = s.organization_id JOIN users u ON u.id = s.admin_user_id
            ORDER BY s.started_at DESC LIMIT 200");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<SupportSessionRow>();
        var now = DateTimeOffset.UtcNow;
        while (await r.ReadAsync(ct))
        {
            var expires = r.GetFieldValue<DateTimeOffset>(7);
            var ended = r.IsDBNull(8) ? (DateTimeOffset?)null : r.GetFieldValue<DateTimeOffset>(8);
            rows.Add(new SupportSessionRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetFieldValue<DateTimeOffset>(6), expires, ended, ended is null && expires > now));
        }
        return rows;
    }

    // Shared ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes to the audit log of the society concerned, and of the platform, so a change made by the platform team shows up in both:
    /// the society can see what was done to it, and the platform can see what its people did.
    /// </summary>
    public async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid organizationId, Guid actor, string action, string entityType, string entityId, object? after, string? ip, CancellationToken ct)
    {
        var json = after is null ? null : JsonSerializer.Serialize(after);
        await using var platform = Cmd(conn, "SELECT id FROM organizations WHERE slug = 'platform'", tx);
        var platformOrg = await platform.ExecuteScalarAsync(ct) as Guid?;
        foreach (var org in new[] { organizationId, platformOrg }.Where(o => o is not null).Select(o => o!.Value).Distinct())
        {
            await using var cmd = Cmd(conn, @"
                INSERT INTO audit_log (id, organization_id, actor_user_id, action, entity_type, entity_id, after, occurred_at, ip)
                VALUES (gen_random_uuid(), @o, @a, @action, @type, @eid, @after::jsonb, now(), @ip)", tx,
                ("o", org), ("a", actor), ("action", action), ("type", entityType), ("eid", entityId), ("after", json), ("ip", ip));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
