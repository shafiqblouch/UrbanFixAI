using UrbanFix.SharedKernel;

namespace UrbanFix.Complaints;

/// <summary>One raised escalation: a rule that fired for a complaint at a level (RT-07). Open until the condition clears (RT-08).</summary>
public class Escalation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ComplaintId { get; set; }
    public Guid? JobId { get; set; }
    public string Rule { get; set; } = "";
    public int Level { get; set; }
    /// <summary>The moment the condition began, e.g. the deadline that passed.</summary>
    public DateTimeOffset Since { get; set; }
    public DateTimeOffset RaisedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Resolution { get; set; }
}

/// <summary>Who is told at each level and how long after the condition began (RT-08).</summary>
public class EscalationLevel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public int Level { get; set; }
    public int AfterHours { get; set; }
    public Role Role { get; set; }
}

public sealed record EscalationRaised(
    Guid OrganizationId, Guid ComplaintId, string Reference, string Rule, int Level, Role NotifyRole, Guid? JobId, string? JobNumber)
    : DomainEvent(OrganizationId);

public static class EscalationRuleNames
{
    public const string NotAssigned = "not_assigned";
    public const string NotAccepted = "not_accepted";
    public const string TargetPassed = "target_passed";
    public const string AwaitingSignOff = "awaiting_signoff";
    public const string Dispute = "resident_dispute";
    /// <summary>An estimate has waited too long for a manager (RT-11).</summary>
    public const string EstimateWaiting = "estimate_waiting";
    public static readonly string[] All = [NotAssigned, NotAccepted, TargetPassed, AwaitingSignOff, Dispute, EstimateWaiting];
}

/// <summary>
/// Which escalation conditions hold for a complaint right now (RT-07). Pure, so each rule is tested without a database.
/// A condition has a start time; a level fires once its delay has passed since then.
/// </summary>
public static class EscalationRules
{
    public sealed record Facts(
        ComplaintStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? RespondBy, DateTimeOffset? DueAt,
        string? JobStatus, DateTimeOffset? JobCreatedAt, DateTimeOffset? JobSubmittedAt, DateTimeOffset? ReopenedAt,
        DateTimeOffset? EstimatePendingSince = null, int EstimateWaitHours = 24);

    public sealed record Condition(string Rule, DateTimeOffset Since);

    public static readonly TimeSpan SignOffWindow = TimeSpan.FromHours(24);

    public static readonly IReadOnlyList<(int Level, int AfterHours, Role Role)> DefaultLevels = [(1, 0, Role.Supervisor), (2, 12, Role.Manager)];

    public static IReadOnlyList<Condition> Evaluate(Facts f, DateTimeOffset now)
    {
        var found = new List<Condition>();
        if (f.Status is ComplaintStatus.Closed or ComplaintStatus.Rejected or ComplaintStatus.Merged or ComplaintStatus.Resolved) return found;

        var jobActive = f.JobStatus is not null && JobStatus.IsActive(f.JobStatus);

        // A resident who reopened a complaint is waiting for a person; it clears when a new job is raised.
        if (f.Status == ComplaintStatus.UnderReview && f.ReopenedAt is { } reopened && !jobActive)
        {
            found.Add(new Condition(EscalationRuleNames.Dispute, reopened));
            return found;
        }

        if (f.Status is ComplaintStatus.New or ComplaintStatus.UnderReview && f.RespondBy is { } respondBy && respondBy < now)
            found.Add(new Condition(EscalationRuleNames.NotAssigned, respondBy));

        if (f.JobStatus == JobStatus.Open && f.JobCreatedAt is { } created)
        {
            // A quarter of the resolve time, at least an hour, to pick the job up.
            var total = (f.DueAt ?? f.CreatedAt.AddDays(7)) - f.CreatedAt;
            var window = TimeSpan.FromTicks(Math.Max(total.Ticks / 4, TimeSpan.TicksPerHour));
            if (created + window < now) found.Add(new Condition(EscalationRuleNames.NotAccepted, created + window));
        }

        if ((jobActive || f.Status is ComplaintStatus.Assigned or ComplaintStatus.InProgress) && f.DueAt is { } due && due < now)
            found.Add(new Condition(EscalationRuleNames.TargetPassed, due));

        if (f.EstimatePendingSince is { } pending && pending.AddHours(f.EstimateWaitHours) < now)
            found.Add(new Condition(EscalationRuleNames.EstimateWaiting, pending.AddHours(f.EstimateWaitHours)));

        if (f.JobStatus == JobStatus.PendingVerification && f.JobSubmittedAt is { } submitted && submitted + SignOffWindow < now)
            found.Add(new Condition(EscalationRuleNames.AwaitingSignOff, submitted + SignOffWindow));

        return found;
    }

    /// <summary>The levels whose delay has passed for a condition that began at <paramref name="since"/>.</summary>
    public static IEnumerable<(int Level, Role Role)> DueLevels(IEnumerable<(int Level, int AfterHours, Role Role)> levels, DateTimeOffset since, DateTimeOffset now) =>
        levels.Where(l => since.AddHours(l.AfterHours) <= now).OrderBy(l => l.Level).Select(l => (l.Level, l.Role));

    public static string? ValidateLevels(IReadOnlyList<(int Level, int AfterHours, Role Role)> levels)
    {
        if (levels.Count == 0 || levels.Count > 5) return "one_to_five_levels";
        if (levels.Select(l => l.Level).Distinct().Count() != levels.Count) return "duplicate_level";
        if (levels.Any(l => l.AfterHours < 0 || l.AfterHours > 24 * 30)) return "invalid_delay";
        if (levels.Any(l => l.Role is Role.Resident or Role.Guard or Role.PlatformAdmin)) return "role_cannot_receive_escalations";
        var ordered = levels.OrderBy(l => l.Level).ToList();
        if (ordered[0].Level != 1) return "levels_must_start_at_1";
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Level != ordered[i - 1].Level + 1) return "levels_must_be_consecutive";
            if (ordered[i].AfterHours < ordered[i - 1].AfterHours) return "later_levels_must_not_come_sooner";
        }
        return null;
    }
}
