using System.Text.Json;
using UrbanFix.SharedKernel;

namespace UrbanFix.Complaints;

public static class JobStatus
{
    public const string Open = "Open";
    public const string Accepted = "Accepted";
    public const string InProgress = "InProgress";
    public const string PendingVerification = "PendingVerification";
    public const string Completed = "Completed";
    /// <summary>The technician could not finish; the complaint went back to review (JB-04).</summary>
    public const string Returned = "Returned";
    public const string Cancelled = "Cancelled";

    /// <summary>A job in one of these states blocks a second job on the same complaint.</summary>
    public static readonly string[] Active = [Open, Accepted, InProgress, PendingVerification];

    public static bool IsActive(string status) => Active.Contains(status);
}

public static class EvidenceStage
{
    public const string Before = "before";
    public const string After = "after";
    public const string Progress = "progress";
    public static readonly string[] All = [Before, After, Progress];
}

public static class CostKind
{
    public static readonly string[] All = ["material", "labour", "transport", "other"];
}

public sealed record ChecklistItem(string Label, bool Done);

/// <summary>Pure rules for what a job needs before it can be handed over for verification (JB-05, JC-04).</summary>
public static class JobEvidence
{
    public static IReadOnlyList<ChecklistItem> ParseChecklist(string json) =>
        JsonSerializer.Deserialize<List<ChecklistItem>>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];

    public static string ToJson(IEnumerable<ChecklistItem> items) =>
        JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Codes for what is still missing; empty when the job may be completed.</summary>
    public static IReadOnlyList<string> Missing(
        string rule, bool hasBefore, bool hasAfter, IReadOnlyList<ChecklistItem> checklist, string? note, bool requiresCosts, bool hasCosts)
    {
        var missing = new List<string>();
        switch (rule)
        {
            case "photo":
                if (!hasBefore) missing.Add("before_photo");
                if (!hasAfter) missing.Add("after_photo");
                break;
            case "checklist":
                if (checklist.Any(i => !i.Done)) missing.Add("checklist_incomplete");
                break;
            case "note":
                if (string.IsNullOrWhiteSpace(note)) missing.Add("completion_note");
                break;
        }
        if (requiresCosts && !hasCosts) missing.Add("actual_costs");
        return missing;
    }
}

/// <summary>
/// The job lifecycle (JB-02 to JB-06). Reassigning is not a status change, so it is not listed here.
/// <code>
/// Open -> Accepted -> InProgress -> PendingVerification -> Completed
///                         ^               |  (supervisor returns)
///                         +---------------+
/// Accepted/InProgress -> Returned (cannot complete);  Open/Accepted/InProgress/PendingVerification -> Cancelled
/// </code>
/// </summary>
public static class JobStateMachine
{
    private static readonly (string From, string To)[] Allowed =
    [
        (JobStatus.Open, JobStatus.Accepted),
        (JobStatus.Accepted, JobStatus.InProgress),
        (JobStatus.InProgress, JobStatus.PendingVerification),
        (JobStatus.PendingVerification, JobStatus.Completed),
        (JobStatus.PendingVerification, JobStatus.InProgress),
        (JobStatus.Accepted, JobStatus.Returned),
        (JobStatus.InProgress, JobStatus.Returned),
        (JobStatus.Open, JobStatus.Cancelled),
        (JobStatus.Accepted, JobStatus.Cancelled),
        (JobStatus.InProgress, JobStatus.Cancelled),
        (JobStatus.PendingVerification, JobStatus.Cancelled),
    ];

    public static bool CanMove(string from, string to) => Allowed.Contains((from, to));

    public static IReadOnlyList<(string From, string To)> All => Allowed;
}

/// <summary>Pure pricing arithmetic for estimate and actual-cost lines.</summary>
public static class Pricing
{
    public static decimal Amount(decimal quantity, decimal unitRate) => Math.Round(quantity * unitRate, 2, MidpointRounding.AwayFromZero);

    public static decimal Total(IEnumerable<CostLine> lines) => lines.Sum(l => l.Amount);
}

public sealed record JobAssigned(Guid OrganizationId, Guid JobId, Guid ComplaintId, string Number, Guid? TechnicianId, Guid? TeamId) : DomainEvent(OrganizationId);


public sealed record JobStatusChanged(Guid OrganizationId, Guid JobId, Guid ComplaintId, string Number, string From, string To, Guid? ActorUserId, string? Reason)
    : DomainEvent(OrganizationId);
