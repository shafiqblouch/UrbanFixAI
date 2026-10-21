using Amazon.Runtime;
using Amazon.S3;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Auth;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure;

public static class DependencyInjection
{
    /// <param name="runBackgroundServers">False in tests that drive the outbox/jobs by hand.</param>
    public static IServiceCollection AddUrbanFixInfrastructure(this IServiceCollection services, IConfiguration config, bool runBackgroundServers = true)
    {
        // Tenancy and unit of work -----------------------------------------------------------------------
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<EventBus>();
        services.AddScoped<IEventBus>(sp => sp.GetRequiredService<EventBus>());
        services.AddScoped<RequestInfo>();
        services.AddScoped<TenantConnectionInterceptor>();
        services.AddScoped<UnitOfWorkInterceptor>();
        services.AddDbContext<AppDbContext>((sp, o) => o
            .UseNpgsql(config.GetConnectionString("App") ?? throw new InvalidOperationException("ConnectionStrings:App missing"))
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TenantConnectionInterceptor>(), sp.GetRequiredService<UnitOfWorkInterceptor>()));

        // Outbox ---------------------------------------------------------------------------------------
        var outbox = config.GetSection("Outbox").Get<OutboxOptions>() ?? new OutboxOptions();
        services.AddSingleton(outbox);
        services.AddSingleton<OutboxProcessor>();
        if (runBackgroundServers) services.AddHostedService<OutboxDispatcher>();

        // Identity ---------------------------------------------------------------------------------------
        services.AddSingleton<PasswordService>();
        services.AddSingleton(config.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions());
        services.AddScoped<AuthService>();
        var keyPath = config["DataProtection:KeyPath"];
        var dp = services.AddDataProtection().SetApplicationName("UrbanFix");
        if (!string.IsNullOrEmpty(keyPath)) dp.PersistKeysToFileSystem(new DirectoryInfo(keyPath));

        // Background jobs ------------------------------------------------------------------------------
        var jobs = config.GetSection("Jobs").Get<JobOptions>() ?? new JobOptions();
        services.AddSingleton(jobs);
        services.AddSingleton<IJobFailureNotifier, LoggingJobFailureNotifier>();
        services.AddHangfire((sp, c) => c
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(config.GetConnectionString("Owner") ?? config.GetConnectionString("App")!))
            .UseFilter(new AutomaticRetryAttribute { Attempts = jobs.Attempts, DelaysInSeconds = jobs.EffectiveDelays, OnAttemptsExceeded = AttemptsExceededAction.Fail })
            .UseFilter(new FailureAlertFilter(sp.GetRequiredService<IJobFailureNotifier>())));
        if (runBackgroundServers) services.AddHangfireServer();

        // Object storage -------------------------------------------------------------------------------
        var storage = config.GetSection("Storage").Get<StorageOptions>() ?? new StorageOptions();
        services.AddSingleton(storage);
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
            new BasicAWSCredentials(storage.AccessKey, storage.SecretKey),
            new AmazonS3Config { ServiceURL = storage.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" }));
        services.AddScoped<IObjectStorage, ObjectStorage>();

        // Complaints: numbering, media, AI enrichment ---------------------------------------------------
        var ai = config.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton(ai);
        services.AddHttpClient<IAiClient, HttpAiClient>(http =>
        {
            http.BaseAddress = new Uri(ai.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(40);
            if (!string.IsNullOrEmpty(ai.ServiceToken)) http.DefaultRequestHeaders.Authorization = new("Bearer", ai.ServiceToken);
        });
        services.AddScoped<ComplaintEnrichmentJob>();
        services.AddScoped<IEventHandler<ComplaintCreated>, EnqueueEnrichmentOnCreate>();
        services.AddScoped<NumberGenerator>();
        services.AddScoped<IMediaProcessor, MediaProcessor>();

        return services;
    }

    /// <summary>Applies migrations as the owner role. Call once at startup (or from a deployment step).</summary>
    public static Task MigrateDatabaseAsync(IConfiguration config, CancellationToken ct = default)
    {
        var owner = config.GetConnectionString("Owner") ?? throw new InvalidOperationException("ConnectionStrings:Owner missing");
        var password = config["Database:AppRolePassword"] ?? throw new InvalidOperationException("Database:AppRolePassword missing");
        return Migrator.MigrateAsync(owner, password, ct);
    }
}
