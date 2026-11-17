using UrbanFix.Identity;
using UrbanFix.SharedKernel;

namespace UrbanFix.OrgSetup;

/// <summary>A manned entry point — a boom barrier or pedestrian gate. Guards are scoped to one through <see cref="Membership.GateId"/> (GT-00).</summary>
public class Gate : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// A resident or the office authorises an expected visitor or vehicle ahead of time; the guard looks the code up
/// at the gate and lets them in without a phone call (GT-03). Works offline once synced to the guard's device.
/// </summary>
public class GatePass : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>Short human code the visitor shows, unique per organization, e.g. "PX7QK2".</summary>
    public string Code { get; set; } = "";
    public string VisitorName { get; set; } = "";
    public string? Phone { get; set; }
    public string? VehiclePlate { get; set; }
    /// <summary>Letters and digits only, upper case, so "abc 123" and "ABC-123" match (as for vehicles).</summary>
    public string? PlateKey { get; set; }
    /// <summary>The house being visited.</summary>
    public Guid HouseId { get; set; }
    /// <summary>visitor | guest | delivery | help | contractor.</summary>
    public string Kind { get; set; } = "visitor";
    public string? Purpose { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset ValidTo { get; set; }
    /// <summary>Null means any number of entries inside the window.</summary>
    public int? MaxUses { get; set; }
    public int UsedCount { get; set; }
    /// <summary>active | cancelled | pending (a guard asked the resident) | denied (the resident said no).</summary>
    public string Status { get; set; } = "active";
    public Guid? CreatedBy { get; set; }
    /// <summary>The resident who approved or denied a pending request (GT-04).</summary>
    public Guid? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// One crossing: a person or vehicle going in or out, as the guard recorded it (GT-01). Append-only — a correction
/// is a new event, never an edit — so the log can be trusted. Most columns are optional: a registered car fills
/// <see cref="VehicleId"/> and <see cref="HouseId"/>, a walk-up visitor fills only <see cref="SubjectName"/>.
/// </summary>
public class GateEvent : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? GateId { get; set; }
    /// <summary>in | out.</summary>
    public string Direction { get; set; } = "in";
    /// <summary>resident | vehicle | help | visitor | delivery.</summary>
    public string SubjectType { get; set; } = "visitor";
    public Guid? HouseId { get; set; }
    public Guid? VehicleId { get; set; }
    public Guid? HelpId { get; set; }
    public Guid? PassId { get; set; }
    public Guid? ResidentUserId { get; set; }
    /// <summary>Free text for a person or car not in the directory.</summary>
    public string? SubjectName { get; set; }
    public string? Phone { get; set; }
    public string? VehiclePlate { get; set; }
    public string? PlateKey { get; set; }
    public string? PhotoKey { get; set; }
    /// <summary>allowed | denied.</summary>
    public string Decision { get; set; } = "allowed";
    public string? Note { get; set; }
    public Guid? RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Small gate rules as pure functions, kept together so they can be read and tested in one place.</summary>
public static class GateRules
{
    public static readonly string[] Directions = ["in", "out"];
    public static readonly string[] SubjectTypes = ["resident", "vehicle", "help", "visitor", "delivery"];
    public static readonly string[] Decisions = ["allowed", "denied"];
    public static readonly string[] PassKinds = ["visitor", "guest", "delivery", "help", "contractor"];
    public static readonly string[] PassStatuses = ["active", "cancelled", "pending", "denied"];

    /// <summary>No 0/O/1/I/S/5/2/Z/8/B look-alikes, so a code read aloud at a gate is not mistyped.</summary>
    private const string Alphabet = "ACDEFGHJKLMNPQRTUVWXY34679";

    public static string NewPassCode(Random? rng = null)
    {
        rng ??= Random.Shared;
        return new string(Enumerable.Range(0, 6).Select(_ => Alphabet[rng.Next(Alphabet.Length)]).ToArray());
    }

    /// <summary>Tidies a typed code to the stored form: letters and digits only, upper case.</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public enum PassState { Valid, NotYetValid, Expired, UsedUp, Cancelled, Pending, Denied }

    /// <summary>Whether a pass may be used right now, and if not, why — so the guard sees a clear reason.</summary>
    public static PassState Check(GatePass pass, DateTimeOffset now)
    {
        if (pass.Status == "cancelled") return PassState.Cancelled;
        if (pass.Status == "denied") return PassState.Denied;
        if (pass.Status == "pending") return PassState.Pending; // the resident has not answered yet
        if (now < pass.ValidFrom) return PassState.NotYetValid;
        if (now > pass.ValidTo) return PassState.Expired;
        if (pass.MaxUses is { } max && pass.UsedCount >= max) return PassState.UsedUp;
        return PassState.Valid;
    }

    public static bool IsUsable(GatePass pass, DateTimeOffset now) => Check(pass, now) == PassState.Valid;

    /// <summary>
    /// A stable key for "the same person or car", used to pair an exit with its entry and to list who is inside.
    /// Prefers a directory id; falls back to the number plate, then the phone, then the name.
    /// </summary>
    public static string SubjectKey(GateEvent e) =>
        e.VehicleId is { } v ? $"vehicle:{v}"
        : e.HelpId is { } h ? $"help:{h}"
        : e.ResidentUserId is { } r ? $"resident:{r}"
        : e.PassId is { } p ? $"pass:{p}"
        : !string.IsNullOrEmpty(e.PlateKey) ? $"plate:{e.PlateKey}"
        : !string.IsNullOrWhiteSpace(e.Phone) ? $"phone:{e.Phone}"
        : $"name:{(e.SubjectName ?? "").Trim().ToLowerInvariant()}";

    /// <summary>
    /// From a run of events (any order) the subjects currently inside: for each subject the latest event is an
    /// allowed entry. An unknown walk-up with no name at all is ignored (it cannot be paired to an exit).
    /// </summary>
    public static IEnumerable<GateEvent> Inside(IEnumerable<GateEvent> events) =>
        events
            .Where(e => e.Decision == "allowed")
            .GroupBy(SubjectKey)
            .Where(g => g.Key != "name:")
            .Select(g => g.OrderByDescending(e => e.RecordedAt).First())
            .Where(e => e.Direction == "in");
}

/// <summary>A guard asked the residents of a house to approve a visitor waiting at the gate (GT-04).</summary>
public sealed record GateVisitRequested(Guid OrganizationId, Guid PassId, Guid HouseId, string VisitorName, IReadOnlyList<Guid> ResidentUserIds) : DomainEvent(OrganizationId);

/// <summary>A resident approved or denied a visitor the guard asked about; the requesting guard is told (GT-04).</summary>
public sealed record GateVisitDecided(Guid OrganizationId, Guid PassId, Guid GuardUserId, bool Approved, string VisitorName) : DomainEvent(OrganizationId);
