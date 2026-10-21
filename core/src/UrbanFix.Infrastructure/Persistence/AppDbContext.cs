using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Membership> Memberships => Set<Membership>();
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
    public DbSet<PriceItem> PriceItems => Set<PriceItem>();
    public DbSet<PriceRate> PriceRates => Set<PriceRate>();

    public DbSet<Complaint> Complaints => Set<Complaint>();
    public DbSet<ComplaintStatusHistory> ComplaintHistory => Set<ComplaintStatusHistory>();
    public DbSet<ComplaintMedia> ComplaintMedia => Set<ComplaintMedia>();
    public DbSet<Job> Jobs => Set<Job>();
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
        });
        b.Entity<Job>(e =>
        {
            e.ToTable("jobs");
            e.HasOne<Complaint>().WithMany().HasForeignKey(x => x.ComplaintId);
        });
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

