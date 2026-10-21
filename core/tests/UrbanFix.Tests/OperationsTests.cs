using System.Net;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

[Collection("db")]
public class OperationsTests(TestApp app)
{
    [Fact]
    public async Task Health_endpoints_are_public_and_readiness_checks_the_database()
    {
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Contains("ready", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Migrations_are_idempotent()
    {
        await Migrator.MigrateAsync(app.OwnerConnectionString, TestSecrets.AppRolePassword);
        await Migrator.MigrateAsync(app.OwnerConnectionString, TestSecrets.AppRolePassword);
        await using var conn = new Npgsql.NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM schema_migrations", conn);
        Assert.True((long)(await cmd.ExecuteScalarAsync())! >= 1);
    }

    [Fact]
    public async Task Openapi_contract_matches_the_committed_file()
    {
        var json = await app.CreateClient().GetStringAsync("/openapi/v1.json");
        var parsed = System.Text.Json.JsonDocument.Parse(json);
        var pretty = System.Text.Json.JsonSerializer.Serialize(parsed, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

        var path = ContractPath();
        if (Environment.GetEnvironmentVariable("UPDATE_CONTRACT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, pretty.Replace("\r\n", "\n") + "\n");
        }

        Assert.True(File.Exists(path), "contracts/openapi.json missing; run with UPDATE_CONTRACT=1");
        var committed = (await File.ReadAllTextAsync(path)).Replace("\r\n", "\n").TrimEnd();
        Assert.True(committed == pretty.Replace("\r\n", "\n").TrimEnd(),
            "API contract changed. If intended, regenerate with UPDATE_CONTRACT=1 and the web client with `pnpm --filter @urbanfix/api-client generate`.");
    }

    [Fact]
    public async Task Openapi_describes_the_core_endpoints()
    {
        var doc = System.Text.Json.JsonDocument.Parse(await app.CreateClient().GetStringAsync("/openapi/v1.json"));
        var paths = doc.RootElement.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "/auth/login", "/me", "/members", "/audit", "/admin/organizations" }, paths);
    }

    private static string ContractPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "core"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("repo root not found"), "contracts", "openapi.json");
    }
}
