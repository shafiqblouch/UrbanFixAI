using UrbanFix.SharedKernel;

namespace UrbanFix.Complaints;

/// <summary>A file was attached to a complaint; audio is sent for transcription (AI-09).</summary>
public sealed record MediaAdded(Guid OrganizationId, Guid MediaId, Guid ComplaintId, string Kind) : DomainEvent(OrganizationId);

/// <summary>Words used when a complaint has no written description of its own.</summary>
public static class IntakeText
{
    /// <summary>The description of a complaint sent as only a photo or a voice note; replaced by the transcript once a voice note is transcribed.</summary>
    public const string MediaOnly = "Photo or voice message sent on WhatsApp, no written description.";
}

/// <summary>The resident has not yet confirmed that finished work is fixed (CP-10).</summary>
public sealed record ConfirmationReminder(Guid OrganizationId, Guid ComplaintId, string Reference) : DomainEvent(OrganizationId);

/// <summary>Raised once when a complaint is flagged as a life-safety matter (RT-05).</summary>
public sealed record ComplaintSafetyFlagged(Guid OrganizationId, Guid ComplaintId, string Reference) : DomainEvent(OrganizationId);

/// <summary>
/// Priority levels (RT-04). Stored names are kept from the POC; the owner-facing labels are routine / standard / prompt / urgent / safety.
/// </summary>
public static class PriorityLevels
{
    public const string Safety = "Safety";
    public static readonly string[] Ordered = ["Low", "Normal", "High", "Urgent", Safety];

    public static int Rank(string priority) => Math.Max(0, Array.IndexOf(Ordered, priority));
    public static bool IsValid(string priority) => Array.IndexOf(Ordered, priority) >= 0;

    public static string Label(string priority) => priority switch
    {
        "Low" => "routine", "Normal" => "standard", "High" => "prompt", "Urgent" => "urgent", Safety => "safety", _ => "standard",
    };
}

/// <summary>
/// Works out a complaint's priority from the category default, the risk of the place, and any safety flag (RT-04).
/// A safety flag always wins and always needs a person: nothing flagged for safety is routed automatically.
/// </summary>
public static class PriorityEngine
{
    public sealed record Result(string Priority, bool RequiresPerson);

    /// <param name="locationRisk">0 normal, 1 raised, 2 high (a school, a main road). Each step lifts the priority one level, up to urgent.</param>
    public static Result Compute(string categoryDefault, int locationRisk, bool safetyFlag)
    {
        if (safetyFlag) return new Result(PriorityLevels.Safety, true);
        var baseRank = PriorityLevels.Rank(categoryDefault);
        var cap = PriorityLevels.Rank("Urgent");
        // Risk lifts a priority up to urgent but never beyond; a default already at or past urgent is kept.
        var rank = baseRank >= cap ? baseRank : Math.Min(baseRank + Math.Clamp(locationRisk, 0, 2), cap);
        return new Result(PriorityLevels.Ordered[rank], rank == PriorityLevels.Rank(PriorityLevels.Safety));
    }
}

/// <summary>
/// Danger words in English, Urdu and Roman Urdu (AI-10, RT-05). The list lives in <c>contracts/safety-terms.json</c> so the
/// server and the mobile app (RA-08) use exactly the same words. Deliberately over-eager: a false flag costs a glance, a miss costs more.
/// </summary>
public static class SafetyTerms
{
    private static readonly string[] All = Load();

    private static string[] Load()
    {
        using var stream = typeof(SafetyTerms).Assembly.GetManifestResourceStream("safety-terms.json")
            ?? throw new InvalidOperationException("safety-terms.json is not embedded in the Complaints assembly");
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        return doc.RootElement.GetProperty("terms").EnumerateObject()
            .SelectMany(language => language.Value.EnumerateArray().Select(t => t.GetString()!.ToLowerInvariant()))
            .Distinct().ToArray();
    }

    public static IReadOnlyList<string> Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var t = text.ToLowerInvariant();
        return All.Where(term => t.Contains(term, StringComparison.Ordinal)).ToList();
    }

    public static bool IsSafety(string? text) => Find(text).Count > 0;
}

/// <summary>Decides whether the system may assign a complaint on its own (RT-02). Everything else goes to the supervisor queue.</summary>
public static class AutoRoutePolicy
{
    public const float DefaultThreshold = 0.9f;

