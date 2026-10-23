using UrbanFix.Identity;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Reports;

/// <summary>Who gets the monthly summary and when (DB-05). One row per society.</summary>
public class SummarySchedule : IAudited
{
    public Guid Id => OrganizationId;
    public Guid OrganizationId { get; set; }
    public bool Enabled { get; set; }
    public int DayOfMonth { get; set; } = 1;
    public string? Language { get; set; }
    public Guid[] RecipientUserIds { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SummaryReport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public string Language { get; set; } = "en";
    public bool IncludesCosts { get; set; }
    public string StorageKey { get; set; } = "";
    public long SizeBytes { get; set; }
    public Guid[] RecipientUserIds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
}

/// <summary>A monthly summary was made and is ready for its recipients.</summary>
public sealed record MonthlySummaryReady(Guid OrganizationId, Guid ReportId, string Period, IReadOnlyList<Guid> Recipients) : DomainEvent(OrganizationId);
