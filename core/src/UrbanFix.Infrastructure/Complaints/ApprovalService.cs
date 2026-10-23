using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>
/// The approval workflow (ID-10) as used for estimates (JC-03). A request is made when an estimate is above the society's limit;
/// managers and committee members answer it; a very large one needs two different people; an unanswered one expires.
/// Nothing here saves: the caller does, so the request, the estimate and the job timeline are stored together.
/// </summary>
public sealed class ApprovalService(AppDbContext db, IEventBus bus)
{
    private async Task<OrgPolicy> PolicyAsync(CancellationToken ct) =>
        OrgPolicy.From(await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct) ?? "{}");

    /// <summary>Marks the estimate as needing no approval, or opens a request for one.</summary>
    public async Task RequestForEstimateAsync(Estimate estimate, Job job, Guid requester, CancellationToken ct)
    {
        var policy = await PolicyAsync(ct);
        var (needed, approvers) = ApprovalRules.Requirement(estimate.Total, policy.EstimateApprovalLimit, policy.SecondApproverAbove);
        if (!needed) { estimate.ApprovalStatus = EstimateApproval.NotRequired; return; }

        var approval = new Approval
        {
            OrganizationId = estimate.OrganizationId, Kind = ApprovalKinds.Estimate, SubjectId = estimate.Id, JobId = job.Id, Amount = estimate.Total,
            RequestedBy = requester, ApprovalsNeeded = approvers, ExpiresAt = DateTimeOffset.UtcNow.AddHours(policy.ApprovalExpiryHours),
        };
        db.Approvals.Add(approval);
        estimate.ApprovalId = approval.Id;
        estimate.ApprovalStatus = EstimateApproval.Pending;
        db.JobUpdates.Add(new JobUpdate { OrganizationId = estimate.OrganizationId, JobId = job.Id, AuthorId = requester, Kind = "approval", Note = $"Estimate v{estimate.Version} (Rs {estimate.Total:0.00}) sent for approval" });
        bus.Publish(new ApprovalRequested(estimate.OrganizationId, approval.Id, job.Id, job.Number, estimate.Total));
    }

    /// <summary>A revised estimate replaces the one still waiting, so the older request is withdrawn.</summary>
    public async Task CancelPendingForJobAsync(Guid jobId, CancellationToken ct)
    {
        foreach (var a in await db.Approvals.Where(a => a.JobId == jobId && a.Status == ApprovalStatus.Pending).ToListAsync(ct))
        {
            a.Status = ApprovalStatus.Cancelled; a.DecidedAt = DateTimeOffset.UtcNow; a.Note = "Replaced by a revised estimate";
        }
    }

    /// <returns>The approval after the answer, or an error code (see <see cref="ApprovalRules.CanDecide"/>; also reason_required, not_found).</returns>
    public async Task<(Approval? Approval, string? Error)> DecideAsync(Guid approvalId, Guid userId, IEnumerable<Role> roles, bool approve, string? note, CancellationToken ct)
    {
        var approval = await db.Approvals.SingleOrDefaultAsync(a => a.Id == approvalId, ct);
        if (approval is null) return (null, "not_found");
        if (!approve && string.IsNullOrWhiteSpace(note)) return (null, "reason_required");

        var earlier = await db.ApprovalDecisions.AsNoTracking().Where(d => d.ApprovalId == approvalId).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        if (ApprovalRules.CanDecide(approval, userId, roles, earlier, now) is { } error) return (null, error);

        var decision = new ApprovalDecision { OrganizationId = approval.OrganizationId, ApprovalId = approvalId, UserId = userId, Approve = approve, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() };
        db.ApprovalDecisions.Add(decision);
        var outcome = ApprovalRules.Outcome(approval, [.. earlier, decision]);
        if (outcome == ApprovalStatus.Pending) return (approval, null); // waiting for the second person

        approval.Status = outcome; approval.DecidedAt = now; approval.Note = decision.Note;
        await FinishAsync(approval, outcome == ApprovalStatus.Approved, outcome == ApprovalStatus.Approved ? EstimateApproval.Approved : EstimateApproval.Rejected, decision.Note, userId, ct);
        return (approval, null);
    }

    private async Task FinishAsync(Approval approval, bool approved, string estimateStatus, string? note, Guid? actor, CancellationToken ct)
    {
        var estimate = await db.Estimates.SingleOrDefaultAsync(e => e.Id == approval.SubjectId, ct);
        if (estimate is not null) estimate.ApprovalStatus = estimateStatus;
        var job = approval.JobId is { } jid ? await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jid, ct) : null;
        if (job is not null)
        {
            var text = approved ? "Estimate approved" : estimateStatus == EstimateApproval.Expired ? "Estimate approval expired with no answer" : "Estimate not approved";
            db.JobUpdates.Add(new JobUpdate { OrganizationId = approval.OrganizationId, JobId = job.Id, AuthorId = actor, Kind = "approval", Note = note is null ? text : $"{text}: {note}" });
            bus.Publish(new ApprovalDecided(approval.OrganizationId, approval.Id, job.Id, job.Number, approved, note, approval.RequestedBy));
        }
    }

    /// <summary>Requests nobody answered in time are closed, and the estimate goes back for revision.</summary>
    public async Task<int> ExpireAsync(DateTimeOffset now, CancellationToken ct)
    {
        var overdue = await db.Approvals.Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt <= now).ToListAsync(ct);
        foreach (var a in overdue)
        {
            a.Status = ApprovalStatus.Expired; a.DecidedAt = now; a.Note = "No answer in time";
            await FinishAsync(a, false, EstimateApproval.Expired, "No answer in time", null, ct);
        }
        if (overdue.Count > 0) await db.SaveChangesAsync(ct);
        return overdue.Count;
    }
}

/// <summary>Closes approval requests that nobody answered (ID-10). Hourly.</summary>
public sealed class ApprovalSweeper(IServiceScopeFactory scopes, IConfiguration config, IJobFailureNotifier failures)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var closed = 0;
        var errors = new List<Exception>();
        foreach (var org in await OrganizationList.ReadAsync(config, ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(org, null);
                closed += await scope.ServiceProvider.GetRequiredService<ApprovalService>().ExpireAsync(DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                failures.JobFailed("approval-sweep", $"ApprovalSweeper (organization {org})", ex.Message);
            }
        }
        if (errors.Count > 0) throw new AggregateException("Approval sweep failed", errors);
        return closed;
    }
}
