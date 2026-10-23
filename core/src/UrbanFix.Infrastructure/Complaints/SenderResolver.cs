using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

public enum SenderKind
{
    /// <summary>The number belongs to a resident in the directory.</summary>
    Matched,
    /// <summary>Unknown number; the complaint is held for staff to check (CP-08).</summary>
    UnverifiedQueue,
    /// <summary>Unknown number; the complaint goes ahead but is marked as from a guest.</summary>
    Guest,
    /// <summary>Unknown number; the sender must register with a code first.</summary>
    RegistrationRequired,
}

public sealed record SenderDecision(SenderKind Kind, Guid? UserId);

/// <summary>Decides what to do with a complaint that arrives from a phone number, using the organization's policy (CP-08).</summary>
public static class SenderResolver
{
    public static async Task<SenderDecision> ResolveAsync(AppDbContext db, string phone, OrgPolicy policy, CancellationToken ct)
    {
        // Row-level security limits users to this organization's members, so a match is always one of ours.
        var match = await (from u in db.Users.AsNoTracking()
                           join m in db.Memberships.AsNoTracking() on u.Id equals m.UserId
                           where u.Phone == phone && u.IsActive && m.IsActive && m.Role == Role.Resident
                           select u.Id).Cast<Guid?>().FirstOrDefaultAsync(ct);
        if (match is not null) return new SenderDecision(SenderKind.Matched, match);
        return policy.UnknownSender switch
        {
            UnknownSenderPolicy.Guest => new SenderDecision(SenderKind.Guest, null),
            UnknownSenderPolicy.OtpRegistration => new SenderDecision(SenderKind.RegistrationRequired, null),
            _ => new SenderDecision(SenderKind.UnverifiedQueue, null),
        };
    }
}

/// <summary>What a resident may be told about a complaint when they prove who they are by phone (CP-09).</summary>
public sealed record StatusView(string Reference, ComplaintStatus Status, string? StatusReason, DateTimeOffset UpdatedAt);

public static class ComplaintLookup
{
    /// <summary>
    /// The status of a complaint, but only to the person who reported it. <paramref name="verifiedPhone"/> must already be proven
    /// (an OTP, or the WhatsApp sender). A wrong number gets the same answer as a reference that does not exist: nothing.
    /// </summary>
    public static async Task<StatusView?> StatusByPhoneAsync(AppDbContext db, string reference, string verifiedPhone, CancellationToken ct)
    {
        var row = await (from c in db.Complaints.AsNoTracking()
                         join u in db.Users.AsNoTracking() on c.ReportedByUserId equals u.Id into reporters
                         from u in reporters.DefaultIfEmpty()
                         where c.Reference == reference && (c.ReporterPhone == verifiedPhone || (u != null && u.Phone == verifiedPhone))
                         select new StatusView(c.Reference, c.Status, c.StatusReason, c.UpdatedAt)).FirstOrDefaultAsync(ct);
        return row;
    }
}
