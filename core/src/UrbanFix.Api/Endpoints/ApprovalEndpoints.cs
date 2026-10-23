using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Requests waiting for a decision and the decisions themselves (ID-10, JC-03).</summary>
public static class ApprovalEndpoints
{
    public sealed record DecisionDto(Guid UserId, bool Approve, string? Note, DateTimeOffset DecidedAt);
    public sealed record ApprovalDto(
        Guid Id, string Kind, Guid? JobId, string? JobNumber, string? Reference, int? EstimateVersion, decimal Amount, Guid RequestedBy, DateTimeOffset RequestedAt,
        DateTimeOffset ExpiresAt, int ApprovalsNeeded, string Status, string? Note, IReadOnlyList<DecisionDto> Decisions);
    public sealed record Decide(bool Approve, string? Note);

    public static void MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/approvals").WithTags("Approvals");
        var policy = AuthPolicies.For(Permissions.EstimateApprove);

        g.MapGet("", async (string? status, AppDbContext db, CancellationToken ct) =>
        {
            var wanted = status ?? ApprovalStatus.Pending;
            var rows = await db.Approvals.AsNoTracking().Where(a => a.Status == wanted).OrderBy(a => a.RequestedAt).Take(200).ToListAsync(ct);
            return Results.Ok(await ToDtosAsync(db, rows, ct));
        }).Produces<List<ApprovalDto>>(200).RequireAuthorization(policy);

        g.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Approvals.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) is { } a ? Results.Ok((await ToDtosAsync(db, [a], ct))[0]) : Results.NotFound())
            .Produces<ApprovalDto>(200).RequireAuthorization(policy);

        g.MapPost("/{id:guid}/decision", async (Guid id, Decide req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, ApprovalService approvals, CancellationToken ct) =>
        {
            var roles = (await memberships.GetAsync(ct)).Select(m => m.Role).ToList();
            var (approval, error) = await approvals.DecideAsync(id, Guid.Parse(user.FindFirstValue("sub")!), roles, req.Approve, req.Note, ct);
            if (error is not null)
                return error switch
                {
                    "not_found" => Results.NotFound(),
                    "reason_required" => Results.BadRequest(new { error }),
                    "not_an_approver" or "requester_cannot_decide" => Results.Json(new { error }, statusCode: 403),
                    _ => Results.Conflict(new { error }),
                };
            await db.SaveChangesAsync(ct);
            return Results.Ok((await ToDtosAsync(db, [approval!], ct))[0]);
        }).Produces<ApprovalDto>(200).RequireAuthorization(policy);
    }

    private static async Task<List<ApprovalDto>> ToDtosAsync(AppDbContext db, List<Approval> rows, CancellationToken ct)
    {
        var ids = rows.Select(a => a.Id).ToList();
        var decisions = (await db.ApprovalDecisions.AsNoTracking().Where(d => ids.Contains(d.ApprovalId)).OrderBy(d => d.DecidedAt).ToListAsync(ct)).ToLookup(d => d.ApprovalId);
        var jobIds = rows.Where(a => a.JobId != null).Select(a => a.JobId!.Value).Distinct().ToList();
        var jobs = await (from j in db.Jobs.AsNoTracking()
                          join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                          where jobIds.Contains(j.Id)
                          select new { j.Id, j.Number, c.Reference }).ToDictionaryAsync(x => x.Id, ct);
        var subjects = rows.Select(a => a.SubjectId).ToList();
        var versions = await db.Estimates.AsNoTracking().Where(e => subjects.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Version, ct);

        return rows.Select(a =>
        {
            var job = a.JobId is { } j && jobs.TryGetValue(j, out var found) ? found : null;
            return new ApprovalDto(a.Id, a.Kind, a.JobId, job?.Number, job?.Reference, versions.TryGetValue(a.SubjectId, out var v) ? v : null, a.Amount, a.RequestedBy,
                a.RequestedAt, a.ExpiresAt, a.ApprovalsNeeded, a.Status, a.Note,
                decisions[a.Id].Select(d => new DecisionDto(d.UserId, d.Approve, d.Note, d.DecidedAt)).ToList());
        }).ToList();
    }
}
