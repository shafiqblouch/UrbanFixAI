using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Api.Endpoints;

/// <summary>Routing rules (RT-01), the supervisor review queue (RT-03) and capture of AI acceptance / override (AI-16).</summary>
public static class RoutingEndpoints
{
    public sealed record SaveRule(string Name, Guid? CategoryId, Guid? LocationId, Guid TeamId, int? SortOrder, bool? IsActive);
    public sealed record RuleDto(Guid Id, string Name, Guid? CategoryId, Guid? LocationId, Guid TeamId, int SortOrder, bool IsActive);
    public sealed record Triage(bool? AcceptAi, Guid? CategoryId, string? Priority, Guid? LocationId, string? LocationText);
    public sealed record QueueItemDto(ComplaintDto Complaint, Guid? SuggestedCategoryId, string? SuggestedPriority, Guid? SuggestedTeamId, string? SuggestedTeamName);
    public sealed record SuggestionDto(
        Guid Id, string Task, string Status, int Attempt, string? CategoryKey, string? Summary, string? Language, string? LocationText,
        bool? NeedsMoreInfo, float? Confidence, string? Model, string? PromptVersion, string? Error, DateTimeOffset CreatedAt);
    public sealed record SaveSla(Guid? CategoryId, string Priority, int RespondHours, int ResolveHours, bool? BusinessHoursOnly, bool? IsActive);
    public sealed record SlaDto(Guid Id, Guid? CategoryId, string Priority, int RespondHours, int ResolveHours, bool BusinessHoursOnly, bool IsActive);
    public sealed record SaveAutoRoute(bool Enabled, float? Threshold);
    public sealed record ReadinessDto(Guid CategoryId, string Key, string Name, bool AutoRoute, int Samples, double? Accuracy, double RequiredAccuracy, int RequiredSamples, bool Ready);
    public sealed record AutoRouteDto(Guid CategoryId, bool Enabled, float Threshold);
    public sealed record SaveRisk(int Level);
    public sealed record LevelDto(int Level, int AfterHours, Role Role);
    public sealed record EscalationDto(Guid Id, Guid ComplaintId, string Reference, Guid? JobId, string Rule, int Level, DateTimeOffset Since, DateTimeOffset RaisedAt, DateTimeOffset? ResolvedAt, string? Resolution);
    public sealed record UsageRow(string Task, string Model, int Calls, long InputTokens, long OutputTokens, decimal? CostUsd);
    public sealed record UsageReport(
        DateTimeOffset From, DateTimeOffset To, long MonthTokens, int MonthlyTokenCap, int Calls, long InputTokens, long OutputTokens,
        decimal? CostUsd, bool CostComplete, int ComplaintsWithAi, decimal? CostPerComplaint, long TokensPerComplaint, IReadOnlyList<UsageRow> ByTask);
    public sealed record AccuracyRow(string? Language, string Suggested, int Total, int Accepted, double AcceptRate);

    private static readonly string[] Priorities = PriorityLevels.Ordered;
    private static readonly ComplaintStatus[] Triageable = [ComplaintStatus.New, ComplaintStatus.UnderReview, ComplaintStatus.OnHold];

    public static void MapRoutingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Routing");
        var manage = AuthPolicies.For(Permissions.CatalogManage);
        var triage = AuthPolicies.For(Permissions.ComplaintTriage);

