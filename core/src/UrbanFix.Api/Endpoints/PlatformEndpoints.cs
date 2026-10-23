using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Platform;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>
/// What the platform team uses to run the societies on it: onboarding, feature flags, AI quality and cost, and support access
/// (PA-01 to PA-05, FD-13). All of it is for platform admins; a society's own people only ever read their own flags.
/// </summary>
public static class PlatformEndpoints
{
    public sealed record PlatformOrganizationDto(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt, int Members, int Houses, int Complaints, DateTimeOffset? LastComplaintAt);
    public sealed record OnboardingDto(Guid OrganizationId, IReadOnlyList<OnboardingStep> Steps, bool ReadyForUse);
    public sealed record FeatureDto(string Feature, bool Enabled, bool Default);
    public sealed record SetFeature(bool Enabled);
    public sealed record SetAiCap(long MonthlyTokenCap);
    public sealed record AiUsageDto(DateTimeOffset MonthStart, IReadOnlyList<AiUsageRow> Organizations);
    public sealed record OpenSupport(string Reason, int? Minutes);
    public sealed record SupportOpened(Guid SessionId, string Token, DateTimeOffset ExpiresAt, Guid OrganizationId);

    /// <summary>The steps that must be done before a society can take its first complaint.</summary>
    public static readonly string[] RequiredSteps = ["manager", "categories", "places", "houses", "teams"];

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin").WithTags("Platform");
        var platform = AuthPolicies.For(Permissions.OrganizationManage);
        var audit = AuthPolicies.For(Permissions.AuditRead);

        // PA-01 ----------------------------------------------------------------------------------------------------
        g.MapGet("/organizations/overview", async (PlatformData data, CancellationToken ct) =>
            Results.Ok((await data.OrganizationsAsync(ct)).Select(o => new PlatformOrganizationDto(o.Id, o.Name, o.Slug, o.CreatedAt, o.Members, o.Houses, o.Complaints, o.LastComplaintAt)).ToList()))
            .Produces<List<PlatformOrganizationDto>>(200).RequireAuthorization(platform);

        g.MapGet("/organizations/{id:guid}/onboarding", async (Guid id, PlatformData data, CancellationToken ct) =>
        {
            if (!await data.OrganizationExistsAsync(id, ct)) return Results.NotFound();
            var steps = await data.OnboardingAsync(id, ct);
            return Results.Ok(new OnboardingDto(id, steps, RequiredSteps.All(r => steps.Any(s => s.Key == r && s.Done))));
        }).Produces<OnboardingDto>(200).RequireAuthorization(platform);

        // PA-02 / FD-13 --------------------------------------------------------------------------------------------
        g.MapGet("/organizations/{id:guid}/features", async (Guid id, PlatformData data, CancellationToken ct) =>
        {
            if (!await data.OrganizationExistsAsync(id, ct)) return Results.NotFound();
            return Results.Ok((await data.FeaturesAsync(id, ct)).OrderBy(f => f.Key).Select(f => new FeatureDto(f.Key, f.Value, Features.Defaults[f.Key])).ToList());
        }).Produces<List<FeatureDto>>(200).RequireAuthorization(platform);

