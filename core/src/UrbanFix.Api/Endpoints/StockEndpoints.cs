using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Api.Endpoints;

/// <summary>The store: stock ledger, issues to jobs, counts, low-stock thresholds and material requests (JC-06 to JC-09, TA-09).</summary>
public static class StockEndpoints
{
    public sealed record StockLevelDto(Guid ItemId, string Item, string Unit, decimal OnHand, decimal? LowThreshold, bool Low);
    public sealed record MovementDto(Guid Id, Guid ItemId, string Kind, decimal Quantity, Guid? JobId, Guid? RequestId, string Reason, Guid UserId, DateTimeOffset CreatedAt);
    public sealed record MovementResult(MovementDto Movement, decimal OnHand);

    public sealed record Receive(Guid ItemId, decimal Quantity, string? Reason);
    public sealed record Adjust(Guid ItemId, decimal Quantity, string Reason);
    public sealed record IssueOrReturn(Guid JobId, Guid ItemId, decimal Quantity, string? Note);
    public sealed record Threshold(decimal LowThreshold);

    public sealed record CountLineInput(Guid ItemId, decimal Counted, string? Reason);
    public sealed record SaveCount(string? Note, IReadOnlyList<CountLineInput> Lines);
    public sealed record CountLineDto(Guid ItemId, decimal Expected, decimal Counted, decimal Difference, string? Reason);
    public sealed record CountDto(Guid Id, Guid CountedBy, string? Note, DateTimeOffset CountedAt, IReadOnlyList<CountLineDto> Lines);

    public sealed record RequestLineInput(Guid ItemId, decimal Quantity);
    public sealed record SaveRequest(Guid JobId, string? Note, IReadOnlyList<RequestLineInput> Lines);
    public sealed record RequestLineDto(Guid ItemId, string Item, string Unit, decimal Quantity, decimal OnHand);
    public sealed record RequestDto(Guid Id, Guid JobId, string JobNumber, Guid RequestedBy, string Status, string? Note, DateTimeOffset CreatedAt, string? DecisionNote, IReadOnlyList<RequestLineDto> Lines);
    public sealed record Fulfil(IReadOnlyList<RequestLineInput>? Lines);
    public sealed record Reason(string? Text);

    private static MovementDto Dto(StockMovement m) => new(m.Id, m.ItemId, m.Kind, m.Quantity, m.JobId, m.RequestId, m.Reason, m.UserId, m.CreatedAt);

    public static void MapStockEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/stock").WithTags("Stock");
        var read = AuthPolicies.For(Permissions.PricingRead);
        var manage = AuthPolicies.For(Permissions.StockManage);
        var work = AuthPolicies.For(Permissions.ComplaintWork);

        g.MapGet("", async (AppDbContext db, StockService stock, CancellationToken ct) =>
        {
            var items = await db.PriceItems.AsNoTracking().Where(i => i.IsActive).OrderBy(i => i.Name).ToListAsync(ct);
            var onHand = await stock.OnHandAsync(ct);
            var thresholds = await db.StockSettings.AsNoTracking().ToDictionaryAsync(s => s.ItemId, s => s.LowThreshold, ct);
            return Results.Ok(items.Select(i =>
            {
                var qty = onHand.GetValueOrDefault(i.Id);
                var t = thresholds.TryGetValue(i.Id, out var th) ? th : (decimal?)null;
                return new StockLevelDto(i.Id, i.Name, i.Unit, qty, t, t is > 0 && qty <= t);
            }).ToList());
        }).Produces<List<StockLevelDto>>(200).RequireAuthorization(read);