        g.MapGet("/routing-rules", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.RoutingRules.AsNoTracking().OrderBy(r => r.SortOrder).ThenBy(r => r.Name).ToListAsync(ct)).Select(RuleOf)))
            .Produces<List<RuleDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintAssign));

        g.MapPost("/routing-rules", async (SaveRule req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var rule = new RoutingRule { OrganizationId = tenant.OrganizationId!.Value };
            return await SaveRuleAsync(rule, req, db, ct, created: true);
        }).Produces<RuleDto>(201).RequireAuthorization(manage);

        g.MapPut("/routing-rules/{id:guid}", async (Guid id, SaveRule req, AppDbContext db, CancellationToken ct) =>
            await db.RoutingRules.SingleOrDefaultAsync(r => r.Id == id, ct) is { } rule ? await SaveRuleAsync(rule, req, db, ct, created: false) : Results.NotFound())
            .Produces<RuleDto>(200).RequireAuthorization(manage);

        // Priority, SLA and auto-routing settings (RT-02, RT-04, RT-06, RT-10)
        g.MapGet("/sla-policies", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.SlaPolicies.AsNoTracking().OrderBy(p => p.CategoryId).ToListAsync(ct)).OrderBy(p => PriorityLevels.Rank(p.Priority)).Select(SlaOf)))
            .Produces<List<SlaDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintAssign));
        g.MapPost("/sla-policies", async (SaveSla req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
            await SaveSlaAsync(new SlaPolicy { OrganizationId = tenant.OrganizationId!.Value }, req, db, ct, created: true)).Produces<SlaDto>(201).RequireAuthorization(manage);
        g.MapPut("/sla-policies/{id:guid}", async (Guid id, SaveSla req, AppDbContext db, CancellationToken ct) =>
            await db.SlaPolicies.SingleOrDefaultAsync(p => p.Id == id, ct) is { } p ? await SaveSlaAsync(p, req, db, ct, created: false) : Results.NotFound())
            .Produces<SlaDto>(200).RequireAuthorization(manage);
        g.MapGet("/reports/auto-route-readiness", async (AppDbContext db, IConfiguration config, CancellationToken ct) =>
            Results.Ok(await ReadinessAsync(db, config, ct))).Produces<List<ReadinessDto>>(200).RequireAuthorization(manage);
        g.MapPut("/categories/{id:guid}/auto-route", async (Guid id, SaveAutoRoute req, AppDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var threshold = req.Threshold ?? AutoRoutePolicy.DefaultThreshold;
            if (RuleChecks.AutoRoute(threshold) is { } bad) return Results.BadRequest(new { error = bad });
            if (await db.Categories.SingleOrDefaultAsync(c => c.Id == id, ct) is not { } c) return Results.NotFound();
            if (req.Enabled && !await UrbanFix.Infrastructure.Platform.FeatureFlags.IsEnabledAsync(db, UrbanFix.Infrastructure.Platform.Features.AutoRouting, ct))
                return Results.Json(new { error = "feature_disabled", feature = UrbanFix.Infrastructure.Platform.Features.AutoRouting }, statusCode: 403);
            // AI-17: a category may only be switched to automatic once the AI has been right often enough on it.
            if (req.Enabled && !c.AutoRoute && (await ReadinessAsync(db, config, ct, c.Id)).Single() is { Ready: false } gate)
                return Results.Conflict(new { error = "accuracy_below_gate", gate.Samples, gate.Accuracy, gate.RequiredAccuracy, gate.RequiredSamples });
            c.AutoRoute = req.Enabled; c.AutoRouteThreshold = threshold;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new AutoRouteDto(c.Id, c.AutoRoute, c.AutoRouteThreshold));
        }).Produces<AutoRouteDto>(200).RequireAuthorization(manage);
        g.MapPut("/locations/{id:guid}/risk", async (Guid id, SaveRisk req, AppDbContext db, CancellationToken ct) =>
        {
            if (req.Level is < 0 or > 2) return Results.BadRequest(new { error = "invalid_risk_level" });
            if (await db.Locations.SingleOrDefaultAsync(l => l.Id == id, ct) is not { } l) return Results.NotFound();
            l.RiskLevel = req.Level;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { id = l.Id, level = l.RiskLevel });
        }).RequireAuthorization(manage);

        g.MapGet("/escalation-levels", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.EscalationLevels.AsNoTracking().OrderBy(l => l.Level).ToListAsync(ct);
            return Results.Ok(rows.Count > 0 ? rows.Select(l => new LevelDto(l.Level, l.AfterHours, l.Role)).ToList()
                : EscalationRules.DefaultLevels.Select(l => new LevelDto(l.Level, l.AfterHours, l.Role)).ToList());
        }).Produces<List<LevelDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintAssign));
        g.MapPut("/escalation-levels", async (List<LevelDto> req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (EscalationRules.ValidateLevels(req.Select(l => (l.Level, l.AfterHours, l.Role)).ToList()) is { } bad) return Results.BadRequest(new { error = bad });
            db.EscalationLevels.RemoveRange(await db.EscalationLevels.ToListAsync(ct));
            db.EscalationLevels.AddRange(req.Select(l => new EscalationLevel { OrganizationId = tenant.OrganizationId!.Value, Level = l.Level, AfterHours = l.AfterHours, Role = l.Role }));
            await db.SaveChangesAsync(ct);
            return Results.Ok(req.OrderBy(l => l.Level).ToList());
        }).Produces<List<LevelDto>>(200).RequireAuthorization(manage);
        g.MapGet("/escalations", async (bool? open, AppDbContext db, CancellationToken ct) =>
        {
            var q = from e in db.Escalations.AsNoTracking()
                    join c in db.Complaints.AsNoTracking() on e.ComplaintId equals c.Id
                    where open == null || (open == true) == (e.ResolvedAt == null)
                    orderby e.RaisedAt descending
                    select new EscalationDto(e.Id, e.ComplaintId, c.Reference, e.JobId, e.Rule, e.Level, e.Since, e.RaisedAt, e.ResolvedAt, e.Resolution);
            return Results.Ok(await q.Take(200).ToListAsync(ct));
        }).Produces<List<EscalationDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintAssign));

        g.MapGet("/review-queue", ReviewQueueAsync).Produces<List<QueueItemDto>>(200).RequireAuthorization(triage);
        g.MapGet("/complaints/{id:guid}/ai-suggestions", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.AiSuggestions.AsNoTracking().Where(s => s.ComplaintId == id).OrderBy(s => s.CreatedAt)
                .Select(s => new SuggestionDto(s.Id, s.Task, s.Status, s.Attempt, s.CategoryKey, s.Summary, s.Language, s.LocationText, s.NeedsMoreInfo, s.Confidence, s.Model, s.PromptVersion, s.Error, s.CreatedAt))
                .ToListAsync(ct)))
            .Produces<List<SuggestionDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));
        g.MapPost("/complaints/{id:guid}/triage", TriageAsync).Produces<ComplaintEndpoints.ComplaintDto>(200).RequireAuthorization(triage);
        g.MapGet("/reports/ai-usage", UsageAsync).Produces<UsageReport>(200).RequireAuthorization(AuthPolicies.For(Permissions.DashboardRead));
        g.MapGet("/reports/ai-accuracy", AccuracyAsync).Produces<List<AccuracyRow>>(200).RequireAuthorization(AuthPolicies.For(Permissions.DashboardRead));
    }

    // Rules ---------------------------------------------------------------------------------------------------------

    private static async Task<IResult> SaveRuleAsync(RoutingRule rule, SaveRule req, AppDbContext db, CancellationToken ct, bool created)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
        if (!await db.Teams.AnyAsync(t => t.Id == req.TeamId && t.IsActive, ct)) return Results.BadRequest(new { error = "team_not_found" });
        if (req.CategoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c, ct)) return Results.BadRequest(new { error = "category_not_found" });
        if (req.LocationId is { } l && !await db.Locations.AnyAsync(x => x.Id == l, ct)) return Results.BadRequest(new { error = "location_not_found" });

        var existing = (await db.RoutingRules.AsNoTracking().ToListAsync(ct)).Select(r => new RuleChecks.RuleKey(r.Id, r.CategoryId, r.LocationId, r.TeamId, r.SortOrder, r.IsActive));
        var candidate = new RuleChecks.RuleKey(created ? null : rule.Id, req.CategoryId, req.LocationId, req.TeamId, req.SortOrder ?? rule.SortOrder, req.IsActive ?? rule.IsActive);
        if (RuleChecks.Ambiguous(candidate, existing) is { } clash) return Results.Conflict(new { error = clash });

        rule.Name = req.Name.Trim(); rule.CategoryId = req.CategoryId; rule.LocationId = req.LocationId; rule.TeamId = req.TeamId;
        rule.SortOrder = req.SortOrder ?? rule.SortOrder;
        if (req.IsActive is { } active) rule.IsActive = active;
        if (created) db.RoutingRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return created ? Results.Created($"/routing-rules/{rule.Id}", RuleOf(rule)) : Results.Ok(RuleOf(rule));
    }

    internal static Task<Guid?> SuggestTeamAsync(AppDbContext db, Guid? categoryId, Guid? locationId, CancellationToken ct) =>
        RoutingLookup.SuggestTeamAsync(db, categoryId, locationId, ct);

    // Review queue (RT-03) ------------------------------------------------------------------------------------------

    private static async Task<IResult> ReviewQueueAsync(int? limit, AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Complaints.AsNoTracking()
            .Where(c => c.Status == ComplaintStatus.New || c.Status == ComplaintStatus.UnderReview)
            .OrderBy(c => c.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        // Most urgent first, oldest first within a priority.
        rows = rows.OrderByDescending(c => Array.IndexOf(Priorities, c.Priority)).ThenBy(c => c.CreatedAt).ToList();

        var categories = await db.Categories.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var teams = await db.Teams.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var items = new List<QueueItemDto>();
        foreach (var c in rows)
        {
            var suggested = c.CategoryId is { } cid ? categories.FirstOrDefault(x => x.Id == cid)
                : c.AiCategoryKey is { } key ? categories.FirstOrDefault(x => x.Key == key) : null;
            var team = await SuggestTeamAsync(db, suggested?.Id ?? c.CategoryId, c.LocationId, ct);
            items.Add(new QueueItemDto(ToDto(c), suggested?.Id, suggested?.DefaultPriority, team, team is { } t ? teams.GetValueOrDefault(t) : null));
        }
        return Results.Ok(items);
    }

    private static async Task<IResult> TriageAsync(
        Guid id, Triage req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct)
    {
        var actorId = Guid.Parse(user.FindFirstValue("sub")!);
        var c = await db.Complaints.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Results.NotFound();
        if (!Triageable.Contains(c.Status)) return Results.Conflict(new { error = "not_triageable", status = c.Status });
        if (req.Priority is not null && !Priorities.Contains(req.Priority)) return Results.BadRequest(new { error = "invalid_priority" });

        Category? category = null;
        var categoryId = req.CategoryId;
        if (req.AcceptAi == true)
        {
            if (c.AiCategoryKey is null) return Results.BadRequest(new { error = "no_ai_suggestion" });
            category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(x => x.Key == c.AiCategoryKey && x.IsActive, ct);
            if (category is null) return Results.BadRequest(new { error = "ai_category_unknown" });
        }
        else if (categoryId is { } cid)
        {
            category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid && x.IsActive, ct);
            if (category is null) return Results.BadRequest(new { error = "category_not_found" });
        }
        if (req.LocationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid, ct)) return Results.BadRequest(new { error = "location_not_found" });

        if (category is not null)
        {
            c.CategoryId = category.Id;
            // Picking a category re-derives the priority unless the supervisor sets one explicitly.
            if (req.Priority is null) c.Priority = category.DefaultPriority;
            if (c.AiCategoryKey is { } suggested) await RecordFeedbackAsync(db, c, "category", suggested, category.Key, actorId, ct);
        }
        if (req.Priority is not null) c.Priority = req.Priority;
        if (req.LocationId is not null) c.LocationId = req.LocationId;
        if (req.LocationText is not null) c.LocationText = string.IsNullOrWhiteSpace(req.LocationText) ? null : req.LocationText.Trim();
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); // the audit interceptor records who changed which field (ID-08)
        return Results.Ok(ToDto(c));
    }

    /// <summary>How often the AI's category suggestion was accepted over the last 90 days, per category, against the gate (AI-17).</summary>
    private static async Task<List<ReadinessDto>> ReadinessAsync(AppDbContext db, IConfiguration config, CancellationToken ct, Guid? only = null)
    {
        var minAccuracy = config.GetValue("AutoRoute:MinAccuracy", 0.9);
        var minSamples = config.GetValue("AutoRoute:MinSamples", 30);
        var since = DateTimeOffset.UtcNow.AddDays(-90);
        var categories = await db.Categories.AsNoTracking().Where(c => only == null ? c.IsActive : c.Id == only).OrderBy(c => c.SortOrder).ToListAsync(ct);
        var feedback = (await db.AiFeedback.AsNoTracking().Where(f => f.Field == "category" && f.CreatedAt >= since).Select(f => new { f.Suggested, f.Accepted }).ToListAsync(ct)).ToLookup(f => f.Suggested);
        return categories.Select(c =>
        {
            var rows = feedback[c.Key].ToList();
            double? accuracy = rows.Count == 0 ? null : Math.Round(rows.Count(r => r.Accepted) / (double)rows.Count, 3);
            return new ReadinessDto(c.Id, c.Key, c.NameEn, c.AutoRoute, rows.Count, accuracy, minAccuracy, minSamples, rows.Count >= minSamples && (accuracy ?? 0) >= minAccuracy);
        }).ToList();
    }

    private static async Task<IResult> SaveSlaAsync(SlaPolicy p, SaveSla req, AppDbContext db, CancellationToken ct, bool created)
    {
        if (RuleChecks.Sla(req.RespondHours, req.ResolveHours, req.Priority) is { } bad) return Results.BadRequest(new { error = bad });
        if (req.CategoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c, ct)) return Results.BadRequest(new { error = "category_not_found" });
        var active = req.IsActive ?? p.IsActive;
        if (active && await db.SlaPolicies.AnyAsync(x => x.Id != p.Id && x.IsActive && x.CategoryId == req.CategoryId && x.Priority == req.Priority, ct))
            return Results.Conflict(new { error = "duplicate_policy" });

        p.CategoryId = req.CategoryId; p.Priority = req.Priority; p.RespondHours = req.RespondHours; p.ResolveHours = req.ResolveHours;
        p.BusinessHoursOnly = req.BusinessHoursOnly ?? p.BusinessHoursOnly; p.IsActive = active;
        if (created) db.SlaPolicies.Add(p);
        await db.SaveChangesAsync(ct);
        return created ? Results.Created($"/sla-policies/{p.Id}", SlaOf(p)) : Results.Ok(SlaOf(p));
    }

    private static SlaDto SlaOf(SlaPolicy p) => new(p.Id, p.CategoryId, p.Priority, p.RespondHours, p.ResolveHours, p.BusinessHoursOnly, p.IsActive);

    /// <summary>Keeps one row per complaint and field; a later decision replaces the earlier one.</summary>
    private static async Task RecordFeedbackAsync(AppDbContext db, Complaint c, string field, string suggested, string final, Guid actor, CancellationToken ct)
    {
        var row = await db.AiFeedback.SingleOrDefaultAsync(f => f.ComplaintId == c.Id && f.Field == field, ct);
        if (row is null)
        {
            row = new AiFeedback { OrganizationId = c.OrganizationId, ComplaintId = c.Id, Field = field };
            db.AiFeedback.Add(row);
        }
        row.Suggested = suggested; row.FinalValue = final; row.Accepted = suggested == final; row.ActorId = actor;
        row.Language = c.Language; row.Model = c.AiModel; row.PromptVersion = c.AiPromptVersion; row.Confidence = c.AiConfidence;
        row.CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>What the AI cost in a period (default: this month), by task and model, and per complaint (AI-13).</summary>
    private static async Task<IResult> UsageAsync(DateTimeOffset? from, DateTimeOffset? to, AppDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = from ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var end = to ?? now;
        var rows = await db.AiUsage.AsNoTracking().Where(u => u.OccurredAt >= start && u.OccurredAt <= end)
            .Select(u => new { u.Task, u.Model, u.ComplaintId, u.InputTokens, u.OutputTokens, u.CostUsd }).ToListAsync(ct);

        var byTask = rows.GroupBy(r => (r.Task, r.Model)).OrderBy(g => g.Key.Task).ThenBy(g => g.Key.Model)
            .Select(g => new UsageRow(g.Key.Task, g.Key.Model, g.Count(), g.Sum(r => (long)r.InputTokens), g.Sum(r => (long)r.OutputTokens),
                g.All(r => r.CostUsd is not null) ? g.Sum(r => r.CostUsd!.Value) : null)).ToList();
        var priced = rows.All(r => r.CostUsd is not null);
        var cost = priced && rows.Count > 0 ? rows.Sum(r => r.CostUsd!.Value) : (decimal?)null;
        var tokens = rows.Sum(r => (long)r.InputTokens + r.OutputTokens);
        var complaints = rows.Where(r => r.ComplaintId is not null).Select(r => r.ComplaintId).Distinct().Count();
        var settings = await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct);

        return Results.Ok(new UsageReport(
            start, end, await MeteredAiClient.MonthlyTokensAsync(db, ct), OrgPolicy.From(settings ?? "{}").AiMonthlyTokenCap,
            rows.Count, rows.Sum(r => (long)r.InputTokens), rows.Sum(r => (long)r.OutputTokens), cost, priced,
            complaints, cost is { } c && complaints > 0 ? Math.Round(c / complaints, 6) : null,
            complaints > 0 ? tokens / complaints : 0, byTask));
    }

    private static async Task<IResult> AccuracyAsync(string? field, DateTimeOffset? from, DateTimeOffset? to, AppDbContext db, CancellationToken ct)
    {
        var q = db.AiFeedback.AsNoTracking().Where(f => f.Field == (field ?? "category"));
        if (from is { } f) q = q.Where(x => x.CreatedAt >= f);
        if (to is { } t) q = q.Where(x => x.CreatedAt <= t);
        var rows = await q.Select(x => new { x.Language, x.Suggested, x.Accepted }).ToListAsync(ct);
        var result = rows.GroupBy(x => (x.Language, x.Suggested))
            .Select(g => new AccuracyRow(g.Key.Language, g.Key.Suggested, g.Count(), g.Count(x => x.Accepted), Math.Round(g.Count(x => x.Accepted) / (double)g.Count(), 3)))
            .OrderBy(r => r.Suggested).ThenBy(r => r.Language).ToList();
        return Results.Ok(result);
    }

    private static RuleDto RuleOf(RoutingRule r) => new(r.Id, r.Name, r.CategoryId, r.LocationId, r.TeamId, r.SortOrder, r.IsActive);
}
