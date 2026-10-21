using Hangfire;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using UrbanFix.Api.Endpoints;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddUrbanFixInfrastructure(builder.Configuration, runBackgroundServers: !builder.Configuration.GetValue("Background:Disabled", false));
builder.Services.AddUrbanFixAuthorization();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

if (!builder.Environment.IsDevelopment() && builder.Configuration["Database:AppRolePassword"] == "urbanfix_app_dev")
    throw new InvalidOperationException("Refusing to start outside Development with the default database role password.");

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
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) app.MapOpenApi().AllowAnonymous();

app.MapGet("/health/live", () => Results.Ok(new { status = "ok", service = "core" })).AllowAnonymous();
app.MapGet("/health/ready", async (UrbanFix.Infrastructure.Persistence.AppDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.Json(new { status = "database_unreachable" }, statusCode: 503)).AllowAnonymous();

app.MapAuthEndpoints();
app.MapAdminEndpoints();
app.MapSetupEndpoints();
app.MapComplaintEndpoints();
app.MapPricingEndpoints();
app.MapHangfireDashboard("/admin/jobs", new DashboardOptions { Authorization = [] })
    .RequireAuthorization(AuthPolicies.For(Permissions.JobsDashboard));

app.Run();

public partial class Program;
