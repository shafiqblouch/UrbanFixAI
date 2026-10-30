using UrbanFix.Identity;
using UrbanFix.SharedKernel;

namespace UrbanFix.Complaints;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ComplaintStatus { New, OnHold, UnderReview, Assigned, InProgress, Resolved, Closed, Rejected, Merged }

public static class Channels
{
    public static readonly string[] All = ["app", "web", "whatsapp", "phone", "walk_in"];
    /// <summary>Channels where staff type the complaint in for a resident.</summary>
    public static bool IsStaffEntry(string channel) => channel is "phone" or "walk_in";
}

public class Complaint : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Reference { get; set; } = "";
    public ComplaintStatus Status { get; set; } = ComplaintStatus.New;
    public string Channel { get; set; } = "app";
    public string Description { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public Guid? HouseId { get; set; }
    public Guid? LocationId { get; set; }
    public string? LocationText { get; set; }
    /// <summary>Where the reporter was when they sent it (app GPS fix or WhatsApp pin); drawn on the portal map.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public Guid? ReportedByUserId { get; set; }
    public Guid? EnteredByUserId { get; set; }
    public string Priority { get; set; } = "Normal";
    public string? Language { get; set; }
    public string AiStatus { get; set; } = "pending";
    public string? AiCategoryKey { get; set; }
    public string? AiSummary { get; set; }
    public float? AiConfidence { get; set; }
    public string? AiModel { get; set; }
    public string? AiPromptVersion { get; set; }
    public string? HoldReason { get; set; }
    /// <summary>The latest reject / reopen / cannot-complete reason; shown to the resident.</summary>
    public string? StatusReason { get; set; }
    /// <summary>Life-safety terms were found in the report (RT-04); such a complaint always needs a person.</summary>
    public bool SafetyFlag { get; set; }
    /// <summary>When the complaint must be resolved by, from the SLA policy (RT-06).</summary>
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? RespondBy { get; set; }
    /// <summary>The system assigned it without a supervisor (RT-02).</summary>
    public bool AutoRouted { get; set; }
    /// <summary>The number a phone or WhatsApp complaint came from (CP-08).</summary>
    public string? ReporterPhone { get; set; }
    /// <summary>False when the sender is not in the directory and has not been confirmed by staff.</summary>
    public bool ReporterVerified { get; set; } = true;
    /// <summary>Free-text pole or asset number (CP-11).</summary>
    public string? AssetRef { get; set; }
    public DateTimeOffset? ConfirmationRemindedAt { get; set; }
    /// <summary>Closed by the auto-confirm job rather than by the resident (CP-10).</summary>
    public bool AutoConfirmed { get; set; }
    /// <summary>The incident this complaint has been grouped into, if any (DP-01).</summary>
    public Guid? IncidentId { get; set; }
    public Guid? MergedIntoId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public class ComplaintStatusHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ComplaintId { get; set; }
    public ComplaintStatus? FromStatus { get; set; }
    public ComplaintStatus ToStatus { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ComplaintMedia
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ComplaintId { get; set; }
    public string StorageKey { get; set; } = "";
    public string? ThumbKey { get; set; }
    public string Kind { get; set; } = "image";
    public string MimeType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string[] QualityFlags { get; set; } = [];
    /// <summary>Voice notes only (AI-09): what was said, how sure the model is, and where it stands.</summary>
    public string? Transcript { get; set; }
    public float? TranscriptConfidence { get; set; }
    public string? TranscriptLanguage { get; set; }
    /// <summary>null = not asked for, "pending", "done", "failed".</summary>
    public string? TranscriptStatus { get; set; }
    /// <summary>Photos only (AI-08): fixed visible-issue labels and a cautious description. Advisory.</summary>
    public string? AiStatus { get; set; }
    public string[] AiLabels { get; set; } = [];
    public string? AiDescription { get; set; }
    public bool? AiNeedsMoreInfo { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Set when the photo was taken as job evidence (JB-05); null for the resident's own photos.</summary>
    public Guid? JobId { get; set; }
    public string? Stage { get; set; }
    public Guid? UploadedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Job : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Number { get; set; } = "";
    public Guid ComplaintId { get; set; }
    public int SequenceNo { get; set; }
    public string Status { get; set; } = "Open";
    public string? Instructions { get; set; }
    public Guid? AssignedTeamId { get; set; }
    public Guid? AssignedUserId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletionNote { get; set; }
    /// <summary>Set once, when the job first ran past its time limit and supervisors were alerted.</summary>
    public DateTimeOffset? EscalatedAt { get; set; }
    /// <summary>Set when actual cost first went over the estimate by more than the society's threshold (JC-05).</summary>
    public DateTimeOffset? OverrunFlaggedAt { get; set; }
    /// <summary>The cost is to be charged to a house (JB-10). Only supervisors set it; the change is audited.</summary>
    public bool Chargeable { get; set; }
    public Guid? ChargeHouseId { get; set; }
    public string EvidenceRule { get; set; } = "photo";
    public bool RequiresCosts { get; set; }
    /// <summary>JSON array of <c>{label, done}</c>, copied from the category when the job is created.</summary>
    public string Checklist { get; set; } = "[]";
}

/// <summary>What the resident said about finished work (JB-08).</summary>
public class ComplaintFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ComplaintId { get; set; }
    public Guid UserId { get; set; }
    public int Rating { get; set; }
    public bool ConfirmedFixed { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One AI call and what it used (AI-13). Append-only.</summary>
public class AiUsageEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ComplaintId { get; set; }
    public string Task { get; set; } = "";
    public string Model { get; set; } = "";
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The AI's advisory look at a job's before and after photos (AI-12). A person still decides.</summary>
public class JobComparison
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid JobId { get; set; }
    public Guid BeforeMediaId { get; set; }
    public Guid AfterMediaId { get; set; }
    public float SameLocationLikelihood { get; set; }
    public string ChangeNote { get; set; } = "";
    public bool NeedsMoreInfo { get; set; }
    public float Confidence { get; set; }
    public bool Degraded { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public Guid? RequestedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class JobUpdate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid JobId { get; set; }
    public Guid? AuthorId { get; set; }
    public string Kind { get; set; } = "";
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Note { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Estimate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid JobId { get; set; }
    public int Version { get; set; }
    public string Status { get; set; } = "Current";
    public string? Note { get; set; }
    public decimal Total { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>NotRequired, Pending, Approved, Rejected or Expired. Work on the job cannot start while it is Pending, Rejected or Expired (JC-03).</summary>
    public string ApprovalStatus { get; set; } = EstimateApproval.NotRequired;
    public Guid? ApprovalId { get; set; }
}

/// <summary>One priced line, shared by estimates and actual costs.</summary>
public abstract class CostLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Kind { get; set; } = "material";
    public string Description { get; set; } = "";
    public Guid? PriceItemId { get; set; }
    public Guid? RateId { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "";
    public decimal UnitRate { get; set; }
    public decimal Amount { get; set; }
}

public class EstimateLine : CostLine
{
    public Guid EstimateId { get; set; }
}

public class JobCost : CostLine
{
    public Guid JobId { get; set; }
    public Guid? RecordedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class NumberSequence
{
    public Guid OrganizationId { get; set; }
    public string Scope { get; set; } = "";
    public int Year { get; set; }
    public int LastValue { get; set; }
}

public class UploadSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public string MimeType { get; set; } = "";
    public long TotalBytes { get; set; }
    public long ReceivedBytes { get; set; }
    public string[] ChunkKeys { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed record ComplaintCreated(Guid OrganizationId, Guid ComplaintId, string Reference) : DomainEvent(OrganizationId);

public sealed record ComplaintStatusChanged(
    Guid OrganizationId, Guid ComplaintId, string Reference, ComplaintStatus From, ComplaintStatus To, Guid? ActorUserId, string? Reason)
    : DomainEvent(OrganizationId);

public sealed record JobCreated(Guid OrganizationId, Guid JobId, Guid ComplaintId, string Number) : DomainEvent(OrganizationId);

/// <summary>
/// The complaint lifecycle (CP-02, CP-07). Pure rules, no I/O, so every transition is unit-testable.
/// A transition names the permission needed; where a resident may act on their own complaint
/// (confirm closure, reopen) <c>OwnerMay</c> lets the reporter do it without staff permissions.
/// </summary>
public static class ComplaintStateMachine
{
    public sealed record Rule(ComplaintStatus From, ComplaintStatus To, string Permission, bool ReasonRequired = false, bool OwnerMay = false);

    private static readonly Rule[] Rules =
    [
        new(ComplaintStatus.New, ComplaintStatus.UnderReview, Permissions.ComplaintTriage),
        new(ComplaintStatus.New, ComplaintStatus.OnHold, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.OnHold, ComplaintStatus.New, Permissions.ComplaintTriage),
        new(ComplaintStatus.OnHold, ComplaintStatus.UnderReview, Permissions.ComplaintTriage),
        new(ComplaintStatus.UnderReview, ComplaintStatus.Assigned, Permissions.ComplaintAssign),
        new(ComplaintStatus.Assigned, ComplaintStatus.UnderReview, Permissions.ComplaintAssign, ReasonRequired: true),
        new(ComplaintStatus.Assigned, ComplaintStatus.InProgress, Permissions.ComplaintWork),
        new(ComplaintStatus.InProgress, ComplaintStatus.Resolved, Permissions.ComplaintWork),
        // "Cannot complete": the technician hands it back to the supervisor with a reason.
        new(ComplaintStatus.InProgress, ComplaintStatus.UnderReview, Permissions.ComplaintWork, ReasonRequired: true),
        new(ComplaintStatus.Resolved, ComplaintStatus.Closed, Permissions.ComplaintClose, OwnerMay: true),
        // Verification failed: back to review so the supervisor can raise a rework job.
        new(ComplaintStatus.Resolved, ComplaintStatus.UnderReview, Permissions.ComplaintClose, ReasonRequired: true, OwnerMay: true),
        new(ComplaintStatus.New, ComplaintStatus.Rejected, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.OnHold, ComplaintStatus.Rejected, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.UnderReview, ComplaintStatus.Rejected, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.Assigned, ComplaintStatus.Rejected, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.Closed, ComplaintStatus.UnderReview, Permissions.ComplaintTriage, ReasonRequired: true, OwnerMay: true),
        new(ComplaintStatus.Rejected, ComplaintStatus.UnderReview, Permissions.ComplaintTriage, ReasonRequired: true, OwnerMay: true),
        new(ComplaintStatus.New, ComplaintStatus.Merged, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.OnHold, ComplaintStatus.Merged, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.UnderReview, ComplaintStatus.Merged, Permissions.ComplaintTriage, ReasonRequired: true),
        new(ComplaintStatus.Assigned, ComplaintStatus.Merged, Permissions.ComplaintTriage, ReasonRequired: true),
    ];

    public static IReadOnlyList<Rule> AllRules => Rules;

    public static Rule? Find(ComplaintStatus from, ComplaintStatus to) => Rules.FirstOrDefault(r => r.From == from && r.To == to);

    public static IEnumerable<ComplaintStatus> NextStatuses(ComplaintStatus from) => Rules.Where(r => r.From == from).Select(r => r.To);

    /// <summary>Null when allowed, otherwise a machine-readable reason code.</summary>
    public static string? Check(ComplaintStatus from, ComplaintStatus to, IReadOnlySet<string> actorPermissions, bool actorIsReporter, string? reason)
    {
        var rule = Find(from, to);
        if (rule is null) return "illegal_transition";
        var allowed = actorPermissions.Contains(rule.Permission)
            || (rule.OwnerMay && actorIsReporter && actorPermissions.Contains(Permissions.ComplaintReadOwn));
        if (!allowed) return "forbidden";
        if (rule.ReasonRequired && string.IsNullOrWhiteSpace(reason)) return "reason_required";
        return null;
    }
}
