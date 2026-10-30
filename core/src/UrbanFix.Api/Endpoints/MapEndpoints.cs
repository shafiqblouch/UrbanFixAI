using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Api.Endpoints;

/// <summary>The map view: complaint pins and where technicians last reported from.</summary>
public static class MapEndpoints
{
    public sealed record ComplaintPin(Guid Id, string Reference, ComplaintStatus Status, string Priority, string Description, string? LocationText, double Latitude, double Longitude, DateTimeOffset CreatedAt);
    public sealed record TechnicianPin(Guid JobId, string JobNumber, string JobStatus, Guid ComplaintId, string Reference, Guid? UserId, string? TechnicianName, string Kind, double Latitude, double Longitude, DateTimeOffset At);
    public sealed record MapDto(IReadOnlyList<ComplaintPin> Complaints, IReadOnlyList<TechnicianPin> Technicians, int ComplaintsWithoutPosition);

    private const int MaxPins = 1000;

    public static void MapMapEndpoints(this IEndpointRouteBuilder app)
    {
        // Same filters and the same visibility rules as the complaint list, so the map can never show more than the list does.
        app.MapGet("/map", GetAsync).WithTags("Map").Produces<MapDto>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, ComplaintStatus? status, string? q, Guid? categoryId, Guid? departmentId,
        Guid? locationId, string? priority, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var (query, error) = await FilteredAsync(actor, db, status, q, categoryId, departmentId, locationId, priority, from, to, ct);
        if (error is not null) return error;
        query = query!;

        var withoutPosition = await query.CountAsync(c => c.Latitude == null || c.Longitude == null, ct);
        var complaints = await query.Where(c => c.Latitude != null && c.Longitude != null)
            .OrderByDescending(c => c.CreatedAt).Take(MaxPins)
            .Select(c => new ComplaintPin(c.Id, c.Reference, c.Status, c.Priority, c.Description.Length > 160 ? c.Description.Substring(0, 160) : c.Description, c.LocationText, c.Latitude!.Value, c.Longitude!.Value, c.CreatedAt))
            .ToListAsync(ct);

        // The latest position each technician sent on a job that is still being worked on (start, note with location).
        var jobs = db.Jobs.AsNoTracking().Where(j => JobStatus.Active.Contains(j.Status));
        if (!actor.SeesAllJobs) jobs = jobs.Where(j => j.AssignedUserId == actor.UserId);
        var latest = await (from j in jobs
                            join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                            let u = db.JobUpdates.Where(x => x.JobId == j.Id && x.Latitude != null && x.Longitude != null).OrderByDescending(x => x.CreatedAt).FirstOrDefault()
                            where u != null
                            select new { j.Id, j.Number, j.Status, j.ComplaintId, c.Reference, j.AssignedUserId, u.Kind, Lat = u.Latitude!.Value, Lon = u.Longitude!.Value, u.CreatedAt })
            .OrderByDescending(x => x.CreatedAt).Take(MaxPins).ToListAsync(ct);
        var ids = latest.Select(x => x.AssignedUserId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var technicians = latest.Select(x => new TechnicianPin(x.Id, x.Number, x.Status, x.ComplaintId, x.Reference, x.AssignedUserId,
            x.AssignedUserId is { } uid && names.TryGetValue(uid, out var n) ? n : null, x.Kind, x.Lat, x.Lon, x.CreatedAt)).ToList();

        return Results.Ok(new MapDto(complaints, technicians, withoutPosition));
    }
}
