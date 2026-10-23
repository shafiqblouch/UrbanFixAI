using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>
/// Applies the Phase 1 rules to a complaint once its category is known: priority (RT-04), safety handling (RT-05),
/// SLA deadlines (RT-06) and, where the category allows it and the AI is sure enough, automatic assignment (RT-02).
/// Callers save changes afterwards; nothing here commits.
/// </summary>
public sealed class ComplaintPlanner(AppDbContext db, NumberGenerator numbers, IEventBus bus)
{
    public sealed record Outcome(string Priority, bool SafetyFlag, bool AutoRouted, string? QueuedBecause);

    /// <param name="category">The category now on the complaint, if any.</param>
    /// <param name="categoryChosenByPerson">A person picked the category, so the AI must not route on its own.</param>
    /// <param name="aiConfidence">Confidence of the AI's category suggestion, when it made one.</param>
    public async Task<Outcome> ApplyAsync(Complaint c, Category? category, bool categoryChosenByPerson, float? aiConfidence, CancellationToken ct)
    {
        await ApplyIntakeAsync(c, category, ct);

        var teamId = await RoutingLookup.SuggestTeamAsync(db, c.CategoryId, c.LocationId, ct);
        // The platform team can switch automatic routing off for a society without a deployment (FD-13).
        var autoRoutingOn = await Platform.FeatureFlags.IsEnabledAsync(db, Platform.Features.AutoRouting, ct);
        var why = AutoRoutePolicy.WhyQueued(
            autoRoutingOn && (category?.AutoRoute ?? false), category?.AutoRouteThreshold ?? AutoRoutePolicy.DefaultThreshold, aiConfidence,
            c.Priority, c.SafetyFlag, categoryChosenByPerson, teamId is not null);
        if (why is null && c.Status == ComplaintStatus.New && teamId is { } team)
        {
            await AssignAsync(c, category, team, ct);
            return new Outcome(c.Priority, c.SafetyFlag, true, null);
        }
        return new Outcome(c.Priority, c.SafetyFlag, false, why ?? "not_new");
    }

    /// <summary>The part that needs no AI: priority, safety flag and deadlines. Runs when a complaint is stored, so it works with the AI down (AI-14).</summary>
    public async Task ApplyIntakeAsync(Complaint c, Category? category, CancellationToken ct)
    {
        // Priority: the category default and the risk of the place can only raise what is already set, never lower it.
        var risk = c.LocationId is { } lid ? await db.Locations.AsNoTracking().Where(l => l.Id == lid).Select(l => l.RiskLevel).SingleOrDefaultAsync(ct) : 0;
        var safety = c.SafetyFlag || SafetyTerms.IsSafety(c.Description) || SafetyTerms.IsSafety(c.AiSummary);
        var computed = PriorityEngine.Compute(category?.DefaultPriority ?? c.Priority, risk, safety);
        if (PriorityLevels.Rank(computed.Priority) > PriorityLevels.Rank(c.Priority)) c.Priority = computed.Priority;

        if (safety && !c.SafetyFlag)
        {
            c.SafetyFlag = true;
            bus.Publish(new ComplaintSafetyFlagged(c.OrganizationId, c.Id, c.Reference));
        }

        await SetDeadlinesAsync(c, ct);
    }

    /// <summary>
    /// A later finding (a hazard in a photo, danger words in a voice note) turns an existing complaint into a safety matter:
    /// top priority, new deadlines, and the alert to staff and the prompt to the resident (RT-05, AI-10).
    /// </summary>
    public async Task FlagSafetyAsync(Complaint c, CancellationToken ct)
    {
        if (c.SafetyFlag) return;
        c.SafetyFlag = true;
        c.Priority = PriorityLevels.Safety;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await SetDeadlinesAsync(c, ct);
        bus.Publish(new ComplaintSafetyFlagged(c.OrganizationId, c.Id, c.Reference));
    }

