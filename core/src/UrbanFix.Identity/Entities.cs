using UrbanFix.SharedKernel;

namespace UrbanFix.Identity;

/// <summary>Marks entities whose inserts, updates and deletes are written to the audit log automatically.</summary>
public interface IAudited
{
    Guid Id { get; }
    Guid OrganizationId { get; }
}

public class Organization
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A person. Global, not tenant-owned: roles in each organization live in <see cref="Membership"/>.</summary>
public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>International form, e.g. +923001234567; used for OTP login and for matching WhatsApp senders.</summary>
    public string? Phone { get; set; }
    public string PasswordHash { get; set; } = "";
    public string? TotpSecretProtected { get; set; }
    public bool TotpEnabled { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>"en" or "ur"; null follows the organization's default language.</summary>
    public string? PreferredLocale { get; set; }
    /// <summary>Extra notification channels the user opted into; the in-app inbox is always on.</summary>
    public string[] NotifyChannels { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One code sent to a phone (ID-05). Only a hash is kept.</summary>
public class OtpChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phone { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public bool Sent { get; set; } = true;
    public string? Ip { get; set; }
}

public class Membership : IAudited
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid UserId { get; set; }
    public Role Role { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? GateId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? OrganizationId { get; set; }
    public string TokenHash { get; set; } = "";
    /// <summary>False until the second factor is verified; such a session can only enrol or confirm TOTP.</summary>
    public bool TotpVerified { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? Ip { get; set; }
    public string? Device { get; set; }
    /// <summary>Set when this session is a support session: a platform person looking into one society, read-only (PA-05).</summary>
    public Guid? SupportSessionId { get; set; }
}

public class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string EntityId { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Ip { get; set; }
    public string? Device { get; set; }
}
