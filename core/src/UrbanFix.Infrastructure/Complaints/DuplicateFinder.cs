using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>
/// Looks for complaints that may be about the same problem (DP-02, DP-03): recent, close by, scored on wording, place, time and category.
/// It only ever suggests. Pairs already suggested, accepted or rejected are not suggested again (DP-04).
/// </summary>
public sealed class DuplicateFinder(AppDbContext db)
{
    private static readonly ComplaintStatus[] Gone = [ComplaintStatus.Closed, ComplaintStatus.Rejected, ComplaintStatus.Merged];

    public static DuplicateScoring.Settings SettingsOf(OrgPolicy p) => new(p.DuplicateRadiusMetres, p.DuplicateWindowDays, p.DuplicateThresholdPercent / 100.0);

    /// <summary>Stores new suggestions for this complaint and returns them. Saves.</summary>
    public async Task<IReadOnlyList<DuplicateSuggestion>> FindAsync(Guid complaintId, CancellationToken ct)
    {
        var complaint = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(c => c.Id == complaintId, ct);
        if (complaint is null || Gone.Contains(complaint.Status)) return [];
        var settings = SettingsOf(OrgPolicy.From(await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct) ?? "{}"));

        var from = complaint.CreatedAt.AddDays(-settings.WindowDays);
        var to = complaint.CreatedAt.AddDays(settings.WindowDays);
        var candidates = await db.Complaints.AsNoTracking()
            .Where(c => c.Id != complaintId && !Gone.Contains(c.Status) && c.CreatedAt >= from && c.CreatedAt <= to
                        && (complaint.IncidentId == null || c.IncidentId != complaint.IncidentId))
            .ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var locationIds = candidates.Select(c => c.LocationId).Append(complaint.LocationId).Where(l => l != null).Select(l => l!.Value).Distinct().ToList();
        var coords = await db.Locations.AsNoTracking().Where(l => locationIds.Contains(l.Id) && l.Latitude != null && l.Longitude != null).ToDictionaryAsync(l => l.Id, l => (l.Latitude!.Value, l.Longitude!.Value), ct);

        var shortlist = new List<(Complaint C, double Hours, double? Metres)>();
        foreach (var c in candidates)
        {
            var samePlace = (complaint.HouseId != null && complaint.HouseId == c.HouseId) || (complaint.LocationId != null && complaint.LocationId == c.LocationId);
            double? metres = samePlace ? 0
                : complaint.LocationId is { } a && c.LocationId is { } b && coords.TryGetValue(a, out var pa) && coords.TryGetValue(b, out var pb) ? Geo.DistanceMetres(pa.Item1, pa.Item2, pb.Item1, pb.Item2) : null;
            var hours = Math.Abs((c.CreatedAt - complaint.CreatedAt).TotalHours);
            if (DuplicateScoring.IsCandidate(hours, metres, samePlace, settings)) shortlist.Add((c, hours, metres));
        }
        if (shortlist.Count == 0) return [];

        var texts = await EmbeddingStore.SimilarityToAsync(db, complaintId, shortlist.Select(s => s.C.Id).ToList(), ct);
        var seen = await db.DuplicateSuggestions.AsNoTracking()
            .Where(s => s.ComplaintAId == complaintId || s.ComplaintBId == complaintId)
            .Select(s => s.ComplaintAId == complaintId ? s.ComplaintBId : s.ComplaintAId).ToListAsync(ct);

        var created = new List<DuplicateSuggestion>();
        foreach (var (c, hours, metres) in shortlist.Where(s => !seen.Contains(s.C.Id)))
        {
            var sameCategory = complaint.CategoryId != null ? complaint.CategoryId == c.CategoryId : complaint.AiCategoryKey != null && complaint.AiCategoryKey == c.AiCategoryKey;
            double? text = texts.TryGetValue(c.Id, out var t) ? t : null;
            var result = DuplicateScoring.Score(text, metres, hours, sameCategory, settings);
            if (result.Score < settings.Threshold) continue;

            var (x, y) = DuplicateScoring.Pair(complaintId, c.Id);
            var suggestion = new DuplicateSuggestion
            {
                OrganizationId = complaint.OrganizationId, ComplaintAId = x, ComplaintBId = y, Score = (float)result.Score, TextSimilarity = text is null ? null : (float)text.Value,
                DistanceMetres = metres, HoursApart = Math.Round(hours, 2), SameCategory = sameCategory, Reason = result.Reason,
            };
            db.DuplicateSuggestions.Add(suggestion);
            created.Add(suggestion);
        }
        if (created.Count > 0) await db.SaveChangesAsync(ct);
        return created;
    }
}

