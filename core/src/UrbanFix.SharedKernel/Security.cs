namespace UrbanFix.SharedKernel;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum Role
{
    Resident,
    Guard,
    Technician,
    Office,
    Supervisor,
    Accounts,
    Committee,
    Manager,
    PlatformAdmin,
    /// <summary>Never stored on a membership: it is what a platform person acts as inside a society during a support session (read-only, time-limited).</summary>
    Support,
}

public static class Permissions
{
    public const string ComplaintCreate = "complaint.create";
    public const string ComplaintReadOwn = "complaint.read.own";
    public const string ComplaintRead = "complaint.read";
    public const string ComplaintTriage = "complaint.triage";
    public const string ComplaintAssign = "complaint.assign";
    public const string ComplaintWork = "complaint.work";
    public const string ComplaintClose = "complaint.close";
    public const string EstimateApprove = "estimate.approve";
    public const string GateLog = "gate.log";
    public const string GateManage = "gate.manage";
    public const string BillRead = "bill.read";
    public const string BillManage = "bill.manage";
    public const string PaymentRecord = "payment.record";
    public const string LedgerRead = "ledger.read";
    public const string AuditRead = "audit.read";
    public const string MembershipManage = "membership.manage";
    public const string OrganizationManage = "organization.manage";
    public const string JobsDashboard = "jobs.dashboard";
    public const string OrganizationSettings = "organization.settings";
    public const string CatalogManage = "catalog.manage";
    public const string ComplaintCreateBehalf = "complaint.create.behalf";
    public const string HouseLink = "house.link";
    public const string PricingManage = "pricing.manage";
    public const string JobCreate = "job.create";
    public const string PricingRead = "pricing.read";
    public const string EstimateCreate = "estimate.create";
    public const string DashboardRead = "dashboard.read";
    /// <summary>See what jobs cost: cost reports, repeated repairs, spend and overrun widgets (JC-10, DB-07). Counts alone need only dashboard.read.</summary>
    public const string CostRead = "cost.read";
    /// <summary>Receive, adjust, issue and count stock; decide material requests. The storekeeper duty, held by supervisors and managers until a storekeeper role exists.</summary>
    public const string StockManage = "stock.manage";
    /// <summary>Write, schedule and cancel notices to residents, and see who has read them (NT-08).</summary>
    public const string NoticeManage = "notice.manage";

    public static IReadOnlyList<string> All { get; } = typeof(Permissions)
        .GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToArray();
}

/// <summary>The role-to-permission matrix (ID-06). One place to read and test; no permission lives anywhere else.</summary>
public static class RolePermissions
{
    private static readonly Dictionary<Role, HashSet<string>> Matrix = new()
    {
        [Role.Resident] = [Permissions.ComplaintCreate, Permissions.ComplaintReadOwn, Permissions.BillRead],
        [Role.Guard] = [Permissions.GateLog],
        [Role.Technician] = [Permissions.ComplaintRead, Permissions.ComplaintWork, Permissions.PricingRead, Permissions.EstimateCreate],
        [Role.Office] = [Permissions.ComplaintCreate, Permissions.ComplaintCreateBehalf, Permissions.ComplaintRead, Permissions.ComplaintTriage, Permissions.BillRead, Permissions.HouseLink, Permissions.PricingRead, Permissions.DashboardRead],
        [Role.Supervisor] =
        [
            Permissions.ComplaintRead, Permissions.ComplaintTriage, Permissions.ComplaintAssign,
            Permissions.ComplaintClose, Permissions.EstimateApprove, Permissions.ComplaintCreateBehalf, Permissions.JobCreate, Permissions.PricingRead, Permissions.EstimateCreate, Permissions.DashboardRead, Permissions.CostRead, Permissions.StockManage,
        ],
        [Role.Accounts] = [Permissions.BillRead, Permissions.BillManage, Permissions.PaymentRecord, Permissions.LedgerRead, Permissions.PricingManage, Permissions.PricingRead, Permissions.CostRead],
        [Role.Committee] = [Permissions.ComplaintRead, Permissions.PricingRead, Permissions.LedgerRead, Permissions.EstimateApprove, Permissions.AuditRead, Permissions.DashboardRead, Permissions.CostRead, Permissions.NoticeManage],
        [Role.Manager] =
        [
            Permissions.ComplaintRead, Permissions.ComplaintTriage, Permissions.ComplaintAssign, Permissions.ComplaintClose,
            Permissions.EstimateApprove, Permissions.GateManage, Permissions.BillRead, Permissions.LedgerRead,
            Permissions.AuditRead, Permissions.MembershipManage, Permissions.OrganizationSettings, Permissions.CatalogManage,
            Permissions.ComplaintCreateBehalf, Permissions.HouseLink, Permissions.PricingManage, Permissions.PricingRead, Permissions.JobCreate, Permissions.EstimateCreate, Permissions.DashboardRead, Permissions.CostRead, Permissions.StockManage, Permissions.NoticeManage,
        ],
        [Role.Support] = [Permissions.ComplaintRead, Permissions.PricingRead, Permissions.DashboardRead, Permissions.CostRead, Permissions.AuditRead],
        [Role.PlatformAdmin] =
        [
            Permissions.OrganizationManage, Permissions.MembershipManage, Permissions.AuditRead, Permissions.JobsDashboard,
        ],
    };

    public static IReadOnlySet<string> For(Role role) => Matrix[role];

    public static bool Allows(Role role, string permission) => Matrix[role].Contains(permission);
}