        g.MapGet("/items/{id:guid}/movements", async (Guid id, int? limit, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.StockMovements.AsNoTracking().Where(m => m.ItemId == id).OrderByDescending(m => m.CreatedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct)).Select(Dto)))
            .Produces<List<MovementDto>>(200).RequireAuthorization(manage);

        g.MapPut("/items/{id:guid}/threshold", async (Guid id, Threshold req, ITenantContext tenant, AppDbContext db, StockService stock, CancellationToken ct) =>
        {
            if (req.LowThreshold < 0) return Results.BadRequest(new { error = "threshold_cannot_be_negative" });
            if (!await db.PriceItems.AnyAsync(i => i.Id == id, ct)) return Results.NotFound();
            var setting = await db.StockSettings.SingleOrDefaultAsync(s => s.ItemId == id, ct);
            if (setting is null) { setting = new StockSetting { OrganizationId = tenant.OrganizationId!.Value, ItemId = id }; db.StockSettings.Add(setting); }
            setting.LowThreshold = req.LowThreshold;
            // Already at or below the new limit: the alert state follows, but a threshold change by itself does not send one.
            setting.LowAlertedAt = StockRules.NextAlert(await stock.OnHandAsync(id, ct), req.LowThreshold, false).Alerted ? DateTimeOffset.UtcNow : null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new Threshold(setting.LowThreshold));
        }).Produces<Threshold>(200).RequireAuthorization(manage);

        g.MapPost("/receipts", (Receive req, ClaimsPrincipal user, StockService stock, CancellationToken ct) =>
            MoveAsync(stock, req.ItemId, StockKinds.Receive, req.Quantity, req.Reason, user, null, ct)).Produces<MovementResult>(201).RequireAuthorization(manage);

        g.MapPost("/adjustments", (Adjust req, ClaimsPrincipal user, StockService stock, CancellationToken ct) =>
            MoveAsync(stock, req.ItemId, StockKinds.Adjust, req.Quantity, req.Reason, user, null, ct)).Produces<MovementResult>(201).RequireAuthorization(manage);

        g.MapPost("/issues", (IssueOrReturn req, ClaimsPrincipal user, StockService stock, CancellationToken ct) =>
            MoveAsync(stock, req.ItemId, StockKinds.Issue, -Math.Abs(req.Quantity), req.Note, user, req.JobId, ct)).Produces<MovementResult>(201).RequireAuthorization(manage);

        g.MapPost("/returns", (IssueOrReturn req, ClaimsPrincipal user, StockService stock, CancellationToken ct) =>
            MoveAsync(stock, req.ItemId, StockKinds.Return, Math.Abs(req.Quantity), req.Note, user, req.JobId, ct)).Produces<MovementResult>(201).RequireAuthorization(manage);

        g.MapGet("/jobs/{jobId:guid}/materials", async (Guid jobId, AppDbContext db, CancellationToken ct) =>
            await db.Jobs.AnyAsync(j => j.Id == jobId, ct) ? Results.Ok(await MaterialsReport.ForJobAsync(db, jobId, ct)) : Results.NotFound())
            .Produces<List<MaterialLine>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));

        // JC-09 ----------------------------------------------------------------------------------------------------
        g.MapPost("/counts", CountAsync).Produces<CountDto>(201).RequireAuthorization(manage);
        g.MapGet("/counts", async (AppDbContext db, CancellationToken ct) =>
        {
            var counts = await db.StockCounts.AsNoTracking().OrderByDescending(c => c.CountedAt).Take(50).ToListAsync(ct);
            var ids = counts.Select(c => c.Id).ToList();
            var lines = (await db.StockCountLines.AsNoTracking().Where(l => ids.Contains(l.CountId)).ToListAsync(ct)).ToLookup(l => l.CountId);
            return Results.Ok(counts.Select(c => new CountDto(c.Id, c.CountedBy, c.Note, c.CountedAt, lines[c.Id].Select(l => new CountLineDto(l.ItemId, l.Expected, l.Counted, l.Counted - l.Expected, l.Reason)).ToList())));
        }).Produces<List<CountDto>>(200).RequireAuthorization(manage);

        // TA-09 ----------------------------------------------------------------------------------------------------
        g.MapPost("/requests", RequestAsync).Produces<RequestDto>(201).RequireAuthorization(work);
        g.MapGet("/requests", ListRequestsAsync).Produces<List<RequestDto>>(200).RequireAuthorization(read);
        g.MapPost("/requests/{id:guid}/fulfil", FulfilAsync).Produces<RequestDto>(200).RequireAuthorization(manage);
        g.MapPost("/requests/{id:guid}/reject", RejectAsync).Produces<RequestDto>(200).RequireAuthorization(manage);
        g.MapPost("/requests/{id:guid}/cancel", CancelAsync).Produces<RequestDto>(200).RequireAuthorization(work);
    }

    private static async Task<IResult> MoveAsync(StockService stock, Guid itemId, string kind, decimal quantity, string? reason, ClaimsPrincipal user, Guid? jobId, CancellationToken ct)
    {
        var (movement, error, available) = await stock.RecordAsync(itemId, kind, quantity, reason, Guid.Parse(user.FindFirstValue("sub")!), jobId, null, null, ct);
        if (error is not null) return Failure(error, available);
        return Results.Created($"/stock/items/{itemId}/movements", new MovementResult(Dto(movement!), available ?? 0));
    }

    private static IResult Failure(string error, decimal? available) => error switch
    {
        "item_not_found" or "job_not_found" => Results.NotFound(new { error }),
        "insufficient_stock" or "more_than_issued" or "job_not_active" => Results.Conflict(new { error, available }),
        _ => Results.BadRequest(new { error }),
    };

    // Counts ----------------------------------------------------------------------------------------------------------

    /// <summary>A physical count: each difference from the books becomes an adjustment with its reason, all together or not at all (JC-09).</summary>
    private static async Task<IResult> CountAsync(SaveCount req, ClaimsPrincipal principal, ITenantContext tenant, AppDbContext db, StockService stock, CancellationToken ct)
    {
        if (req.Lines.Count == 0) return Results.BadRequest(new { error = "lines_required" });
        if (req.Lines.Select(l => l.ItemId).Distinct().Count() != req.Lines.Count) return Results.BadRequest(new { error = "item_counted_twice" });
        var user = Guid.Parse(principal.FindFirstValue("sub")!);

        return await stock.InTransactionAsync(async () =>
        {
            var count = new StockCount { OrganizationId = tenant.OrganizationId!.Value, CountedBy = user, Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim() };
            var onHand = await stock.OnHandAsync(ct);
            var lines = new List<StockCountLine>();
            var missing = new List<Guid>();
            foreach (var l in req.Lines)
            {
                var expected = onHand.GetValueOrDefault(l.ItemId);
                if (StockRules.CheckCount(expected, l.Counted, l.Reason) is { } problem)
                {
                    if (problem == "reason_required") { missing.Add(l.ItemId); continue; }
                    return Results.BadRequest(new { error = problem, itemId = l.ItemId });
                }
                if (!await db.PriceItems.AnyAsync(i => i.Id == l.ItemId, ct)) return Results.BadRequest(new { error = "item_not_found", itemId = l.ItemId });
                lines.Add(new StockCountLine { OrganizationId = count.OrganizationId, CountId = count.Id, ItemId = l.ItemId, Expected = expected, Counted = l.Counted, Reason = string.IsNullOrWhiteSpace(l.Reason) ? null : l.Reason.Trim() });
            }
            if (missing.Count > 0) return Results.BadRequest(new { error = "reason_required", itemIds = missing });

            db.StockCounts.Add(count);
            await db.SaveChangesAsync(ct);
            db.StockCountLines.AddRange(lines);
            await db.SaveChangesAsync(ct);
            foreach (var line in lines.Where(l => l.Counted != l.Expected))
            {
                var (_, error, _) = await stock.RecordAsync(line.ItemId, StockKinds.Adjust, line.Counted - line.Expected, $"Stock count: {line.Reason}", user, null, null, count.Id, ct);
                if (error is not null) return Results.Conflict(new { error, itemId = line.ItemId });
            }
            return Results.Created($"/stock/counts/{count.Id}", new CountDto(count.Id, user, count.Note, count.CountedAt, lines.Select(l => new CountLineDto(l.ItemId, l.Expected, l.Counted, l.Counted - l.Expected, l.Reason)).ToList()));
        }, ct);
    }

    // Requests --------------------------------------------------------------------------------------------------------

    private static async Task<List<RequestDto>> RequestDtosAsync(AppDbContext db, StockService stock, List<StockRequest> requests, CancellationToken ct)
    {
        var ids = requests.Select(r => r.Id).ToList();
        var lines = (await db.StockRequestLines.AsNoTracking().Where(l => ids.Contains(l.RequestId)).ToListAsync(ct)).ToLookup(l => l.RequestId);
        var itemIds = lines.SelectMany(g => g).Select(l => l.ItemId).Distinct().ToList();
        var items = await db.PriceItems.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var onHand = await stock.OnHandAsync(ct);
        var jobs = await db.Jobs.AsNoTracking().Where(j => requests.Select(r => r.JobId).Contains(j.Id)).ToDictionaryAsync(j => j.Id, j => j.Number, ct);
        return requests.Select(r => new RequestDto(r.Id, r.JobId, jobs.GetValueOrDefault(r.JobId, ""), r.RequestedBy, r.Status, r.Note, r.CreatedAt, r.DecisionNote,
            lines[r.Id].Select(l => new RequestLineDto(l.ItemId, items[l.ItemId].Name, items[l.ItemId].Unit, l.Quantity, onHand.GetValueOrDefault(l.ItemId))).ToList())).ToList();
    }

    private static async Task<IResult> RequestAsync(SaveRequest req, ClaimsPrincipal principal, ITenantContext tenant, AppDbContext db, StockService stock, IEventBus bus, CancellationToken ct)
    {
        var user = Guid.Parse(principal.FindFirstValue("sub")!);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == req.JobId, ct);
        // A technician asks for materials only for a job that is theirs and still being worked on.
        if (job is null || job.AssignedUserId != user) return Results.NotFound();
        if (!JobStatus.IsActive(job.Status)) return Results.Conflict(new { error = "job_not_active", status = job.Status });
        if (req.Lines.Count == 0) return Results.BadRequest(new { error = "lines_required" });
        if (req.Lines.Any(l => l.Quantity <= 0)) return Results.BadRequest(new { error = "quantity_must_be_positive" });
        var itemIds = req.Lines.Select(l => l.ItemId).Distinct().ToList();
        if (await db.PriceItems.CountAsync(i => itemIds.Contains(i.Id) && i.IsActive, ct) != itemIds.Count) return Results.BadRequest(new { error = "item_not_found" });

        var request = new StockRequest { OrganizationId = tenant.OrganizationId!.Value, JobId = job.Id, RequestedBy = user, Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim() };
        db.StockRequests.Add(request);
        await db.SaveChangesAsync(ct);
        foreach (var l in req.Lines.GroupBy(l => l.ItemId))
            db.StockRequestLines.Add(new StockRequestLine { OrganizationId = request.OrganizationId, RequestId = request.Id, ItemId = l.Key, Quantity = l.Sum(x => x.Quantity) });
        bus.Publish(new StockRequested(request.OrganizationId, request.Id, job.Id, job.Number));
        await db.SaveChangesAsync(ct);
        return Results.Created($"/stock/requests/{request.Id}", (await RequestDtosAsync(db, stock, [request], ct))[0]);
    }

    private static async Task<IResult> ListRequestsAsync(string? status, Guid? jobId, ClaimsPrincipal principal, CurrentMemberships memberships, AppDbContext db, StockService stock, CancellationToken ct)
    {
        var user = Guid.Parse(principal.FindFirstValue("sub")!);
        var query = db.StockRequests.AsNoTracking().AsQueryable();
        // The storekeeper sees everyone's; a technician sees their own.
        if (!await memberships.CanAsync(Permissions.StockManage, null, null, ct)) query = query.Where(r => r.RequestedBy == user);
        if (status is not null) query = query.Where(r => r.Status == status);
        if (jobId is { } j) query = query.Where(r => r.JobId == j);
        return Results.Ok(await RequestDtosAsync(db, stock, await query.OrderBy(r => r.CreatedAt).Take(200).ToListAsync(ct), ct));
    }

    /// <summary>The storekeeper issues what was asked for (or less), against the job; if any line cannot be met nothing is issued.</summary>
    private static async Task<IResult> FulfilAsync(Guid id, Fulfil req, ClaimsPrincipal principal, AppDbContext db, StockService stock, IEventBus bus, CancellationToken ct)
    {
        var user = Guid.Parse(principal.FindFirstValue("sub")!);
        var request = await db.StockRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();
        if (request.Status != RequestStatus.Open) return Results.Conflict(new { error = "not_open", status = request.Status });

        var asked = await db.StockRequestLines.AsNoTracking().Where(l => l.RequestId == id).ToListAsync(ct);
        var lines = req.Lines is { Count: > 0 } ? req.Lines : asked.Select(l => new RequestLineInput(l.ItemId, l.Quantity)).ToList();
        if (lines.Any(l => l.Quantity <= 0 || asked.All(a => a.ItemId != l.ItemId))) return Results.BadRequest(new { error = "line_not_in_request" });

        var result = await stock.InTransactionAsync(async () =>
        {
            foreach (var l in lines)
            {
                var (_, error, available) = await stock.RecordAsync(l.ItemId, StockKinds.Issue, -l.Quantity, $"Request {id.ToString()[..8]}", user, request.JobId, request.Id, null, ct);
                if (error is not null) { db.ChangeTracker.Clear(); return Results.Conflict(new { error, itemId = l.ItemId, available }); }
            }
            var tracked = await db.StockRequests.SingleAsync(r => r.Id == id, ct);
            tracked.Status = RequestStatus.Fulfilled; tracked.DecidedBy = user; tracked.DecidedAt = DateTimeOffset.UtcNow;
            var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == tracked.JobId, ct);
            db.JobUpdates.Add(new JobUpdate { OrganizationId = tracked.OrganizationId, JobId = job.Id, AuthorId = user, Kind = "materials", Note = $"Materials issued from the store ({lines.Count} item(s))" });
            bus.Publish(new StockRequestDecided(tracked.OrganizationId, tracked.Id, job.Id, job.Number, true, null, tracked.RequestedBy));
            await db.SaveChangesAsync(ct);
            return null;
        }, ct, commit: failed => failed is null); // a short line undoes the lines already issued
        if (result is not null) return result;
        return Results.Ok((await RequestDtosAsync(db, stock, [await db.StockRequests.AsNoTracking().SingleAsync(r => r.Id == id, ct)], ct))[0]);
    }

    private static async Task<IResult> RejectAsync(Guid id, Reason req, ClaimsPrincipal principal, AppDbContext db, StockService stock, IEventBus bus, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "reason_required" });
        var request = await db.StockRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return Results.NotFound();
        if (request.Status != RequestStatus.Open) return Results.Conflict(new { error = "not_open", status = request.Status });
        request.Status = RequestStatus.Rejected; request.DecidedBy = Guid.Parse(principal.FindFirstValue("sub")!); request.DecidedAt = DateTimeOffset.UtcNow; request.DecisionNote = req.Text.Trim();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == request.JobId, ct);
        bus.Publish(new StockRequestDecided(request.OrganizationId, request.Id, job.Id, job.Number, false, request.DecisionNote, request.RequestedBy));
        await db.SaveChangesAsync(ct);
        return Results.Ok((await RequestDtosAsync(db, stock, [request], ct))[0]);
    }

    private static async Task<IResult> CancelAsync(Guid id, ClaimsPrincipal principal, AppDbContext db, StockService stock, CancellationToken ct)
    {
        var user = Guid.Parse(principal.FindFirstValue("sub")!);
        var request = await db.StockRequests.SingleOrDefaultAsync(r => r.Id == id && r.RequestedBy == user, ct);
        if (request is null) return Results.NotFound();
        if (request.Status != RequestStatus.Open) return Results.Conflict(new { error = "not_open", status = request.Status });
        request.Status = RequestStatus.Cancelled; request.DecidedBy = user; request.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok((await RequestDtosAsync(db, stock, [request], ct))[0]);
    }
}
