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
        [Role.Technician] = [Permissions.ComplaintRead, Permissions.ComplaintWork],
        [Role.Office] = [Permissions.ComplaintCreate, Permissions.ComplaintRead, Permissions.ComplaintTriage, Permissions.BillRead],
        [Role.Supervisor] =
        [
            Permissions.ComplaintRead, Permissions.ComplaintTriage, Permissions.ComplaintAssign,
            Permissions.ComplaintClose, Permissions.EstimateApprove,
        ],
        [Role.Accounts] = [Permissions.BillRead, Permissions.BillManage, Permissions.PaymentRecord, Permissions.LedgerRead],
        [Role.Committee] = [Permissions.ComplaintRead, Permissions.LedgerRead, Permissions.EstimateApprove, Permissions.AuditRead],
        [Role.Manager] =
        [
            Permissions.ComplaintRead, Permissions.ComplaintTriage, Permissions.ComplaintAssign, Permissions.ComplaintClose,
            Permissions.EstimateApprove, Permissions.GateManage, Permissions.BillRead, Permissions.LedgerRead,
            Permissions.AuditRead, Permissions.MembershipManage,
        ],
        [Role.PlatformAdmin] =
        [
            Permissions.OrganizationManage, Permissions.MembershipManage, Permissions.AuditRead, Permissions.JobsDashboard,
        ],
    };

    public static IReadOnlySet<string> For(Role role) => Matrix[role];

    public static bool Allows(Role role, string permission) => Matrix[role].Contains(permission);
}
