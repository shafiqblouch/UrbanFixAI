using UrbanFix.SharedKernel;

namespace UrbanFix.Complaints;

public static class ApprovalKinds
{
    public const string Estimate = "estimate";
}

public static class ApprovalStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// A request for someone with authority to say yes or no (ID-10). Estimates use it now; reversals and waivers will, without
/// changing this. Money-related decisions are never edited afterwards: each person's answer is its own row.
/// </summary>
public class Approval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Kind { get; set; } = "";
    /// <summary>What is being approved (an estimate id).</summary>
    public Guid SubjectId { get; set; }
    public Guid? JobId { get; set; }
    public decimal Amount { get; set; }
    public Guid RequestedBy { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>How many different approvers must say yes: two for large amounts.</summary>
    public int ApprovalsNeeded { get; set; } = 1;
    public string Status { get; set; } = ApprovalStatus.Pending;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
}

public class ApprovalDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ApprovalId { get; set; }
    public Guid UserId { get; set; }
    public bool Approve { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset DecidedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record ApprovalRequested(Guid OrganizationId, Guid ApprovalId, Guid JobId, string JobNumber, decimal Amount) : DomainEvent(OrganizationId);

public sealed record ApprovalDecided(Guid OrganizationId, Guid ApprovalId, Guid JobId, string JobNumber, bool Approved, string? Note, Guid RequestedBy) : DomainEvent(OrganizationId);

public sealed record CostOverrun(Guid OrganizationId, Guid JobId, string JobNumber, string Reference, decimal Estimated, decimal Actual, double Percent) : DomainEvent(OrganizationId);

/// <summary>The rules of approval as pure functions (ID-10, JC-03).</summary>
public static class ApprovalRules
{
    /// <summary>Does an amount need approval, and from how many people? A limit of 0 means the society has not turned approval on.</summary>
    public static (bool Needed, int Approvers) Requirement(decimal amount, decimal limit, decimal secondApproverAbove)
    {
        if (limit <= 0 || amount <= limit) return (false, 0);
        return (true, secondApproverAbove > 0 && amount > secondApproverAbove ? 2 : 1);
    }

    /// <summary>Only a manager or committee member approves; a supervisor, who proposes the work, does not.</summary>
    public static bool IsApproverRole(Role role) => role is Role.Manager or Role.Committee;

    /// <returns>Null when the person may answer, otherwise why not.</returns>
    public static string? CanDecide(Approval a, Guid userId, IEnumerable<Role> roles, IReadOnlyCollection<ApprovalDecision> earlier, DateTimeOffset now)
    {
        if (a.Status != ApprovalStatus.Pending) return "not_pending";
        if (a.ExpiresAt <= now) return "expired";
        if (a.RequestedBy == userId) return "requester_cannot_decide";
        if (!roles.Any(IsApproverRole)) return "not_an_approver";
        if (earlier.Any(d => d.UserId == userId)) return "already_decided";
        return null;
    }

    /// <summary>The state after a new answer. Any "no" ends it; "yes" counts until enough different people have said it.</summary>
    public static string Outcome(Approval a, IReadOnlyCollection<ApprovalDecision> all) =>
        all.Any(d => !d.Approve) ? ApprovalStatus.Rejected
        : all.Count(d => d.Approve) >= a.ApprovalsNeeded ? ApprovalStatus.Approved
        : ApprovalStatus.Pending;

    /// <summary>Work may start when the estimate needs no approval or has it (JC-03).</summary>
    public static bool AllowsWork(string estimateApproval) => estimateApproval is EstimateApproval.NotRequired or EstimateApproval.Approved;

    /// <summary>By how much, in percent, the actual cost is over the estimate; null when there is no estimate to compare with (JC-05).</summary>
    public static double? OverrunPercent(decimal? estimated, decimal actual) =>
        estimated is > 0 ? Math.Round((double)((actual - estimated.Value) / estimated.Value * 100), 1) : null;

    public static bool IsOverrun(decimal? estimated, decimal actual, int thresholdPercent) =>
        OverrunPercent(estimated, actual) is { } p && p > thresholdPercent;
}

public static class EstimateApproval
{
    public const string NotRequired = "NotRequired";
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string Expired = "Expired";
}
