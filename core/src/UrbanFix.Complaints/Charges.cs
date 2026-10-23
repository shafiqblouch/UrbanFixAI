using UrbanFix.Identity;

namespace UrbanFix.Complaints;

public static class ChargeStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Adjusted = "Adjusted";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
}

/// <summary>
/// A finished repair that is to be charged to a house, waiting for accounts to check it (JC-11). The cost is a snapshot taken when
/// the job was finished. Once decided it is never changed: posting to the house's account waits for billing (BL-20).
/// </summary>
public class JobCharge : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid JobId { get; set; }
    public Guid HouseId { get; set; }
    public decimal ProposedAmount { get; set; }
    public decimal? FinalAmount { get; set; }
    public string Status { get; set; } = ChargeStatus.Pending;
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ChargeRules
{
    public static readonly string[] Actions = ["approve", "adjust", "reject"];

    /// <summary>Accounts can approve as proposed, change the amount (with a reason), or reject (with a reason). Nothing is decided twice.</summary>
    public static (string? Error, string Status, decimal? Final) Decide(string status, decimal proposed, string action, decimal? amount, string? note)
    {
        if (status != ChargeStatus.Pending) return ("not_pending", status, null);
        switch (action)
        {
            case "approve": return (null, ChargeStatus.Approved, proposed);
            case "adjust":
                if (amount is null or < 0) return ("amount_required", status, null);
                if (string.IsNullOrWhiteSpace(note)) return ("reason_required", status, null);
                return (null, ChargeStatus.Adjusted, Math.Round(amount.Value, 2));
            case "reject":
                return string.IsNullOrWhiteSpace(note) ? ("reason_required", status, null) : (null, ChargeStatus.Rejected, null);
            default: return ("unknown_action", status, null);
        }
    }

    public static bool ReadyToPost(string status) => status is ChargeStatus.Approved or ChargeStatus.Adjusted;
}
