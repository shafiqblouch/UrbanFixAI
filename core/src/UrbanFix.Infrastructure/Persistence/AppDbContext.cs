using Microsoft.EntityFrameworkCore;
using UrbanFix.Identity;
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
