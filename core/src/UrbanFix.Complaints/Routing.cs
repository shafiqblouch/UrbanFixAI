namespace UrbanFix.Complaints;

public class RoutingRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public Guid? CategoryId { get; set; }
    public Guid? LocationId { get; set; }
    public Guid TeamId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>One attempt by the AI service at a task on a complaint, successful or not (AI-15).</summary>
public class AiSuggestion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ComplaintId { get; set; }
    public string Task { get; set; } = "understanding";
    public string Status { get; set; } = "done";
    public int Attempt { get; set; } = 1;
    public string? CategoryKey { get; set; }
    public string? Summary { get; set; }
    public string? Language { get; set; }
    public string? LocationText { get; set; }
    public bool? NeedsMoreInfo { get; set; }
    public float? Confidence { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AiFeedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid ComplaintId { get; set; }
    public string Field { get; set; } = "";
    public string Suggested { get; set; } = "";
    public string FinalValue { get; set; } = "";
    public bool Accepted { get; set; }
    public string? Language { get; set; }
    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public float? Confidence { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A rule with its location scope resolved to the materialised path (null scope = anywhere).</summary>
public sealed record ScopedRule(Guid Id, Guid? CategoryId, string? ScopePath, int ScopeDepth, Guid TeamId, int SortOrder);

/// <summary>
/// Picks the team for a complaint (RT-01). A rule matches when its category (if any) equals the complaint's and the complaint's
/// location sits inside the rule's location (if any). The most specific match wins: a deeper location beats a shallower one,
/// and naming a category beats not naming one. Ties go to the lower sort order, so rules are evaluated "in order".
/// </summary>
public static class RoutingEngine
{
    public static int Specificity(ScopedRule r) => (r.ScopePath is null ? 0 : r.ScopeDepth + 1) * 2 + (r.CategoryId is null ? 0 : 1);

    public static ScopedRule? Resolve(IEnumerable<ScopedRule> rules, Guid? categoryId, string? locationPath) =>
        rules
            .Where(r => (r.CategoryId is null || r.CategoryId == categoryId)
                        && (r.ScopePath is null || (locationPath is not null && locationPath.StartsWith(r.ScopePath, StringComparison.Ordinal))))
            .OrderByDescending(Specificity).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .FirstOrDefault();
}

/// <summary>How long an open job may take before it counts as overdue, by complaint priority (DB-01).</summary>
public static class Sla
{
    public static TimeSpan For(string priority) => priority switch
    {
        "Urgent" => TimeSpan.FromHours(24),
        "High" => TimeSpan.FromHours(72),
        "Low" => TimeSpan.FromHours(14 * 24),
        _ => TimeSpan.FromHours(7 * 24),
    };

    public static bool IsOverdue(string priority, DateTimeOffset complaintCreatedAt, DateTimeOffset now) => now - complaintCreatedAt > For(priority);
}
