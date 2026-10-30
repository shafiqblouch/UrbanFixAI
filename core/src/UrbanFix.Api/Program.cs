using Hangfire;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using UrbanFix.Api.Endpoints;
using UrbanFix.Api.Reports;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

// Hosted databases hand out one postgres:// URL (the owner login); derive both connection strings from it.
// The app role is created by the migrator with Database:AppRolePassword.
if (builder.Configuration["DATABASE_URL"] is { Length: > 0 } databaseUrl)
{
    var uri = new Uri(databaseUrl);
    var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
    var password = Uri.UnescapeDataString(uri.UserInfo.Split(':', 2)[1]);
    string Conn(string u, string p) => new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host, Port = uri.Port > 0 ? uri.Port : 5432, Database = uri.AbsolutePath.TrimStart('/'),
        Username = u, Password = p, SslMode = SslMode.Prefer,
    }.ConnectionString;
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Owner"] = Conn(user, password),
        ["ConnectionStrings:App"] = Conn("urbanfix_app", builder.Configuration["Database:AppRolePassword"] ?? ""),
    });
}

// Observability (FD-09): structured logs, traces, Sentry. All exporters are off unless configured.
builder.Host.UseSerilog((ctx, services, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("service", "core")
    .WriteTo.Console(ctx.HostingEnvironment.IsDevelopment()
        ? new Serilog.Formatting.Display.MessageTemplateTextFormatter("[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
        : new Serilog.Formatting.Compact.RenderedCompactJsonFormatter()));

if (!string.IsNullOrWhiteSpace(builder.Configuration["Sentry:Dsn"]))
    builder.WebHost.UseSentry(o => { o.Dsn = builder.Configuration["Sentry:Dsn"]; o.Environment = builder.Environment.EnvironmentName; });

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("urbanfix-core"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddNpgsql().AddSource("UrbanFix.*");
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])) t.AddOtlpExporter();
    });

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddUrbanFixInfrastructure(builder.Configuration, runBackgroundServers: !builder.Configuration.GetValue("Background:Disabled", false));
builder.Services.AddUrbanFixAuthorization();
// After the infrastructure default, so live pushes go through SignalR.
builder.Services.AddSingleton<UrbanFix.Infrastructure.Notifications.INotificationPusher, SignalRNotificationPusher>();
builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<UrbanFix.Api.Reports.MonthlySummaryService>();
builder.Services.AddSingleton<UrbanFix.Api.Reports.MonthlySummaryJob>();
builder.Services.AddScoped<UrbanFix.SharedKernel.IEventHandler<UrbanFix.Infrastructure.Reports.MonthlySummaryReady>, UrbanFix.Api.Reports.MonthlySummaryNotifications>();
builder.Services.AddUrbanFixRateLimiting(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

if (!builder.Environment.IsDevelopment() && builder.Configuration["Database:AppRolePassword"] == "urbanfix_app_dev")
    throw new InvalidOperationException("Refusing to start outside Development with the default database role password.");

if (!string.IsNullOrEmpty(builder.Configuration["Auth:DemoTotpCode"]) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Refusing to start outside Development with a fixed authenticator code (Auth:DemoTotpCode).");
if (!string.IsNullOrEmpty(builder.Configuration["Otp:FixedCode"]) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Refusing to start outside Development with a fixed phone code (Otp:FixedCode).");

var app = builder.Build();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await DependencyInjection.MigrateDatabaseAsync(builder.Configuration);
    if (builder.Configuration["Bootstrap:PlatformAdminEmail"] is { Length: > 0 } email
        && builder.Configuration["Bootstrap:PlatformAdminPassword"] is { Length: > 0 } password)
    {
        await Bootstrapper.EnsurePlatformAdminAsync(
            builder.Configuration.GetConnectionString("Owner")!, email, password, app.Services.GetRequiredService<PasswordService>());
    }
}

app.UseSerilogRequestLogging();
app.Use(async (ctx, next) =>
{
    using (Serilog.Context.LogContext.PushProperty("traceId", System.Diagnostics.Activity.Current?.TraceId.ToString()))
        await next();
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseSupportSessionGuard();
app.UseAuthorization();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapOpenApi().AllowAnonymous();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "core" })).AllowAnonymous();
app.MapGet("/health/ready", async (UrbanFix.Infrastructure.Persistence.AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "database_unreachable" }, statusCode: 503)).AllowAnonymous();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapSetupEndpoints();
app.MapComplaintEndpoints();
app.MapJobEndpoints();
app.MapMapEndpoints();
app.MapRoutingEndpoints();
app.MapDashboardEndpoints();
app.MapPricingEndpoints();
app.MapNotificationEndpoints();
app.MapWhatsAppEndpoints();
app.MapApprovalEndpoints();
app.MapRegistryEndpoints();
app.MapStockEndpoints();
app.MapCostReportEndpoints();
app.MapIncidentEndpoints();
app.MapDirectoryEndpoints();
app.MapNoticeEndpoints();
app.MapExportEndpoints();
app.MapPushEndpoints();
app.MapPlatformEndpoints();
app.MapMonthlySummaryEndpoints();
if (!builder.Configuration.GetValue("Background:Disabled", false))
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<UrbanFix.Infrastructure.Complaints.EscalationScanner>("escalation-scan", x => x.RunAsync(CancellationToken.None), "*/5 * * * *");
if (!builder.Configuration.GetValue("Background:Disabled", false))
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<UrbanFix.Infrastructure.Complaints.ConfirmationSweeper>("confirmation-sweep", x => x.RunAsync(CancellationToken.None), "0 * * * *");
if (!builder.Configuration.GetValue("Background:Disabled", false))
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<UrbanFix.Infrastructure.Complaints.ApprovalSweeper>("approval-sweep", x => x.RunAsync(CancellationToken.None), "15 * * * *");
if (!builder.Configuration.GetValue("Background:Disabled", false))
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<UrbanFix.Api.Reports.MonthlySummaryJob>("monthly-summary", x => x.RunAsync(CancellationToken.None), "30 1 * * *");
if (!builder.Configuration.GetValue("Background:Disabled", false))
    app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<UrbanFix.Infrastructure.Notifications.NoticeSweeper>("notice-sweep", x => x.RunAsync(CancellationToken.None), "* * * * *");
app.MapHangfireDashboard("/admin/jobs", new DashboardOptions { Authorization = [] })
    .RequireAuthorization(AuthPolicies.For(Permissions.JobsDashboard));

app.Run();

public partial class Program;
