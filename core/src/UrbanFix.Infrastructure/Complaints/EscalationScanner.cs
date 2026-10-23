using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>
/// Raises escalations for complaints that have run out of time (RT-07) and clears them when the condition goes away (RT-08).
/// Runs every few minutes. A failure in one organization does not stop the others, but it is reported at once (RT-09)
/// and the run then fails, so the job shows as failed too.
/// </summary>
public sealed class EscalationScanner(IServiceScopeFactory scopes, IConfiguration config, IJobFailureNotifier failures)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var orgs = await OrganizationList.ReadAsync(config, ct);

        var raised = 0;
        var errors = new List<Exception>();
        foreach (var org in orgs)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(org, null);
                raised += await ScanAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IEventBus>(), DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                failures.JobFailed("escalation-scan", $"EscalationScanner (organization {org})", ex.Message);
            }
        }
        if (errors.Count > 0) throw new AggregateException("Escalation scan failed", errors);
        return raised;
    }

    public async Task<int> ScanAsync(AppDbContext db, IEventBus bus, DateTimeOffset now, CancellationToken ct)
    {
        var closedStates = new[] { ComplaintStatus.Closed, ComplaintStatus.Rejected, ComplaintStatus.Merged, ComplaintStatus.Resolved };
        var complaints = await db.Complaints.AsNoTracking().Where(c => !closedStates.Contains(c.Status)).ToListAsync(ct);
        var ids = complaints.Select(c => c.Id).ToList();

        var latestJobs = (await db.Jobs.AsNoTracking().Where(j => ids.Contains(j.ComplaintId)).ToListAsync(ct))
            .GroupBy(j => j.ComplaintId).ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.SequenceNo).First());
        var reopened = (await db.ComplaintHistory.AsNoTracking()
                .Where(h => ids.Contains(h.ComplaintId) && h.ToStatus == ComplaintStatus.UnderReview
                            && (h.FromStatus == ComplaintStatus.Resolved || h.FromStatus == ComplaintStatus.Closed))
                .Select(h => new { h.ComplaintId, h.OccurredAt }).ToListAsync(ct))
            .GroupBy(h => h.ComplaintId).ToDictionary(g => g.Key, g => g.Max(h => h.OccurredAt));

        // Estimates still waiting for a manager, by complaint (RT-11).
        var waiting = (await (from a in db.Approvals.AsNoTracking()
                              join j in db.Jobs.AsNoTracking() on a.JobId equals j.Id
                              where a.Status == ApprovalStatus.Pending && ids.Contains(j.ComplaintId)
                              select new { j.ComplaintId, a.RequestedAt }).ToListAsync(ct))
            .GroupBy(x => x.ComplaintId).ToDictionary(g => g.Key, g => g.Min(x => x.RequestedAt));
        var waitHours = OrgPolicy.From(await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct) ?? "{}").EstimateWaitHours;

        var configured = await db.EscalationLevels.AsNoTracking().ToListAsync(ct);
        var levels = configured.Count > 0 ? configured.Select(l => (l.Level, l.AfterHours, l.Role)).ToList() : EscalationRules.DefaultLevels.ToList();

        var existing = await db.Escalations.ToListAsync(ct);
        var current = new HashSet<(Guid, string)>();
        var raised = 0;

        foreach (var c in complaints)
        {
            latestJobs.TryGetValue(c.Id, out var job);
            var facts = new EscalationRules.Facts(c.Status, c.CreatedAt, c.RespondBy, c.DueAt, job?.Status, job?.CreatedAt, job?.SubmittedAt, reopened.TryGetValue(c.Id, out var reopenedAt) ? reopenedAt : null,
                waiting.TryGetValue(c.Id, out var pendingSince) ? pendingSince : null, waitHours);
            foreach (var condition in EscalationRules.Evaluate(facts, now))
            {
                current.Add((c.Id, condition.Rule));
                foreach (var (level, role) in EscalationRules.DueLevels(levels, condition.Since, now))
                {
                    var row = existing.FirstOrDefault(e => e.ComplaintId == c.Id && e.Rule == condition.Rule && e.Level == level);
                    if (row is { ResolvedAt: null }) continue;
                    // A condition that came back after being cleared fires again; one that never cleared does not.
                    if (row is not null && condition.Since <= row.RaisedAt) continue;
                    if (row is null)
                    {
                        row = new Escalation { OrganizationId = c.OrganizationId, ComplaintId = c.Id, Rule = condition.Rule, Level = level };
                        db.Escalations.Add(row);
                        existing.Add(row);
                    }
                    row.JobId = job?.Id; row.Since = condition.Since; row.RaisedAt = now; row.ResolvedAt = null; row.Resolution = null;
                    bus.Publish(new EscalationRaised(c.OrganizationId, c.Id, c.Reference, condition.Rule, level, role, job?.Id, job?.Number));
                    raised++;
                }
            }
        }

        // Resolve on action: an open escalation whose condition no longer holds is closed with the reason.
        var stillOpen = complaints.Select(c => c.Id).ToHashSet();
        foreach (var e in existing.Where(e => e.ResolvedAt is null && !current.Contains((e.ComplaintId, e.Rule))))
        {
            e.ResolvedAt = now;
            e.Resolution = stillOpen.Contains(e.ComplaintId) ? "condition_cleared" : "complaint_closed";
        }

        await db.SaveChangesAsync(ct);
        return raised;
    }
}
