using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Testcontainers.PostgreSql;
using UrbanFix.Infrastructure.Complaints;
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
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("pgvector/pgvector:pg16").Build();
    private readonly IContainer _minio = new ContainerBuilder()
        .WithImage("bitnamilegacy/minio:latest").WithPortBinding(9000, true)
        .WithEnvironment("MINIO_ROOT_USER", "testkey").WithEnvironment("MINIO_ROOT_PASSWORD", "testsecret-123").WithEnvironment("MINIO_DEFAULT_BUCKETS", "test-media")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live"))).Build();
    private string _minioUrl = "";
    private readonly string _keyDir = Path.Combine(Path.GetTempPath(), "urbanfix-test-keys-" + Guid.NewGuid().ToString("N"));

    public string OwnerConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";
    public Guid PlatformOrgId { get; private set; }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_pg.StartAsync(), _minio.StartAsync());
        _minioUrl = $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}";
        await WaitForBucketAsync();
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
        await _minio.DisposeAsync();
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
        builder.UseSetting("Storage:ServiceUrl", _minioUrl);
        builder.UseSetting("Storage:AccessKey", "testkey");
        builder.UseSetting("Storage:SecretKey", "testsecret-123");
        builder.UseSetting("Storage:Bucket", "test-media");
        builder.UseSetting("RateLimits:PublicPerMinute", "100000");
        builder.UseSetting("Ai:Pricing:fake-model:InputPerMillion", "1.00");
        builder.UseSetting("Ai:Pricing:fake-model:OutputPerMillion", "5.00");
        builder.UseSetting("AutoRoute:MinAccuracy", "0");
        builder.UseSetting("AutoRoute:MinSamples", "0");
        builder.UseSetting("WhatsApp:Enabled", "true");
        builder.UseSetting("WhatsApp:AppSecret", WaTest.AppSecret);
        builder.UseSetting("WhatsApp:VerifyToken", WaTest.VerifyToken);
        builder.UseSetting("WhatsApp:AccessToken", "test-access-token");
        builder.UseSetting("WhatsApp:Templates:complaint_received:Name", "uf_complaint_received");
        builder.UseSetting("WhatsApp:Templates:complaint_received:Parameters:0", "reference");
        builder.UseSetting("Sms:Url", "http://sms-gateway.test/send");
        builder.UseSetting("Email:Host", "smtp.test");
        builder.UseSetting("Email:From", "no-reply@urbanfix.test");
        builder.UseSetting("Push:LogOnly", "true");
        builder.UseSetting("Jobs:Attempts", "2");
        builder.UseSetting("Jobs:DelaysInSeconds:0", "1");
        builder.UseSetting("Jobs:DelaysInSeconds:1", "1");
        builder.ConfigureServices(ConfigureTestServices);
    }

    private static void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton<EventCollector>();
        services.AddSingleton<IJobFailureNotifier, RecordingNotifier>();
        services.AddSingleton<FlakyChannel>();
        services.AddSingleton<UrbanFix.Infrastructure.Notifications.INotificationChannel>(sp => sp.GetRequiredService<FlakyChannel>());
        // Stand-ins for FCM and the browsers' push services; registered last, so they replace the log-only ones.
        services.AddSingleton<FakePush>();
        services.AddSingleton<UrbanFix.Infrastructure.Push.IPushSender>(sp => sp.GetRequiredService<FakePush>().Fcm);
        services.AddSingleton<UrbanFix.Infrastructure.Push.IPushSender>(sp => sp.GetRequiredService<FakePush>().Web);
        services.AddSingleton<FakeWhatsAppClient>();
        services.AddSingleton<UrbanFix.Infrastructure.WhatsApp.IWhatsAppClient>(sp => sp.GetRequiredService<FakeWhatsAppClient>());
        services.AddSingleton<RecordingEmailSender>();
        services.AddSingleton<UrbanFix.Infrastructure.Notifications.IEmailSender>(sp => sp.GetRequiredService<RecordingEmailSender>());
        services.AddSingleton<RecordingSmsSender>();
        services.AddSingleton<UrbanFix.Infrastructure.Auth.ISmsSender>(sp => sp.GetRequiredService<RecordingSmsSender>());
        services.AddSingleton<FakeAiClient>();
        services.AddSingleton<IAiBackend>(sp => sp.GetRequiredService<FakeAiClient>());
        services.AddScoped<IEventHandler<TestHappened>, RecordingHandler>();
    }

    private async Task WaitForBucketAsync()
    {
        var s3 = new Amazon.S3.AmazonS3Client(new Amazon.Runtime.BasicAWSCredentials("testkey", "testsecret-123"),
            new Amazon.S3.AmazonS3Config { ServiceURL = _minioUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        for (var i = 0; i < 40; i++)
        {
            try { await s3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = "test-media" }); return; }
            catch (Amazon.S3.AmazonS3Exception) { await Task.Delay(500); }
        }
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

/// <summary>Scriptable stand-in for the AI service.</summary>
public sealed class FakeAiClient : IAiBackend
{
    public bool Down { get; set; }
    public AiUnderstanding Reply { get; set; } = new("water", "No water supply in the street.", "Street 4", "en", 0.9f, false, "fake-model", "v1", new AiUsage(120, 30));
    public List<(string Text, IReadOnlyList<AiCategory> Categories, IReadOnlyList<string> Names, IReadOnlyList<string> Landmarks)> Calls { get; } = [];

    public AiComparison Comparison { get; set; } = new(0.9f, "The rubble appears to have been cleared.", false, 0.8f, false, "fake-vision", "v1", new AiUsage(900, 60));
    public int CompareCalls;

    public Task<AiComparison> CompareAsync(byte[] before, byte[] after, CancellationToken ct)
    {
        Interlocked.Increment(ref CompareCalls);
        return Down ? throw new HttpRequestException("AI service unreachable") : Task.FromResult(Comparison);
    }

    public string EmbeddingModel { get; set; } = "fake-embed";
    public Func<string, float[]> Embedder { get; set; } = text => Hash(text);
    public List<string> EmbeddedTexts { get; } = [];

    /// <summary>
    /// A fixed 64-number vector from the words, so texts sharing words are close and unrelated texts are not. Uses a stable hash
    /// (string.GetHashCode is different in every process, which made results depend on luck).
    /// </summary>
    public static float[] Hash(string text)
    {
        var v = new float[64];
        foreach (var word in text.ToLowerInvariant().Split([' ', '.', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            uint h = 2166136261;
            foreach (var b in System.Text.Encoding.UTF8.GetBytes(word)) h = (h ^ b) * 16777619;
            v[h % 64] += 1;
        }
        return v;
    }

    public Task<AiEmbedding> EmbedAsync(string text, IReadOnlyList<string> knownNames, CancellationToken ct)
    {
        lock (EmbeddedTexts) EmbeddedTexts.Add(text);
        return Down ? throw new HttpRequestException("AI service unreachable") : Task.FromResult(new AiEmbedding(Embedder(text), EmbeddingModel, new AiUsage(40, 0)));
    }

    public AiPhoto Photo { get; set; } = new(["pothole"], "A possible pothole is visible in the road.", false, 0.8f, false, false, "fake-vision", "v1", new AiUsage(500, 40));
    public int PhotoCalls;

    public Task<AiPhoto> InterpretPhotoAsync(byte[] image, CancellationToken ct)
    {
        Interlocked.Increment(ref PhotoCalls);
        return Down ? throw new HttpRequestException("AI service unreachable") : Task.FromResult(Photo);
    }

    public AiTranscript Transcript { get; set; } = new("no water since morning", 0.8f, "en", false, "fake-speech", "v1", new AiUsage(0, 0));
    public List<(int Bytes, string Mime, string? Hint)> TranscribeCalls { get; } = [];

    public Task<AiTranscript> TranscribeAsync(byte[] audio, string mimeType, string? languageHint, CancellationToken ct)
    {
        lock (TranscribeCalls) TranscribeCalls.Add((audio.Length, mimeType, languageHint));
        return Down ? throw new HttpRequestException("AI service unreachable") : Task.FromResult(Transcript);
    }

    public Task<AiUnderstanding> UnderstandAsync(string text, IReadOnlyList<AiCategory> categories, IReadOnlyList<string> knownNames, IReadOnlyList<string> landmarks, CancellationToken ct)
    {
        lock (Calls) Calls.Add((text, categories, knownNames, landmarks));
        return Down ? throw new HttpRequestException("AI service unreachable") : Task.FromResult(Reply);
    }
}

[CollectionDefinition("db")]
public sealed class DbCollection : ICollectionFixture<TestApp>;

/// <summary>Keeps every text message the application "sends", so tests can read the one-time codes.</summary>
public sealed class RecordingSmsSender : UrbanFix.Infrastructure.Auth.ISmsSender
{
    public List<(string Phone, string Message)> Sent { get; } = [];

    public Task SendAsync(string phone, string message, CancellationToken ct)
    {
        lock (Sent) Sent.Add((phone, message));
        return Task.CompletedTask;
    }

    public string? LastCode(string phone)
    {
        lock (Sent) return Sent.LastOrDefault(s => s.Phone == phone).Message is { } m ? System.Text.RegularExpressions.Regex.Match(m, @"\d{6}").Value : null;
    }

    public int CountFor(string phone) { lock (Sent) return Sent.Count(s => s.Phone == phone); }
}


/// <summary>A push service the tests control: what it answers, and what it was asked to send.</summary>
public sealed class FakePushSender(string kind) : UrbanFix.Infrastructure.Push.IPushSender
{
    public string Kind => kind;
    public UrbanFix.Infrastructure.Push.PushOutcome Outcome { get; set; } = UrbanFix.Infrastructure.Push.PushOutcome.Sent;
    public List<(string Token, UrbanFix.Infrastructure.Push.PushMessage Message)> Sent { get; } = [];

    public Task<UrbanFix.Infrastructure.Push.PushResult> SendAsync(UrbanFix.Infrastructure.Push.PushDevice device, UrbanFix.Infrastructure.Push.PushMessage message, CancellationToken ct)
    {
        lock (Sent) Sent.Add((device.Token, message));
        return Task.FromResult(new UrbanFix.Infrastructure.Push.PushResult(Outcome, Outcome == UrbanFix.Infrastructure.Push.PushOutcome.Sent ? "fake-" + Sent.Count : null, Outcome == UrbanFix.Infrastructure.Push.PushOutcome.Sent ? null : "fake_" + Outcome));
    }

    public void Reset() { lock (Sent) Sent.Clear(); Outcome = UrbanFix.Infrastructure.Push.PushOutcome.Sent; }
}

public sealed class FakePush
{
    public FakePushSender Fcm { get; } = new("fcm");
    public FakePushSender Web { get; } = new("web");
}