/// <summary>Grouping complaints into incidents (DP-01, DP-04). Complaints are never merged or closed by this: only linked.</summary>
public sealed class IncidentService(AppDbContext db)
{
    private static string TitleFor(Complaint c)
    {
        var text = string.IsNullOrWhiteSpace(c.AiSummary) ? c.Description : c.AiSummary;
        return text.Length > 80 ? text[..80] : text;
    }

    /// <returns>The incident, or an error code: not_found, not_pending, already_in_different_incident.</returns>
    public async Task<(Incident? Incident, string? Error)> AcceptAsync(Guid suggestionId, Guid userId, string? note, CancellationToken ct)
    {
        var suggestion = await db.DuplicateSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion is null) return (null, "not_found");
        if (suggestion.Status != SuggestionStatus.Pending) return (null, "not_pending");

        var a = await db.Complaints.SingleAsync(c => c.Id == suggestion.ComplaintAId, ct);
        var b = await db.Complaints.SingleAsync(c => c.Id == suggestion.ComplaintBId, ct);
        if (a.IncidentId != null && b.IncidentId != null && a.IncidentId != b.IncidentId) return (null, "already_in_different_incident");

        var older = a.CreatedAt <= b.CreatedAt ? a : b;
        var incidentId = a.IncidentId ?? b.IncidentId;
        Incident incident;
        if (incidentId is { } id) incident = await db.Incidents.SingleAsync(i => i.Id == id, ct);
        else
        {
            incident = new Incident { OrganizationId = a.OrganizationId, Title = TitleFor(older), PrimaryComplaintId = older.Id, CreatedBy = userId };
            db.Incidents.Add(incident);
            await db.SaveChangesAsync(ct); // the incident must exist before complaints point at it
        }
        a.IncidentId = incident.Id; b.IncidentId = incident.Id;
        suggestion.Status = SuggestionStatus.Accepted; suggestion.DecidedBy = userId; suggestion.DecidedAt = DateTimeOffset.UtcNow; suggestion.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);
        return (incident, null);
    }

    public async Task<string?> RejectAsync(Guid suggestionId, Guid userId, string? note, CancellationToken ct)
    {
        var suggestion = await db.DuplicateSuggestions.SingleOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion is null) return "not_found";
        if (suggestion.Status != SuggestionStatus.Pending) return "not_pending";
        suggestion.Status = SuggestionStatus.Rejected; suggestion.DecidedBy = userId; suggestion.DecidedAt = DateTimeOffset.UtcNow; suggestion.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>A supervisor groups complaints by hand.</summary>
    public async Task<(Incident? Incident, string? Error)> CreateAsync(IReadOnlyCollection<Guid> complaintIds, string? title, Guid userId, CancellationToken ct)
    {
        var ids = complaintIds.Distinct().ToList();
        if (ids.Count < 2) return (null, "at_least_two_complaints");
        var complaints = await db.Complaints.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        if (complaints.Count != ids.Count) return (null, "complaint_not_found");
        if (complaints.Any(c => c.IncidentId != null)) return (null, "already_in_an_incident");

        var older = complaints.OrderBy(c => c.CreatedAt).First();
        var incident = new Incident { OrganizationId = older.OrganizationId, Title = string.IsNullOrWhiteSpace(title) ? TitleFor(older) : title.Trim(), PrimaryComplaintId = older.Id, CreatedBy = userId };
        db.Incidents.Add(incident);
        await db.SaveChangesAsync(ct);
        foreach (var c in complaints) c.IncidentId = incident.Id;
        await db.SaveChangesAsync(ct);
        return (incident, null);
    }

    public async Task<string?> AddAsync(Guid incidentId, Guid complaintId, CancellationToken ct)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == incidentId, ct);
        var complaint = await db.Complaints.SingleOrDefaultAsync(c => c.Id == complaintId, ct);
        if (incident is null || complaint is null) return "not_found";
        if (complaint.IncidentId == incidentId) return null;
        if (complaint.IncidentId != null) return "already_in_an_incident";
        complaint.IncidentId = incidentId;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Takes a complaint out of its incident. An incident left with one complaint is closed, and the primary moves to the oldest remaining.</summary>
    public async Task<string?> RemoveAsync(Guid incidentId, Guid complaintId, CancellationToken ct)
    {
        var complaint = await db.Complaints.SingleOrDefaultAsync(c => c.Id == complaintId && c.IncidentId == incidentId, ct);
        if (complaint is null) return "not_found";
        complaint.IncidentId = null;
        var incident = await db.Incidents.SingleAsync(i => i.Id == incidentId, ct);
        var rest = await db.Complaints.Where(c => c.IncidentId == incidentId && c.Id != complaintId).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        if (rest.Count < 2) { incident.Status = "closed"; foreach (var c in rest) c.IncidentId = null; }
        else if (incident.PrimaryComplaintId == complaintId) incident.PrimaryComplaintId = rest[0].Id;
        await db.SaveChangesAsync(ct);
        return null;
    }
}
