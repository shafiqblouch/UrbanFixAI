using UrbanFix.Identity;
using UrbanFix.SharedKernel;

namespace UrbanFix.Complaints;

public static class StockKinds
{
    public const string Receive = "receive";
    public const string Adjust = "adjust";
    public const string Issue = "issue";
    public const string Return = "return";
}

/// <summary>
/// One change to the store, never edited or deleted (JC-06). The quantity is signed, so current stock is simply the sum of an item's
/// movements; every one has a user and a reason.
/// </summary>
public class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ItemId { get; set; }
    public string Kind { get; set; } = "";
    public decimal Quantity { get; set; }
    public Guid? JobId { get; set; }
    public Guid? RequestId { get; set; }
    public Guid? CountId { get; set; }
    public string Reason { get; set; } = "";
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>When stock falls to this or below, the storekeeper is told once (JC-08).</summary>
public class StockSetting : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ItemId { get; set; }
    public decimal LowThreshold { get; set; }
    /// <summary>Set when the alert was sent; cleared when stock rises above the threshold again, so the next fall alerts again.</summary>
    public DateTimeOffset? LowAlertedAt { get; set; }
}

public class StockCount : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid CountedBy { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CountedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class StockCountLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid CountId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Expected { get; set; }
    public decimal Counted { get; set; }
    public string? Reason { get; set; }
}

public static class RequestStatus
{
    public const string Open = "Open";
    public const string Fulfilled = "Fulfilled";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

/// <summary>A technician asks the store for materials for a job (TA-09).</summary>
public class StockRequest : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid JobId { get; set; }
    public Guid RequestedBy { get; set; }
    public string Status { get; set; } = RequestStatus.Open;
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
}

public class StockRequestLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Quantity { get; set; }
}

public sealed record StockLow(Guid OrganizationId, Guid ItemId, string ItemName, decimal Quantity, decimal Threshold) : DomainEvent(OrganizationId);
public sealed record StockRequested(Guid OrganizationId, Guid RequestId, Guid JobId, string JobNumber) : DomainEvent(OrganizationId);
public sealed record StockRequestDecided(Guid OrganizationId, Guid RequestId, Guid JobId, string JobNumber, bool Fulfilled, string? Note, Guid RequestedBy) : DomainEvent(OrganizationId);
public sealed record MaterialsMismatch(Guid OrganizationId, Guid JobId, string JobNumber, int Items) : DomainEvent(OrganizationId);

/// <summary>The rules of the store as pure functions (JC-06 to JC-09).</summary>
public static class StockRules
{
    /// <summary>Null when the movement is acceptable, otherwise a code. Receipts and returns add, issues take away, adjustments either way but never nothing, and an adjustment always says why.</summary>
    public static string? Check(string kind, decimal quantity, string? reason)
    {
        if (quantity == 0) return "quantity_required";
        if (string.IsNullOrWhiteSpace(reason) && kind is StockKinds.Adjust) return "reason_required";
        return kind switch
        {
            StockKinds.Receive or StockKinds.Return => quantity > 0 ? null : "quantity_must_be_positive",
            StockKinds.Issue => quantity < 0 ? null : "quantity_must_be_negative",
            StockKinds.Adjust => null,
            _ => "unknown_kind",
        };
    }

    /// <summary>Issued less returned is what the job drew; the difference from what was recorded as used is what is unexplained.</summary>
    public static decimal Unexplained(decimal issued, decimal returned, decimal used) => issued - returned - used;

    public static bool IsDiscrepancy(decimal issued, decimal returned, decimal used) => Unexplained(issued, returned, used) != 0;

    /// <summary>
    /// The alert state after stock changes: it fires once when stock reaches the threshold, stays quiet while low, and re-arms when stock
    /// is above the threshold again (JC-08).
    /// </summary>
    public static (bool Alert, bool Alerted) NextAlert(decimal stock, decimal threshold, bool alreadyAlerted)
    {
        if (threshold <= 0) return (false, false);
        if (stock <= threshold) return (!alreadyAlerted, true);
        return (false, false);
    }

    public static string? CheckCount(decimal expected, decimal counted, string? reason)
    {
        if (counted < 0) return "counted_cannot_be_negative";
        return counted != expected && string.IsNullOrWhiteSpace(reason) ? "reason_required" : null;
    }
}
