using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.WhatsApp;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<OrganizationSettings> OrgSettings => Set<OrganizationSettings>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<House> Houses => Set<House>();
    public DbSet<Landmark> Landmarks => Set<Landmark>();
    public DbSet<ResidentLink> ResidentLinks => Set<ResidentLink>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<DuplicateSuggestion> DuplicateSuggestions => Set<DuplicateSuggestion>();
    public DbSet<JobCharge> JobCharges => Set<JobCharge>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockSetting> StockSettings => Set<StockSetting>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<StockRequest> StockRequests => Set<StockRequest>();
    public DbSet<StockRequestLine> StockRequestLines => Set<StockRequestLine>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<DailyHelp> DailyHelp => Set<DailyHelp>();
    public DbSet<DailyHelpHouse> DailyHelpHouses => Set<DailyHelpHouse>();
    public DbSet<DirectoryDismissal> DirectoryDismissals => Set<DirectoryDismissal>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<PriceItem> PriceItems => Set<PriceItem>();
    public DbSet<PriceRate> PriceRates => Set<PriceRate>();

    public DbSet<Complaint> Complaints => Set<Complaint>();
    public DbSet<ComplaintStatusHistory> ComplaintHistory => Set<ComplaintStatusHistory>();
    public DbSet<ComplaintMedia> ComplaintMedia => Set<ComplaintMedia>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ApprovalDecision> ApprovalDecisions => Set<ApprovalDecision>();
    public DbSet<ComplaintFeedback> Feedback => Set<ComplaintFeedback>();
    public DbSet<AiUsageEntry> AiUsage => Set<AiUsageEntry>();
    public DbSet<JobComparison> JobComparisons => Set<JobComparison>();
    public DbSet<JobUpdate> JobUpdates => Set<JobUpdate>();
    public DbSet<Estimate> Estimates => Set<Estimate>();
    public DbSet<EstimateLine> EstimateLines => Set<EstimateLine>();
    public DbSet<JobCost> JobCosts => Set<JobCost>();
    public DbSet<RoutingRule> RoutingRules => Set<RoutingRule>();
    public DbSet<AiSuggestion> AiSuggestions => Set<AiSuggestion>();
    public DbSet<WhatsAppChannel> WhatsAppChannels => Set<WhatsAppChannel>();
    public DbSet<WhatsAppMessage> WhatsAppMessages => Set<WhatsAppMessage>();
    public DbSet<WhatsAppConversation> WhatsAppConversations => Set<WhatsAppConversation>();
    public DbSet<Escalation> Escalations => Set<Escalation>();
    public DbSet<EscalationLevel> EscalationLevels => Set<EscalationLevel>();
    public DbSet<AiFeedback> AiFeedback => Set<AiFeedback>();
    public DbSet<UrbanFix.Infrastructure.Reports.SummarySchedule> SummarySchedules => Set<UrbanFix.Infrastructure.Reports.SummarySchedule>();
    public DbSet<UrbanFix.Infrastructure.Reports.SummaryReport> SummaryReports => Set<UrbanFix.Infrastructure.Reports.SummaryReport>();
    public DbSet<UrbanFix.Infrastructure.Platform.OrganizationFeature> OrganizationFeatures => Set<UrbanFix.Infrastructure.Platform.OrganizationFeature>();
    public DbSet<UrbanFix.Infrastructure.Push.PushDevice> PushDevices => Set<UrbanFix.Infrastructure.Push.PushDevice>();
    public DbSet<Notice> Notices => Set<Notice>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<UploadSession> Uploads => Set<UploadSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Organization>().ToTable("organizations");
        b.Entity<User>().ToTable("users");
        b.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.Property(x => x.Role).HasConversion<string>();
        });
        b.Entity<Session>().ToTable("sessions");
        b.Entity<OtpChallenge>().ToTable("otp_challenges");
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit_log");
            e.Property(x => x.Before).HasColumnType("jsonb");
            e.Property(x => x.After).HasColumnType("jsonb");
        });
        b.Entity<OrganizationSettings>(e =>
        {
            e.ToTable("organization_settings");
            e.HasKey(x => x.OrganizationId);
            e.Ignore(x => x.Id);
            e.Property(x => x.Settings).HasColumnType("jsonb");
        });
        b.Entity<Location>().ToTable("locations");
        b.Entity<Department>().ToTable("departments");
        b.Entity<Team>().ToTable("teams");
        b.Entity<TeamMember>().ToTable("team_members");
        b.Entity<Category>(e =>
        {
            e.ToTable("categories");
            e.Property(x => x.Checklist).HasColumnType("jsonb");
        });
        b.Entity<House>(e =>
        {
            e.ToTable("houses");
            e.Property(x => x.PlotSizeSqYd).HasColumnName("plot_size_sqyd");
        });
        b.Entity<ResidentLink>().ToTable("resident_links");
        b.Entity<Landmark>().ToTable("landmarks");
        b.Entity<SlaPolicy>().ToTable("sla_policies");
        b.Entity<Vehicle>().ToTable("vehicles");
        b.Entity<Incident>().ToTable("incidents");
        b.Entity<DuplicateSuggestion>().ToTable("duplicate_suggestions");
        b.Entity<JobCharge>().ToTable("job_charges");
        b.Entity<StockMovement>().ToTable("stock_movements");
        b.Entity<StockSetting>().ToTable("stock_settings");
        b.Entity<StockCount>().ToTable("stock_counts");
        b.Entity<StockCountLine>(e => { e.ToTable("stock_count_lines"); e.HasOne<StockCount>().WithMany().HasForeignKey(x => x.CountId); });
        b.Entity<StockRequest>().ToTable("stock_requests");
        b.Entity<StockRequestLine>(e => { e.ToTable("stock_request_lines"); e.HasOne<StockRequest>().WithMany().HasForeignKey(x => x.RequestId); });
        b.Entity<DailyHelp>(e =>
        {
            e.ToTable("daily_help");
            e.Property(x => x.AllowedDays).HasColumnType("text[]");
        });
        b.Entity<DailyHelpHouse>().ToTable("daily_help_houses");
        b.Entity<DirectoryDismissal>().ToTable("directory_dismissals");
        b.Entity<PriceItem>().ToTable("price_items");
        b.Entity<PriceRate>().ToTable("price_rates");

        b.Entity<Complaint>(e =>
        {
            e.ToTable("complaints");
            e.Property(x => x.Status).HasConversion<string>();
        });
        b.Entity<ComplaintStatusHistory>(e =>
        {
            e.ToTable("complaint_status_history");
            // Declared so EF inserts the complaint before its history in one SaveChanges (the DB constraint is composite).
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
            e.Property(x => x.FromStatus).HasConversion<string>();
            e.Property(x => x.ToStatus).HasConversion<string>();
        });
        b.Entity<ComplaintMedia>(e =>
        {
            e.ToTable("complaint_media");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId);
        });
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
            e.Property(x => x.Checklist).HasColumnType("jsonb");
        });
        b.Entity<Approval>().ToTable("approvals");
        b.Entity<ApprovalDecision>(e =>
        {
            e.ToTable("approval_decisions");
            e.HasOne<Approval>().WithMany().HasForeignKey(x => x.ApprovalId);
        });
        b.Entity<ComplaintFeedback>(e =>
        {
            e.ToTable("complaint_feedback");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
        });
        b.Entity<AiUsageEntry>().ToTable("ai_usage");
        b.Entity<JobComparison>(e =>
        {
            e.ToTable("job_comparisons");
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId);
        });
        b.Entity<JobUpdate>(e =>
        {
            e.ToTable("job_updates");
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId);
        });
        b.Entity<Estimate>(e =>
        {
            e.ToTable("estimates");
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId);
        });
        b.Entity<EstimateLine>(e =>
        {
            e.ToTable("estimate_lines");
            e.HasOne<Estimate>().WithMany().HasForeignKey(x => x.EstimateId);
        });
        b.Entity<JobCost>(e =>
        {
            e.ToTable("job_costs");
            e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId);
        });
        b.Entity<RoutingRule>().ToTable("routing_rules");
        b.Entity<WhatsAppChannel>().ToTable("whatsapp_channels");
        b.Entity<WhatsAppMessage>(e =>
        {
            e.ToTable("whatsapp_messages");
            e.Property(x => x.Raw).HasColumnType("jsonb");
        });
        b.Entity<WhatsAppConversation>(e =>
        {
            e.ToTable("whatsapp_conversations");
            e.Property(x => x.StateData).HasColumnType("jsonb");
        });
        b.Entity<Escalation>(e =>
        {
            e.ToTable("escalations");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
        });
        b.Entity<EscalationLevel>(e =>
        {
            e.ToTable("escalation_levels");
            e.Property(x => x.Role).HasConversion<string>();
        });
        b.Entity<AiSuggestion>(e =>
        {
            e.ToTable("ai_suggestions");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
        });
        b.Entity<AiFeedback>(e =>
        {
            e.ToTable("ai_feedback");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
        });
        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(x => x.TemplateValues).HasColumnType("jsonb");
        });
        b.Entity<NotificationDelivery>(e =>
        {
            e.ToTable("notification_deliveries");
            e.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId);
        });
        b.Entity<Notice>().ToTable("notices");
        b.Entity<UrbanFix.Infrastructure.Push.PushDevice>().ToTable("push_devices");
        b.Entity<UrbanFix.Infrastructure.Platform.OrganizationFeature>(e => { e.ToTable("organization_features"); e.HasKey(x => new { x.OrganizationId, x.Feature }); });
        b.Entity<UrbanFix.Infrastructure.Reports.SummarySchedule>(e => { e.ToTable("summary_schedules"); e.HasKey(x => x.OrganizationId); e.Ignore(x => x.Id); });
        b.Entity<UrbanFix.Infrastructure.Reports.SummaryReport>().ToTable("summary_reports");
        b.Entity<UploadSession>().ToTable("upload_sessions");
        b.Entity<NumberSequence>(e =>
        {
            e.ToTable("number_sequences");
            e.HasKey(x => new { x.OrganizationId, x.Scope, x.Year });
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }

    /// <summary>
    /// Switches the organization/user the next statements run as. The connection is closed so the tenant
    /// interceptor re-applies the context when it reopens; row-level security then filters accordingly.
    /// </summary>
    public async Task ChangeTenantAsync(Guid? organizationId, Guid? userId, CancellationToken ct = default)
    {
        ((TenantContext)tenant).Set(organizationId, userId);
        await Database.CloseConnectionAsync();
    }
}

public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? Error { get; set; }
}

