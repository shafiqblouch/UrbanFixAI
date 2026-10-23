using Amazon.Runtime;
using Amazon.S3;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Auth;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.Infrastructure.WhatsApp;
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
        var otp = config.GetSection("Otp").Get<OtpOptions>() ?? new OtpOptions();
        services.AddSingleton(otp);
        services.TryAddSingleton<ISmsSender, LoggingSmsSender>();
        services.AddScoped<OtpService>();
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
        services.AddHttpClient<HttpAiClient>(http =>
        {
            http.BaseAddress = new Uri(ai.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(40);
            if (!string.IsNullOrEmpty(ai.ServiceToken)) http.DefaultRequestHeaders.Authorization = new("Bearer", ai.ServiceToken);
        });
        services.AddTransient<IAiBackend>(sp => sp.GetRequiredService<HttpAiClient>());
        services.AddScoped<AiUsageContext>();
        services.AddScoped<IAiClient, MeteredAiClient>();
        services.AddScoped<ComplaintEnrichmentJob>();
        services.AddScoped<IEventHandler<ComplaintCreated>, EnqueueEnrichmentOnCreate>();
        services.AddScoped<VoiceTranscriptionJob>();
        services.AddScoped<PhotoInterpretationJob>();
        services.AddScoped<EmbeddingJob>();
        services.AddScoped<IEventHandler<ComplaintCreated>, EnqueueEmbeddingOnCreate>();
        services.AddScoped<IEventHandler<MediaAdded>, EnqueueTranscriptionOnAudio>();
        services.AddScoped<NumberGenerator>();
        services.AddScoped<ComplaintPlanner>();
        services.AddScoped<ComplaintIntake>();
        services.AddScoped<IMediaProcessor, MediaProcessor>();

        // Notifications ------------------------------------------------------------------------------------
        services.AddSingleton<INotificationPusher, NullNotificationPusher>();
        services.AddScoped<INotificationChannel, InAppChannel>();
        services.AddScoped<NotificationService>();
        services.AddScoped<LifecycleNotifications>();
        services.AddScoped<IEventHandler<ComplaintCreated>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<ComplaintStatusChanged>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<JobAssigned>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<JobStatusChanged>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<EscalationRaised>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<ComplaintSafetyFlagged>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<ConfirmationReminder>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        // WhatsApp, text messages and email (WA-02 to WA-06, NT-02 to NT-04). Each is installed only when it is configured.
        var whatsApp = config.GetSection("WhatsApp").Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        services.AddSingleton(whatsApp);
        services.AddHttpClient<IWhatsAppClient, HttpWhatsAppClient>(http => HttpWhatsAppClient.Configure(http, whatsApp));
        services.AddScoped<WhatsAppSender>();
        services.AddScoped<WhatsAppInbox>();
        services.AddScoped<WhatsAppConversationService>();
        services.AddScoped<WhatsAppInboundJob>();
        if (whatsApp.Enabled) services.AddScoped<INotificationChannel, WhatsAppNotificationChannel>();

        var smsGateway = config.GetSection("Sms").Get<SmsGatewayOptions>() ?? new SmsGatewayOptions();
        services.AddSingleton(smsGateway);
        if (!string.IsNullOrEmpty(smsGateway.Url))
        {
            services.AddHttpClient<HttpSmsSender>();
            services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<HttpSmsSender>());
            services.AddScoped<INotificationChannel, SmsNotificationChannel>();
        }

        var email = config.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
        services.AddSingleton(email);
        if (!string.IsNullOrEmpty(email.Host))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
            services.AddScoped<INotificationChannel, EmailNotificationChannel>();
        }

        // Push to phones and browsers (FD-16). Each service is installed only when it is configured; the channel only when at least one is.
        var push = config.GetSection("Push").Get<UrbanFix.Infrastructure.Push.PushOptions>() ?? new UrbanFix.Infrastructure.Push.PushOptions();
        services.AddSingleton(push);
        services.AddHttpClient("push");
        services.AddScoped<UrbanFix.Infrastructure.Push.PushService>();
        if (push.Fcm.Enabled) services.AddSingleton<UrbanFix.Infrastructure.Push.IPushSender>(sp => new UrbanFix.Infrastructure.Push.FcmPushSender(sp.GetRequiredService<IHttpClientFactory>().CreateClient("push"), push.Fcm));
        if (push.Web.Enabled) services.AddSingleton<UrbanFix.Infrastructure.Push.IPushSender>(sp => new UrbanFix.Infrastructure.Push.WebPushSender(sp.GetRequiredService<IHttpClientFactory>().CreateClient("push"), push.Web));
        if (push.LogOnly)
            foreach (var kind in UrbanFix.Infrastructure.Push.PushKinds.All)
                services.AddSingleton<UrbanFix.Infrastructure.Push.IPushSender>(sp => new UrbanFix.Infrastructure.Push.LogPushSender(kind, sp.GetRequiredService<ILogger<UrbanFix.Infrastructure.Push.LogPushSender>>()));
        if (push.Fcm.Enabled || push.Web.Enabled || push.LogOnly) services.AddScoped<INotificationChannel, UrbanFix.Infrastructure.Push.PushChannel>();

        services.AddSingleton<EscalationScanner>();
        services.AddSingleton<ConfirmationSweeper>();
        services.AddScoped<ApprovalService>();
        services.AddScoped<StockService>();
        services.AddScoped<DuplicateFinder>();
        services.AddScoped<IncidentService>();
        services.AddScoped<IEventHandler<JobStatusChanged>, QueueChargeOnCompletion>();
        services.AddScoped<IEventHandler<StockLow>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<StockRequested>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<StockRequestDecided>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<MaterialsMismatch>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<UrbanFix.Infrastructure.Registry.DirectoryImporter>();
        services.AddSingleton<ApprovalSweeper>();
        services.AddScoped<NoticeService>();
        services.AddSingleton<UrbanFix.Infrastructure.Platform.PlatformData>();
        services.AddSingleton<UrbanFix.Infrastructure.Pdf.IPdfRenderer, UrbanFix.Infrastructure.Pdf.PdfRenderer>();
        services.AddScoped<IEventHandler<NoticePublished>>(sp => sp.GetRequiredService<NoticeService>());
        services.AddSingleton<NoticeSweeper>();
        services.AddScoped<IEventHandler<ApprovalRequested>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<ApprovalDecided>>(sp => sp.GetRequiredService<LifecycleNotifications>());
        services.AddScoped<IEventHandler<CostOverrun>>(sp => sp.GetRequiredService<LifecycleNotifications>());

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
