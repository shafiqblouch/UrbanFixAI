using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Tests.Support;

public static class TestSecrets
{
    public const string AppRolePassword = "test-app-role-pw";
    public const string PlatformEmail = "root@platform.test";
    public const string PlatformPassword = "Platform-Pass-123!";
    public const string UserPassword = "Correct-Horse-9!";
}

/// <summary>
/// Real PostgreSQL in a container, migrated by the production migrator, behind the real application.
/// Shared by all tests in the "db" collection.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private readonly string _keyDir = Path.Combine(Path.GetTempPath(), "urbanfix-test-keys-" + Guid.NewGuid().ToString("N"));

    public string OwnerConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";
    public Guid PlatformOrgId { get; private set; }

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var b = new NpgsqlConnectionStringBuilder(_pg.GetConnectionString());
        OwnerConnectionString = b.ConnectionString;
        b.Username = Migrator.AppRole;
        b.Password = TestSecrets.AppRolePassword;
        b.Pooling = false;
        AppConnectionString = b.ConnectionString;

        await Migrator.MigrateAsync(OwnerConnectionString, TestSecrets.AppRolePassword);
        await Bootstrapper.EnsurePlatformAdminAsync(OwnerConnectionString, TestSecrets.PlatformEmail, TestSecrets.PlatformPassword, new PasswordService());

        await using var conn = new NpgsqlConnection(OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT id FROM organizations WHERE slug = 'platform'", conn);
        PlatformOrgId = (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _pg.DisposeAsync();
        try { Directory.Delete(_keyDir, true); } catch (IOException) { }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Owner", OwnerConnectionString);
        builder.UseSetting("ConnectionStrings:App", new NpgsqlConnectionStringBuilder(AppConnectionString) { Pooling = true }.ConnectionString);
        builder.UseSetting("Database:AppRolePassword", TestSecrets.AppRolePassword);
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("DataProtection:KeyPath", _keyDir);
        builder.UseSetting("Background:Disabled", "true");
        builder.UseSetting("Jobs:Attempts", "2");
        builder.UseSetting("Jobs:DelaysInSeconds:0", "1");
        builder.UseSetting("Jobs:DelaysInSeconds:1", "1");
        builder.ConfigureServices(ConfigureTestServices);
    }

    private static void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton<EventCollector>();
        services.AddSingleton<IJobFailureNotifier, RecordingNotifier>();
        services.AddScoped<IEventHandler<TestHappened>, RecordingHandler>();
    }

    // Direct SQL as the application role, with a tenant context, for isolation tests -------------------------

    public async Task<NpgsqlConnection> OpenAppConnectionAsync(Guid? orgId, Guid? userId = null)
    {
        var conn = new NpgsqlConnection(AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.org_id', @o, false), set_config('app.user_id', @u, false)", conn);
        cmd.Parameters.AddWithValue("o", orgId?.ToString() ?? "");
        cmd.Parameters.AddWithValue("u", userId?.ToString() ?? "");
        await cmd.ExecuteNonQueryAsync();
        return conn;
    }

    public async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql, params (string, object)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        return await cmd.ExecuteScalarAsync();
    }

    // HTTP helpers --------------------------------------------------------------------------------------------

    /// <summary>Logs in, enrols TOTP on first use, and returns a fully verified client.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _totpSecrets = new();

    public async Task<ApiSession> LoginAsync(string email, string password, Guid? organizationId = null, string? totpSecret = null)
    {
        if (totpSecret is null && _totpSecrets.TryGetValue(email, out var known)) totpSecret = known;
        var client = CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UrbanFix-Tests/1.0");
        var res = await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest(email, password, totpSecret is null ? null : TotpService.CodeAt(totpSecret, DateTime.UtcNow), organizationId));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"login {email} -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var login = (await res.Content.ReadFromJsonAsync<AuthEndpoints.LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        if (login.TotpEnrollmentRequired)
        {
            var enroll = await client.PostAsync("/auth/totp/enroll", null);
            enroll.EnsureSuccessStatusCode();
            totpSecret = (await enroll.Content.ReadFromJsonAsync<AuthEndpoints.TotpEnrollmentResponse>())!.Secret;
            var confirm = await client.PostAsJsonAsync("/auth/totp/confirm", new AuthEndpoints.TotpConfirmRequest(TotpService.CodeAt(totpSecret, DateTime.UtcNow)));
            confirm.EnsureSuccessStatusCode();
        }
        _totpSecrets[email] = totpSecret!;
        return new ApiSession(client, login.Token, totpSecret!, login.OrganizationId);
    }

    public Task<ApiSession> LoginPlatformAdminAsync() => LoginAsync(TestSecrets.PlatformEmail, TestSecrets.PlatformPassword);

    /// <summary>Creates an organization with a manager through the real endpoint and returns the manager session.</summary>
    public async Task<(Guid OrgId, ApiSession Manager)> CreateOrganizationAsync(string? slug = null)
    {
        slug ??= "org-" + Guid.NewGuid().ToString("N")[..8];
        var admin = await LoginPlatformAdminAsync();
        var email = $"manager@{slug}.test";
        var res = await admin.Client.PostAsJsonAsync("/admin/organizations",
            new AdminEndpoints.CreateOrganizationRequest(slug, slug, email, "Manager " + slug, TestSecrets.UserPassword));
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"create org -> {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        var org = (await res.Content.ReadFromJsonAsync<AdminEndpoints.OrganizationDto>())!;
        return (org.Id, await LoginAsync(email, TestSecrets.UserPassword, org.Id));
    }

    public async Task<ApiSession> AddMemberAsync(ApiSession manager, Guid orgId, Role role, Guid? departmentId = null, Guid? gateId = null)
    {
        var email = $"{role}-{Guid.NewGuid():N}@x.test".ToLowerInvariant();
        var res = await manager.Client.PostAsJsonAsync("/members",
            new AdminEndpoints.AddMemberRequest(email, role.ToString(), TestSecrets.UserPassword, role, departmentId, gateId));
        res.EnsureSuccessStatusCode();
        return await LoginAsync(email, TestSecrets.UserPassword, orgId);
    }

}

public sealed record ApiSession(HttpClient Client, string Token, string TotpSecret, Guid? OrganizationId);

public sealed record TestHappened(Guid OrganizationId, string Note) : DomainEvent(OrganizationId);

public sealed class EventCollector
{
    public List<(Guid Org, string Note)> Handled { get; } = [];
    public Dictionary<string, int> FailuresRemaining { get; } = [];
    public int Attempts;
}

public sealed class RecordingHandler(EventCollector collector, ITenantContext tenant) : IEventHandler<TestHappened>
{
    public Task Handle(TestHappened e, CancellationToken ct)
    {
        Interlocked.Increment(ref collector.Attempts);
        lock (collector)
        {
            if (collector.FailuresRemaining.TryGetValue(e.Note, out var left) && left > 0)
            {
                collector.FailuresRemaining[e.Note] = left - 1;
                throw new InvalidOperationException("boom " + e.Note);
            }
            if (tenant.OrganizationId != e.OrganizationId) throw new InvalidOperationException("handler ran without the event's tenant context");
            collector.Handled.Add((e.OrganizationId, e.Note));
        }
        return Task.CompletedTask;
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<TestApp>;