        g.MapPut("/organizations/{id:guid}/features/{feature}", async (Guid id, string feature, SetFeature req, ClaimsPrincipal user, HttpContext http, PlatformData data, CancellationToken ct) =>
        {
            if (!Features.IsKnown(feature)) return Results.BadRequest(new { error = "unknown_feature" });
            if (!await data.OrganizationExistsAsync(id, ct)) return Results.NotFound();
            await data.SetFeatureAsync(id, feature, req.Enabled, Guid.Parse(user.FindFirstValue("sub")!), http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Ok(new FeatureDto(feature, req.Enabled, Features.Defaults[feature]));
        }).Produces<FeatureDto>(200).RequireAuthorization(platform);

        // A society reads its own flags, so the apps can hide what is not switched on for it.
        app.MapGet("/features", async (AppDbContext db, CancellationToken ct) => Results.Ok(await FeatureFlags.AllAsync(db, ct)))
            .WithTags("Platform").Produces<Dictionary<string, bool>>(200).RequireAuthorization(AuthPolicies.Verified);

        // PA-03 ----------------------------------------------------------------------------------------------------
        g.MapGet("/ai/quality", async (Guid? organizationId, DateTimeOffset? from, DateTimeOffset? to, PlatformData data, CancellationToken ct) =>
            Results.Ok(await data.AiQualityAsync(organizationId, from, to, ct))).Produces<List<AiQualityRow>>(200).RequireAuthorization(platform);

        // PA-04 ----------------------------------------------------------------------------------------------------
        g.MapGet("/ai/usage", async (DateTimeOffset? month, PlatformData data, CancellationToken ct) =>
        {
            var at = month ?? DateTimeOffset.UtcNow;
            var start = new DateTimeOffset(at.Year, at.Month, 1, 0, 0, 0, TimeSpan.Zero);
            return Results.Ok(new AiUsageDto(start, await data.AiUsageAsync(start, ct)));
        }).Produces<AiUsageDto>(200).RequireAuthorization(platform);

        g.MapPut("/organizations/{id:guid}/ai-cap", async (Guid id, SetAiCap req, ClaimsPrincipal user, HttpContext http, PlatformData data, CancellationToken ct) =>
        {
            if (req.MonthlyTokenCap is < 0 or > 1_000_000_000) return Results.BadRequest(new { error = "ai_cap_out_of_range" });
            if (!await data.OrganizationExistsAsync(id, ct)) return Results.NotFound();
            await data.SetAiCapAsync(id, req.MonthlyTokenCap, Guid.Parse(user.FindFirstValue("sub")!), http.Connection.RemoteIpAddress?.ToString(), ct);
            return Results.Ok(req);
        }).Produces<SetAiCap>(200).RequireAuthorization(platform);

        // PA-05 ----------------------------------------------------------------------------------------------------
        g.MapGet("/support-sessions", async (PlatformData data, CancellationToken ct) => Results.Ok(await data.SupportSessionsAsync(ct)))
            .Produces<List<SupportSessionRow>>(200).RequireAuthorization(platform);

        g.MapPost("/organizations/{id:guid}/support-sessions", async (Guid id, OpenSupport req, ClaimsPrincipal user, HttpContext http, PlatformData data, CancellationToken ct) =>
        {
            // A support session cannot open another one, and a reason is always given: this is what the society and the platform will read later.
            if (user.HasClaim(c => c.Type == "support")) return Results.Json(new { error = "support_session_cannot_open_support" }, statusCode: 403);
            var reason = req.Reason?.Trim() ?? "";
            if (reason.Length is < 10 or > 500) return Results.BadRequest(new { error = "reason_required" });
            var minutes = req.Minutes ?? 30;
            if (minutes is < 1 or > PlatformData.MaxSupportMinutes) return Results.BadRequest(new { error = "minutes_out_of_range" });
            if (!await data.OrganizationExistsAsync(id, ct)) return Results.NotFound();
            var device = http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 300)] : null;
            var opened = await data.OpenSupportSessionAsync(id, Guid.Parse(user.FindFirstValue("sub")!), reason, minutes, http.Connection.RemoteIpAddress?.ToString(), device, ct);
            return Results.Created($"/admin/support-sessions/{opened.Id}", new SupportOpened(opened.Id, opened.Token, opened.ExpiresAt, id));
        }).Produces<SupportOpened>(201).RequireAuthorization(platform);

        g.MapPost("/support-sessions/{id:guid}/end", async (Guid id, ClaimsPrincipal user, HttpContext http, PlatformData data, CancellationToken ct) =>
            await data.EndSupportSessionAsync(id, Guid.Parse(user.FindFirstValue("sub")!), http.Connection.RemoteIpAddress?.ToString(), ct) ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(platform);
    }

    /// <summary>
    /// Keeps a support session to what it is for: looking, not changing, and everything it looks at is written to the society's audit log
    /// (PA-05). Runs after sign-in and before the endpoint, so a refused change never reaches the code that would make it.
    /// </summary>
    public static IApplicationBuilder UseSupportSessionGuard(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        if (ctx.User.FindFirst("support")?.Value is not { } sessionId)
        {
            await next();
            return;
        }

        var read = HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method) || HttpMethods.IsOptions(ctx.Request.Method);
        var logout = HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.StartsWithSegments("/auth/logout");
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        if (Guid.TryParse(ctx.User.FindFirstValue("org"), out var org) && Guid.TryParse(ctx.User.FindFirstValue("sub"), out var actor))
        {
            db.AuditLog.Add(new AuditEntry
            {
                OrganizationId = org, ActorUserId = actor, Action = read ? "support_viewed" : "support_change_refused", EntityType = "SupportSession", EntityId = sessionId,
                After = System.Text.Json.JsonSerializer.Serialize(new { method = ctx.Request.Method, path = ctx.Request.Path.Value }), Ip = ctx.Connection.RemoteIpAddress?.ToString(),
            });
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        if (!read && !logout)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = "support_read_only" }, ctx.RequestAborted);
            return;
        }
        await next();
    });
}
