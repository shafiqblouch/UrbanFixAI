using System.Text.Json;
using System.Text.Json.Nodes;

namespace UrbanFix.OrgSetup;

/// <summary>What happens to a message from a number the directory does not know (CP-08).</summary>
public static class UnknownSenderPolicy
{
    public const string UnverifiedQueue = "unverified_queue";
    public const string Guest = "guest";
    public const string OtpRegistration = "otp_registration";
    public static readonly string[] All = [UnverifiedQueue, Guest, OtpRegistration];
}

/// <summary>Per-organization rules kept in the settings JSON (CP-08, CP-10).</summary>
public sealed record OrgPolicy(string UnknownSender, bool ClosureNeedsResident, bool AutoConfirm, int AutoConfirmDays, int ReminderDays, int AiMonthlyTokenCap = 2_000_000,
    decimal EstimateApprovalLimit = 0, decimal SecondApproverAbove = 0, int ApprovalExpiryHours = 72, int EstimateWaitHours = 24, int OverrunPercent = 20,
    int RepeatedRepairCount = 3, int RepeatedRepairMonths = 6,
    int DuplicateRadiusMetres = 300, int DuplicateWindowDays = 7, int DuplicateThresholdPercent = 70)
{
    public static readonly OrgPolicy Default = new(UnknownSenderPolicy.UnverifiedQueue, true, false, 3, 1);

    public static OrgPolicy From(string settingsJson)
    {
        try
        {
            if (JsonNode.Parse(settingsJson)?["policy"] is not JsonObject p) return Default;
            return new OrgPolicy(
                p["unknownSender"]?.GetValue<string>() is { } u && UnknownSenderPolicy.All.Contains(u) ? u : Default.UnknownSender,
                p["closureNeedsResident"]?.GetValue<bool>() ?? Default.ClosureNeedsResident,
                p["autoConfirm"]?.GetValue<bool>() ?? Default.AutoConfirm,
                p["autoConfirmDays"]?.GetValue<int>() ?? Default.AutoConfirmDays,
                p["reminderDays"]?.GetValue<int>() ?? Default.ReminderDays,
                p["aiMonthlyTokenCap"]?.GetValue<int>() ?? Default.AiMonthlyTokenCap,
                p["estimateApprovalLimit"]?.GetValue<decimal>() ?? Default.EstimateApprovalLimit,
                p["secondApproverAbove"]?.GetValue<decimal>() ?? Default.SecondApproverAbove,
                p["approvalExpiryHours"]?.GetValue<int>() ?? Default.ApprovalExpiryHours,
                p["estimateWaitHours"]?.GetValue<int>() ?? Default.EstimateWaitHours,
                p["overrunPercent"]?.GetValue<int>() ?? Default.OverrunPercent,
                p["repeatedRepairCount"]?.GetValue<int>() ?? Default.RepeatedRepairCount,
                p["repeatedRepairMonths"]?.GetValue<int>() ?? Default.RepeatedRepairMonths,
                p["duplicateRadiusMetres"]?.GetValue<int>() ?? Default.DuplicateRadiusMetres,
                p["duplicateWindowDays"]?.GetValue<int>() ?? Default.DuplicateWindowDays,
                p["duplicateThresholdPercent"]?.GetValue<int>() ?? Default.DuplicateThresholdPercent);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return Default; }
    }

    /// <summary>Returns the settings JSON with this policy stored under "policy", leaving other settings alone.</summary>
    public string WriteInto(string settingsJson)
    {
        JsonObject root;
        try { root = JsonNode.Parse(settingsJson) as JsonObject ?? new JsonObject(); } catch (JsonException) { root = new JsonObject(); }
        root["policy"] = new JsonObject
        {
            ["unknownSender"] = UnknownSender, ["closureNeedsResident"] = ClosureNeedsResident, ["autoConfirm"] = AutoConfirm,
            ["autoConfirmDays"] = AutoConfirmDays, ["reminderDays"] = ReminderDays, ["aiMonthlyTokenCap"] = AiMonthlyTokenCap,
            ["estimateApprovalLimit"] = EstimateApprovalLimit, ["secondApproverAbove"] = SecondApproverAbove,
            ["approvalExpiryHours"] = ApprovalExpiryHours, ["estimateWaitHours"] = EstimateWaitHours, ["overrunPercent"] = OverrunPercent,
            ["repeatedRepairCount"] = RepeatedRepairCount, ["repeatedRepairMonths"] = RepeatedRepairMonths,
            ["duplicateRadiusMetres"] = DuplicateRadiusMetres, ["duplicateWindowDays"] = DuplicateWindowDays, ["duplicateThresholdPercent"] = DuplicateThresholdPercent,
        };
        return root.ToJsonString();
    }

    public string? Validate()
    {
        if (!UnknownSenderPolicy.All.Contains(UnknownSender)) return "unknown_sender_policy";
        if (AutoConfirmDays is < 1 or > 30) return "auto_confirm_days_out_of_range";
        if (ReminderDays < 1 || ReminderDays >= AutoConfirmDays) return "reminder_must_come_before_close";
        // 0 switches the AI off for this organization: complaints then use the rules-only mode.
        if (AiMonthlyTokenCap is < 0 or > 1_000_000_000) return "ai_cap_out_of_range";
        // An estimate limit of 0 means no approval is asked for; a second approver needs the first limit to be on.
        if (EstimateApprovalLimit < 0 || SecondApproverAbove < 0) return "amount_cannot_be_negative";
        if (SecondApproverAbove > 0 && (EstimateApprovalLimit <= 0 || SecondApproverAbove <= EstimateApprovalLimit)) return "second_approver_limit_must_exceed_first";
        if (ApprovalExpiryHours is < 1 or > 24 * 30) return "approval_expiry_out_of_range";
        if (EstimateWaitHours is < 1 or > 24 * 14) return "estimate_wait_out_of_range";
        if (OverrunPercent is < 0 or > 1000) return "overrun_percent_out_of_range";
        if (RepeatedRepairCount is < 2 or > 100) return "repeated_repair_count_out_of_range";
        if (RepeatedRepairMonths is < 1 or > 60) return "repeated_repair_months_out_of_range";
        if (DuplicateRadiusMetres is < 10 or > 5000) return "duplicate_radius_out_of_range";
        if (DuplicateWindowDays is < 1 or > 60) return "duplicate_window_out_of_range";
        if (DuplicateThresholdPercent is < 30 or > 99) return "duplicate_threshold_out_of_range";
        return null;
    }
}
