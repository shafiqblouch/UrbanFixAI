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
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
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
    public Guid? CreatedBy { get; set; }
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