    /// <returns>Null when the complaint may be routed automatically, otherwise why it is queued for a person.</returns>
    public static string? WhyQueued(bool categoryEnabled, float threshold, float? confidence, string priority, bool safetyFlag, bool categoryChosenByPerson, bool teamFound)
    {
        if (safetyFlag || priority == PriorityLevels.Safety) return "safety";
        if (!categoryEnabled) return "auto_route_off";
        if (categoryChosenByPerson) return "category_set_by_person";
        if (confidence is null) return "no_confidence";
        if (confidence < threshold) return "below_threshold";
        if (!teamFound) return "no_matching_rule";
        return null;
    }
}

/// <summary>SLA deadlines (RT-06). A policy counts elapsed hours, or only working hours in the organization's timezone.</summary>
public static class SlaCalculator
{
    public sealed record Policy(int RespondHours, int ResolveHours, bool BusinessHoursOnly, int WorkStartHour = 9, int WorkEndHour = 18);

    /// <summary>Built-in policy when an organization has defined none; matches the POC overdue limits.</summary>
    public static Policy Default(string priority) => new(
        RespondHours: priority switch { "Safety" => 1, "Urgent" => 4, "High" => 12, "Low" => 72, _ => 24 },
        ResolveHours: priority switch { "Safety" => 4, "Urgent" => 24, "High" => 72, "Low" => 14 * 24, _ => 7 * 24 },
        BusinessHoursOnly: false);

    public static DateTimeOffset Deadline(DateTimeOffset start, int hours, bool businessHoursOnly, TimeZoneInfo zone, int workStartHour = 9, int workEndHour = 18)
    {
        if (!businessHoursOnly) return start.AddHours(hours);
        if (workEndHour <= workStartHour) throw new ArgumentException("Working day must end after it starts");

        var local = TimeZoneInfo.ConvertTime(start, zone).DateTime;
        var remaining = TimeSpan.FromHours(hours);
        while (true)
        {
            local = SkipToWorkingTime(local, workStartHour, workEndHour);
            var endOfDay = local.Date.AddHours(workEndHour);
            var room = endOfDay - local;
            if (remaining <= room) { local += remaining; break; }
            remaining -= room;
            local = local.Date.AddDays(1).AddHours(workStartHour);
        }
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>Moves a local time forward to the next working moment (Sunday is a day off).</summary>
    private static DateTime SkipToWorkingTime(DateTime local, int startHour, int endHour)
    {
        while (true)
        {
            if (local.DayOfWeek == DayOfWeek.Sunday) { local = local.Date.AddDays(1).AddHours(startHour); continue; }
            if (local.TimeOfDay < TimeSpan.FromHours(startHour)) return local.Date.AddHours(startHour);
            if (local.TimeOfDay >= TimeSpan.FromHours(endHour)) { local = local.Date.AddDays(1).AddHours(startHour); continue; }
            return local;
        }
    }

    public static TimeZoneInfo ZoneOrUtc(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
}

/// <summary>Validation shared by the rule administration endpoints (RT-10). Returns an error code, or null when the rule set is sound.</summary>
public static class RuleChecks
{
    public sealed record RuleKey(Guid? Id, Guid? CategoryId, Guid? LocationId, Guid TeamId, int SortOrder, bool IsActive);

    /// <summary>Two active rules with the same scope and the same sort order but different teams make the result depend on chance.</summary>
    public static string? Ambiguous(RuleKey candidate, IEnumerable<RuleKey> existing)
    {
        if (!candidate.IsActive) return null;
        var clash = existing.Any(r => r.IsActive && r.Id != candidate.Id
            && r.CategoryId == candidate.CategoryId && r.LocationId == candidate.LocationId
            && r.SortOrder == candidate.SortOrder && r.TeamId != candidate.TeamId);
        return clash ? "ambiguous_rule" : null;
    }

    public static string? Sla(int respondHours, int resolveHours, string priority)
    {
        if (!PriorityLevels.IsValid(priority)) return "invalid_priority";
        if (respondHours <= 0 || resolveHours <= 0) return "hours_must_be_positive";
        if (respondHours > resolveHours) return "respond_after_resolve";
        if (resolveHours > 24 * 365) return "hours_too_large";
        return null;
    }

    public static string? AutoRoute(float threshold)
    {
        // A threshold below 0.5 would route near-guesses; the switch itself is always valid (AI-17 decides when to turn it on).
        return threshold is < 0.5f or > 1f ? "threshold_out_of_range" : null;
    }
}
