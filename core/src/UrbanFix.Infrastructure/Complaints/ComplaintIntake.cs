using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>A complaint ready to be stored. Who may send it, and what to do with an unknown phone number, are decided before this point.</summary>
public sealed record IntakeRequest(
    Guid OrganizationId, string Channel, string Description, Guid? CategoryId, Guid? HouseId, Guid? LocationId, string? LocationText,
    Guid? ReporterUserId, Guid? EnteredByUserId, string? ReporterPhone, bool ReporterVerified, bool HoldForSender, string? AssetRef, Guid? ActorUserId, double? Latitude = null, double? Longitude = null);

public sealed record IntakeResult(Complaint Complaint, bool NeedsLocation);

/// <summary>
/// Stores a complaint (CP-01, CP-05, CP-06): checks the place, links a resident to their only house, holds the complaint when
/// the place or the sender is unknown, applies priority and deadlines, and records the first history entry and the event.
/// The web, the app, the office entry form and the WhatsApp conversation all create complaints through here.
/// </summary>
public sealed class ComplaintIntake(AppDbContext db, NumberGenerator numbers, IEventBus bus, ComplaintPlanner planner)
{
    /// <returns>The stored complaint, or an error code (category_not_found, house_not_found, location_not_found).</returns>
    public async Task<(IntakeResult? Result, string? Error)> CreateAsync(IntakeRequest req, CancellationToken ct)
    {
        Category? category = null;
        if (req.CategoryId is { } cid)
        {
            category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid && c.IsActive, ct);
            if (category is null) return (null, "category_not_found");
        }

        var houseId = req.HouseId;
        var locationId = req.LocationId;
        if (houseId is { } hid)
        {
            var house = await db.Houses.AsNoTracking().SingleOrDefaultAsync(h => h.Id == hid, ct);
            if (house is null) return (null, "house_not_found");
            locationId ??= house.LocationId;
        }
        if (locationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid, ct)) return (null, "location_not_found");

        // A resident linked to exactly one current house does not have to say where they live.
        if (houseId is null && locationId is null && req.ReporterUserId is { } who)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var links = await db.ResidentLinks.AsNoTracking().Where(l => l.UserId == who && (l.ToDate == null || l.ToDate >= today)).Select(l => l.HouseId).Distinct().ToListAsync(ct);
            if (links.Count == 1)
            {
                houseId = links[0];
                locationId = await db.Houses.AsNoTracking().Where(h => h.Id == houseId).Select(h => (Guid?)h.LocationId).SingleOrDefaultAsync(ct);
            }
        }

        var complaint = new Complaint
        {
            OrganizationId = req.OrganizationId,
            Reference = await numbers.NextAsync(NumberGenerator.ComplaintScope, "UF", ct),
            Channel = req.Channel,
            Description = req.Description.Trim(),
            CategoryId = category?.Id,
            Priority = category?.DefaultPriority ?? "Normal",
            HouseId = houseId,
            LocationId = locationId,
            LocationText = string.IsNullOrWhiteSpace(req.LocationText) ? null : req.LocationText.Trim(),
            ReportedByUserId = req.ReporterUserId,
            EnteredByUserId = req.EnteredByUserId,
            Latitude = req.Latitude, Longitude = req.Longitude,
            ReporterPhone = req.ReporterPhone, ReporterVerified = req.ReporterVerified, AssetRef = req.AssetRef,
        };

        // CP-05: without a house or map location the complaint is held and the resident is asked for it.
        var needsLocation = complaint.HouseId is null && complaint.LocationId is null;
        if (needsLocation)
        {
            complaint.Status = ComplaintStatus.OnHold;
            complaint.HoldReason = "location_missing";
        }
        else if (req.HoldForSender)
        {
            complaint.Status = ComplaintStatus.OnHold;
            complaint.HoldReason = "sender_unverified";
        }

        await planner.ApplyIntakeAsync(complaint, category, ct);
        db.Complaints.Add(complaint);
        db.ComplaintHistory.Add(new ComplaintStatusHistory
        {
            OrganizationId = req.OrganizationId, ComplaintId = complaint.Id, ToStatus = complaint.Status, ActorUserId = req.ActorUserId,
            Reason = needsLocation ? "Location missing: street number or map pin requested" : req.HoldForSender ? "Sender not in the directory: check before review" : null,
        });
        bus.Publish(new ComplaintCreated(req.OrganizationId, complaint.Id, complaint.Reference));
        await db.SaveChangesAsync(ct);
        return (new IntakeResult(complaint, needsLocation), null);
    }
}
