using UrbanFix.Identity;

namespace UrbanFix.OrgSetup;

public class OrganizationSettings : IAudited
{
    /// <summary>One row per organization; the organization id doubles as the row id.</summary>
    public Guid Id => OrganizationId;
    public Guid OrganizationId { get; set; }
    public string? DisplayName { get; set; }
    public string Timezone { get; set; } = "Asia/Karachi";
    public string Locale { get; set; } = "en";
    public string LocationTemplate { get; set; } = "society";
    public string? PrimaryColor { get; set; }
    public string? LogoKey { get; set; }
    public string Settings { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Location : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ParentId { get; set; }
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string? NameUr { get; set; }
    /// <summary>Materialised path of ancestor ids, e.g. "/a/b/c/". Subtree = prefix match, so it is one index scan.</summary>
    public string Path { get; set; } = "";
    public int Depth { get; set; }
    /// <summary>0 normal, 1 raised, 2 high; lifts the priority of complaints here (RT-04).</summary>
    public int RiskLevel { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Department : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? NameUr { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Team : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid DepartmentId { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class TeamMember : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid TeamId { get; set; }
    public Guid UserId { get; set; }
    public bool IsSupervisor { get; set; }
}

public class Category : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Key { get; set; } = "";
    public string NameEn { get; set; } = "";
    public string? NameUr { get; set; }
    public Guid? DepartmentId { get; set; }
    public string DefaultPriority { get; set; } = "Normal";
    public string EvidenceRule { get; set; } = "photo";
    public string Checklist { get; set; } = "[]";
    /// <summary>Jobs in this category cannot be completed without actual cost lines (JC-04).</summary>
    public bool RequiresCosts { get; set; }
    /// <summary>The system may assign complaints in this category without a supervisor when confidence is high enough (RT-02).</summary>
    public bool AutoRoute { get; set; }
    public float AutoRouteThreshold { get; set; } = 0.9f;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>Response and resolution targets for a priority, optionally for one category (RT-06).</summary>
public class SlaPolicy : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? CategoryId { get; set; }
    public string Priority { get; set; } = "Normal";
    public int RespondHours { get; set; }
    public int ResolveHours { get; set; }
    public bool BusinessHoursOnly { get; set; }
    public bool IsActive { get; set; } = true;
}

public class House : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid LocationId { get; set; }
    public decimal? PlotSizeSqYd { get; set; }
    public string? HouseCategory { get; set; }
    public string Status { get; set; } = "occupied";
}

public class ResidentLink : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid HouseId { get; set; }
    public Guid UserId { get; set; }
    public string Relation { get; set; } = "";
    public DateOnly FromDate { get; set; }
    public DateOnly? ToDate { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Landmark : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? NameUr { get; set; }
    public string[] Aliases { get; set; } = [];
    public Guid? LocationId { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public bool IsActive { get; set; } = true;
}

public class PriceItem : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string? NameUr { get; set; }
    public string Unit { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>Append-only. Estimates reference a rate row, so history never changes under them.</summary>
public class PriceRate : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ItemId { get; set; }
    public decimal Rate { get; set; }
    public DateTimeOffset EffectiveFrom { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Which child kinds a location may have, per template (OR-02).</summary>
public static class LocationTemplates
{
    public static readonly IReadOnlyDictionary<string, string[]> Levels = new Dictionary<string, string[]>
    {
        ["society"] = ["sector", "street", "block", "house"],
        ["building"] = ["building", "floor", "unit"],
    };

    /// <summary>The leaf kind that can hold a house/unit record.</summary>
    public static string LeafKind(string template) => Levels[template][^1];

    /// <summary>A child must sit strictly deeper in the template than its parent; levels may be skipped (a society without sectors).</summary>
    public static string? Validate(string template, string? parentKind, string kind)
    {
        if (!Levels.TryGetValue(template, out var levels)) return $"unknown template '{template}'";
        var childIndex = Array.IndexOf(levels, kind);
        if (childIndex < 0) return $"'{kind}' is not a level of the {template} template";
        if (parentKind is null) return null;
        var parentIndex = Array.IndexOf(levels, parentKind);
        return parentIndex >= childIndex ? $"a {kind} cannot sit under a {parentKind}" : null;
    }
}

public static class CategoryTemplates
{
    public sealed record Seed(string Key, string NameEn, string NameUr, string Priority, string Evidence, string[] Checklist);

    public static readonly IReadOnlyList<Seed> All =
    [
        new("water", "Water supply", "پانی کی فراہمی", "High", "photo", ["Check main valve", "Check pipe joints", "Test pressure"]),
        new("roads", "Roads and potholes", "سڑکیں اور گڑھے", "Normal", "photo", ["Measure damaged area", "Barricade if unsafe"]),
        new("streetlights", "Street lights", "اسٹریٹ لائٹس", "Normal", "photo", ["Check bulb", "Check fuse and wiring"]),
        new("sewerage", "Sewerage and drainage", "سیوریج اور نکاسی", "High", "photo", ["Locate blockage", "Clear line", "Flush and check flow"]),
        new("sanitation", "Sanitation and garbage", "صفائی اور کچرا", "Normal", "photo", ["Clear waste", "Disinfect area"]),
        new("parks", "Parks and green belts", "پارک اور سبزہ زار", "Low", "photo", ["Inspect equipment", "Trim or repair"]),
        new("building-maintenance", "Building maintenance", "عمارت کی دیکھ بھال", "Normal", "checklist", ["Inspect issue", "Repair", "Clean up"]),
        new("parking", "Parking", "پارکنگ", "Low", "note", []),
    ];
}