    /// <summary>Respond-by and resolve-by times from the most specific active policy, in the organization's timezone (RT-06).</summary>
    public async Task SetDeadlinesAsync(Complaint c, CancellationToken ct)
    {
        var policies = await db.SlaPolicies.AsNoTracking().Where(p => p.IsActive && p.Priority == c.Priority && (p.CategoryId == null || p.CategoryId == c.CategoryId)).ToListAsync(ct);
        var policy = policies.FirstOrDefault(p => p.CategoryId == c.CategoryId && c.CategoryId != null) ?? policies.FirstOrDefault();
        var rule = policy is null
            ? SlaCalculator.Default(c.Priority)
            : new SlaCalculator.Policy(policy.RespondHours, policy.ResolveHours, policy.BusinessHoursOnly);
        var zone = SlaCalculator.ZoneOrUtc(await db.OrgSettings.AsNoTracking().Select(s => s.Timezone).SingleOrDefaultAsync(ct) ?? "UTC");
        c.RespondBy = SlaCalculator.Deadline(c.CreatedAt, rule.RespondHours, rule.BusinessHoursOnly, zone, rule.WorkStartHour, rule.WorkEndHour);
        c.DueAt = SlaCalculator.Deadline(c.CreatedAt, rule.ResolveHours, rule.BusinessHoursOnly, zone, rule.WorkStartHour, rule.WorkEndHour);
    }

    /// <summary>Review and assignment in one step, done by the system, so the history shows both moves with no person behind them.</summary>
    private async Task AssignAsync(Complaint c, Category? category, Guid teamId, CancellationToken ct)
    {
        var sequence = (await db.Jobs.Where(j => j.ComplaintId == c.Id).MaxAsync(j => (int?)j.SequenceNo, ct) ?? 0) + 1;
        var checklist = category is null ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(category.Checklist) ?? [];
        var job = new Job
        {
            OrganizationId = c.OrganizationId, Number = await numbers.NextAsync(NumberGenerator.JobScope, "J", ct), ComplaintId = c.Id,
            SequenceNo = sequence, AssignedTeamId = teamId,
            EvidenceRule = category?.EvidenceRule ?? "photo", RequiresCosts = category?.RequiresCosts ?? false,
            Checklist = JobEvidence.ToJson(checklist.Select(label => new ChecklistItem(label, false))),
        };
        db.Jobs.Add(job);
        db.JobUpdates.Add(new JobUpdate { OrganizationId = c.OrganizationId, JobId = job.Id, Kind = "created", ToStatus = JobStatus.Open, Note = "Assigned automatically" });

        Move(c, ComplaintStatus.UnderReview, "Routed automatically");
        Move(c, ComplaintStatus.Assigned, $"Job {job.Number} created automatically");
        c.AutoRouted = true;
        bus.Publish(new JobCreated(c.OrganizationId, job.Id, c.Id, job.Number));
    }

    private void Move(Complaint c, ComplaintStatus to, string reason)
    {
        var from = c.Status;
        c.Status = to;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        db.ComplaintHistory.Add(new ComplaintStatusHistory { OrganizationId = c.OrganizationId, ComplaintId = c.Id, FromStatus = from, ToStatus = to, Reason = reason });
        bus.Publish(new ComplaintStatusChanged(c.OrganizationId, c.Id, c.Reference, from, to, null, reason));
    }
}

public static class RoutingLookup
{
    /// <summary>The team the active routing rules point to for this category and location, or null when none matches (RT-01).</summary>
    public static async Task<Guid?> SuggestTeamAsync(AppDbContext db, Guid? categoryId, Guid? locationId, CancellationToken ct)
    {
        var rules = await (from r in db.RoutingRules.AsNoTracking()
                           join t in db.Teams.AsNoTracking() on r.TeamId equals t.Id
                           join l in db.Locations.AsNoTracking() on r.LocationId equals l.Id into scope
                           from l in scope.DefaultIfEmpty()
                           where r.IsActive && t.IsActive
                           select new ScopedRule(r.Id, r.CategoryId, l == null ? null : l.Path, l == null ? 0 : l.Depth, r.TeamId, r.SortOrder)).ToListAsync(ct);
        if (rules.Count == 0) return null;
        var path = locationId is { } id ? await db.Locations.AsNoTracking().Where(l => l.Id == id).Select(l => l.Path).SingleOrDefaultAsync(ct) : null;
        return RoutingEngine.Resolve(rules, categoryId, path)?.TeamId;
    }
}
