using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Possible duplicates for a supervisor to confirm or dismiss, and incidents that group complaints (DP-01 to DP-05).</summary>
public static class IncidentEndpoints
{
    public sealed record SuggestionDto(
        Guid Id, Guid ComplaintId, string Reference, string Description, DateTimeOffset CreatedAt, float Score, float? TextSimilarity, double? DistanceMetres,
        double HoursApart, bool SameCategory, string Reason, string Status);
    public sealed record SuggestionDecision(bool Accept, string? Note);
    public sealed record IncidentComplaint(Guid Id, string Reference, string Status, Guid? ReportedByUserId, bool IsPrimary);
    public sealed record IncidentDto(Guid Id, string Title, string Status, Guid? PrimaryComplaintId, DateTimeOffset CreatedAt, IReadOnlyList<IncidentComplaint> Complaints);
    public sealed record CreateIncident(IReadOnlyList<Guid> ComplaintIds, string? Title);
    public sealed record AddToIncident(Guid ComplaintId);

    public static void MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Incidents");
        var triage = AuthPolicies.For(Permissions.ComplaintTriage);
        var read = AuthPolicies.For(Permissions.ComplaintRead);

        g.MapGet("/complaints/{id:guid}/duplicates", async (Guid id, string? status, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Complaints.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            return Results.Ok(await SuggestionsAsync(db, id, status ?? SuggestionStatus.Pending, ct));
        }).Produces<List<SuggestionDto>>(200).RequireAuthorization(triage);

        g.MapPost("/complaints/{id:guid}/duplicate-check", async (Guid id, DuplicateFinder finder, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Complaints.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
            await finder.FindAsync(id, ct);
            return Results.Ok(await SuggestionsAsync(db, id, SuggestionStatus.Pending, ct));
        }).Produces<List<SuggestionDto>>(200).RequireAuthorization(triage);

        g.MapPost("/duplicate-suggestions/{id:guid}/decision", async (Guid id, SuggestionDecision req, ClaimsPrincipal user, IncidentService incidents, AppDbContext db, CancellationToken ct) =>
        {
            var by = Guid.Parse(user.FindFirstValue("sub")!);
            if (!req.Accept)
            {
                var error = await incidents.RejectAsync(id, by, req.Note, ct);
                return error is null ? Results.NoContent() : Failure(error);
            }
            var (incident, accepted) = await incidents.AcceptAsync(id, by, req.Note, ct);
            return accepted is null ? Results.Ok((await IncidentsAsync(db, [incident!.Id], ct))[0]) : Failure(accepted);
        }).Produces<IncidentDto>(200).RequireAuthorization(triage);

        g.MapGet("/incidents", async (string? status, AppDbContext db, CancellationToken ct) =>
        {
            var wanted = status ?? "open";
            var ids = await db.Incidents.AsNoTracking().Where(i => i.Status == wanted).OrderByDescending(i => i.CreatedAt).Take(100).Select(i => i.Id).ToListAsync(ct);
            return Results.Ok(await IncidentsAsync(db, ids, ct));
        }).Produces<List<IncidentDto>>(200).RequireAuthorization(read);

        g.MapGet("/incidents/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            (await IncidentsAsync(db, [id], ct)) is [var one] ? Results.Ok(one) : Results.NotFound()).Produces<IncidentDto>(200).RequireAuthorization(read);

        g.MapPost("/incidents", async (CreateIncident req, ClaimsPrincipal user, IncidentService incidents, AppDbContext db, CancellationToken ct) =>
        {
            var (incident, error) = await incidents.CreateAsync(req.ComplaintIds, req.Title, Guid.Parse(user.FindFirstValue("sub")!), ct);
            return error is null ? Results.Created($"/incidents/{incident!.Id}", (await IncidentsAsync(db, [incident.Id], ct))[0]) : Failure(error);
        }).Produces<IncidentDto>(201).RequireAuthorization(triage);

        g.MapPost("/incidents/{id:guid}/complaints", async (Guid id, AddToIncident req, IncidentService incidents, AppDbContext db, CancellationToken ct) =>
        {
            var error = await incidents.AddAsync(id, req.ComplaintId, ct);
            return error is null ? Results.Ok((await IncidentsAsync(db, [id], ct))[0]) : Failure(error);
        }).Produces<IncidentDto>(200).RequireAuthorization(triage);

        g.MapDelete("/incidents/{id:guid}/complaints/{complaintId:guid}", async (Guid id, Guid complaintId, IncidentService incidents, CancellationToken ct) =>
        {
            var error = await incidents.RemoveAsync(id, complaintId, ct);
            return error is null ? Results.NoContent() : Failure(error);
        }).RequireAuthorization(triage);
    }

    private static IResult Failure(string error) => error switch
    {
        "not_found" or "complaint_not_found" => Results.NotFound(new { error }),
        "not_pending" or "already_in_different_incident" or "already_in_an_incident" => Results.Conflict(new { error }),
        _ => Results.BadRequest(new { error }),
    };

    private static async Task<List<SuggestionDto>> SuggestionsAsync(AppDbContext db, Guid complaintId, string status, CancellationToken ct)
    {
        var rows = await db.DuplicateSuggestions.AsNoTracking().Where(s => (s.ComplaintAId == complaintId || s.ComplaintBId == complaintId) && s.Status == status).OrderByDescending(s => s.Score).ToListAsync(ct);
        var otherIds = rows.Select(s => s.ComplaintAId == complaintId ? s.ComplaintBId : s.ComplaintAId).ToList();
        var others = await db.Complaints.AsNoTracking().Where(c => otherIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        return rows.Select(s =>
        {
            var o = others[s.ComplaintAId == complaintId ? s.ComplaintBId : s.ComplaintAId];
            return new SuggestionDto(s.Id, o.Id, o.Reference, o.Description, o.CreatedAt, s.Score, s.TextSimilarity, s.DistanceMetres, s.HoursApart, s.SameCategory, s.Reason, s.Status);
        }).ToList();
    }

    private static async Task<List<IncidentDto>> IncidentsAsync(AppDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var incidents = await db.Incidents.AsNoTracking().Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        var members = (await db.Complaints.AsNoTracking().Where(c => c.IncidentId != null && ids.Contains(c.IncidentId.Value)).OrderBy(c => c.CreatedAt).ToListAsync(ct)).ToLookup(c => c.IncidentId!.Value);
        return ids.Select(id => incidents.FirstOrDefault(i => i.Id == id)).Where(i => i is not null).Select(i => new IncidentDto(i!.Id, i.Title, i.Status, i.PrimaryComplaintId, i.CreatedAt,
            members[i.Id].Select(c => new IncidentComplaint(c.Id, c.Reference, c.Status.ToString(), c.ReportedByUserId, c.Id == i.PrimaryComplaintId)).ToList())).ToList();
    }
}
