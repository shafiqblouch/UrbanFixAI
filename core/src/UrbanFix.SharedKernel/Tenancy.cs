namespace UrbanFix.SharedKernel;

/// <summary>Who is calling and for which organization. Applied to every DB connection so row-level security can filter.</summary>
public interface ITenantContext
{
    Guid? OrganizationId { get; }
    Guid? UserId { get; }
}

public sealed class TenantContext : ITenantContext
{
    public Guid? OrganizationId { get; private set; }
    public Guid? UserId { get; private set; }

    public void Set(Guid? organizationId, Guid? userId)
    {
        OrganizationId = organizationId;
        UserId = userId;
    }
}
