using Microsoft.EntityFrameworkCore;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;

namespace UrbanFix.Infrastructure.Platform;

/// <summary>
/// The features that can be switched on or off for one organization without a deployment (FD-13). The first group is already
/// built; the rest are still to come and stay off until a society has passed its own go/no-go.
/// </summary>
public static class Features
{
    public const string AutoRouting = "auto_routing";
    public const string Gate = "gate";
    public const string Billing = "billing";
    public const string Requests = "requests";
    public const string Booking = "booking";
    public const string Accounting = "accounting";
    public const string Payroll = "payroll";
    public const string Governance = "governance";
    public const string Intercom = "intercom";
    public const string Assistant = "assistant";

    /// <summary>Every flag and whether it is on for an organization that has never been given a choice.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Defaults = new Dictionary<string, bool>
    {
        // The AI may route by itself only once a category has passed the accuracy gate (AI-17); that gate is the safeguard, so it is on by default.
        [AutoRouting] = true,
        [Gate] = false, [Billing] = false, [Requests] = false, [Booking] = false, [Accounting] = false,
        [Payroll] = false, [Governance] = false, [Intercom] = false, [Assistant] = false,
    };

    public static bool IsKnown(string feature) => Defaults.ContainsKey(feature);
}

/// <summary>A choice made for one organization. No row means the default applies.</summary>
public class OrganizationFeature
{
    public Guid OrganizationId { get; set; }
    public string Feature { get; set; } = "";
    public bool Enabled { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class FeatureFlags
{
    public static async Task<bool> IsEnabledAsync(AppDbContext db, string feature, CancellationToken ct) =>
        await db.OrganizationFeatures.AsNoTracking().Where(f => f.Feature == feature).Select(f => (bool?)f.Enabled).SingleOrDefaultAsync(ct)
        ?? Features.Defaults.GetValueOrDefault(feature);

    /// <summary>Every flag for the current organization, with its choice or default.</summary>
    public static async Task<Dictionary<string, bool>> AllAsync(AppDbContext db, CancellationToken ct)
    {
        var chosen = await db.OrganizationFeatures.AsNoTracking().ToDictionaryAsync(f => f.Feature, f => f.Enabled, ct);
        return Features.Defaults.ToDictionary(d => d.Key, d => chosen.TryGetValue(d.Key, out var on) ? on : d.Value);
    }
}
