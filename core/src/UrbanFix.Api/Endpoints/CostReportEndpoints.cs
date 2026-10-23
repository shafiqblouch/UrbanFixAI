using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Where the money went (JC-10) and the review of repairs to be charged to houses (JC-11).</summary>
public static class CostReportEndpoints
{
    public sealed record CostRow(string Key, string Name, int Jobs, decimal Estimated, decimal Actual, decimal Variance);
    public sealed record CostReport(string GroupBy, DateTimeOffset? From, DateTimeOffset? To, IReadOnlyList<CostRow> Rows, int Jobs, decimal Estimated, decimal Actual);

    public sealed record RepeatedJob(Guid JobId, string Number, string Reference, DateTimeOffset CreatedAt, decimal Actual);
    public sealed record RepeatedRepair(string Place, string PlaceKey, string? Category, int Repairs, decimal TotalCost, IReadOnlyList<RepeatedJob> Jobs);

    public sealed record ChargeLine(string Kind, string Description, decimal Quantity, decimal Amount);
    public sealed record ChargeResident(Guid UserId, string Name, string Relation);
    public sealed record ChargeEvidence(string Stage, string Url);
    public sealed record ChargeDto(
        Guid Id, Guid JobId, string JobNumber, string Reference, string Description, Guid HouseId, string HouseLabel, IReadOnlyList<ChargeResident> Residents,
        decimal ProposedAmount, decimal? FinalAmount, string Status, bool ReadyToPost, string? Note, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt,
        IReadOnlyList<ChargeLine> Costs, IReadOnlyList<ChargeEvidence> Evidence);
    public sealed record ChargeDecision(string Action, decimal? Amount, string? Note);

    public static void MapCostReportEndpoints(this IEndpointRouteBuilder app)
    {
        var dash = AuthPolicies.For(Permissions.CostRead);

        app.MapGet("/reports/costs", CostsAsync).WithTags("Reports").Produces<CostReport>(200).RequireAuthorization(dash);
        app.MapGet("/reports/repeated-repairs", RepeatedAsync).WithTags("Reports").Produces<List<RepeatedRepair>>(200).RequireAuthorization(dash);

        var g = app.MapGroup("/charges").WithTags("Charges");
        var accounts = AuthPolicies.For(Permissions.BillManage);
        g.MapGet("", ListChargesAsync).Produces<List<ChargeDto>>(200).RequireAuthorization(accounts);
        g.MapPost("/{id:guid}/decision", DecideChargeAsync).Produces<ChargeDto>(200).RequireAuthorization(accounts);
    }

    // Places ---------------------------------------------------------------------------------------------------------

    /// <summary>The street (or building) a location belongs to, for grouping.</summary>
    private sealed class Places(List<Location> all)
    {
        private readonly Dictionary<Guid, Location> _byId = all.ToDictionary(l => l.Id);

        public (string Key, string Name) StreetOf(Guid? locationId)
        {
            if (locationId is not { } id || !_byId.TryGetValue(id, out var loc)) return ("none", "Unknown place");
            var chain = loc.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Where(_byId.ContainsKey).Select(x => _byId[x]).ToList();
            var street = chain.LastOrDefault(l => l.Kind is "street" or "building") ?? (chain.Count > 1 ? chain[^2] : chain.LastOrDefault()) ?? loc;
            return (street.Id.ToString(), Label(street));
        }

        public string Label(Location l) => string.Join(" › ", l.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Where(_byId.ContainsKey).Select(x => _byId[x].Name));
        public string LabelOf(Guid? locationId) => locationId is { } id && _byId.TryGetValue(id, out var l) ? Label(l) : "Unknown place";
    }

    // JC-10 ----------------------------------------------------------------------------------------------------------

    private static async Task<IResult> CostsAsync(string? groupBy, DateTimeOffset? from, DateTimeOffset? to, AppDbContext db, CancellationToken ct)
    {
        var by = groupBy ?? "job";
        if (by is not ("job" or "category" or "street")) return Results.BadRequest(new { error = "invalid_group_by" });
        return Results.Ok(await BuildAsync(db, by, from, to, ct));
    }

    /// <summary>The cost report itself, shared with the dashboard's spend widget so the two always show the same figures (DB-07).</summary>
    internal static async Task<CostReport> BuildAsync(AppDbContext db, string by, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var q = from j in db.Jobs.AsNoTracking()
                join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                where j.Status != JobStatus.Cancelled
                select new { j.Id, j.Number, j.CreatedAt, c.Reference, c.CategoryId, c.LocationId };
        if (from is { } f) q = q.Where(x => x.CreatedAt >= f);
        if (to is { } t) q = q.Where(x => x.CreatedAt <= t);
        var jobs = await q.ToListAsync(ct);

        var ids = jobs.Select(j => j.Id).ToList();
        var actual = (await db.JobCosts.AsNoTracking().Where(c => ids.Contains(c.JobId)).Select(c => new { c.JobId, c.Amount }).ToListAsync(ct)).GroupBy(c => c.JobId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var estimated = await db.Estimates.AsNoTracking().Where(e => e.Status == "Current" && ids.Contains(e.JobId)).ToDictionaryAsync(e => e.JobId, e => e.Total, ct);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
        var places = new Places(await db.Locations.AsNoTracking().ToListAsync(ct));

        (string Key, string Name) Group(dynamic j) => by switch
        {
            "category" => j.CategoryId is Guid cid ? (cid.ToString(), categories.GetValueOrDefault(cid, "?")) : ("none", "Not categorised"),
            "street" => places.StreetOf((Guid?)j.LocationId),
            _ => (j.Id.ToString(), $"{j.Number} ({j.Reference})"),
        };

        var rows = jobs.Select(j => (Group: Group(j), Actual: actual.GetValueOrDefault(j.Id), Estimated: estimated.GetValueOrDefault(j.Id)))
            .GroupBy(x => x.Group)
            .Select(g => new CostRow(g.Key.Key, g.Key.Name, g.Count(), g.Sum(x => x.Estimated), g.Sum(x => x.Actual), g.Sum(x => x.Actual) - g.Sum(x => x.Estimated)))
            .OrderByDescending(r => r.Actual).ThenBy(r => r.Name).ToList();
        return new CostReport(by, from, to, rows, jobs.Count, rows.Sum(r => r.Estimated), rows.Sum(r => r.Actual));
    }

    /// <summary>Places where the same problem keeps coming back: the same category of repair at the same house or street, several times in a period.</summary>
    private static async Task<IResult> RepeatedAsync(string? by, int? months, int? minCount, bool? sameCategory, AppDbContext db, CancellationToken ct)
    {
        var policy = OrgPolicy.From(await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct) ?? "{}");
        var level = by ?? "house";
        if (level is not ("house" or "street")) return Results.BadRequest(new { error = "invalid_by" });
        var since = DateTimeOffset.UtcNow.AddMonths(-Math.Clamp(months ?? policy.RepeatedRepairMonths, 1, 60));
        var threshold = Math.Clamp(minCount ?? policy.RepeatedRepairCount, 2, 100);

        var jobs = await (from j in db.Jobs.AsNoTracking()
                          join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                          where j.Status != JobStatus.Cancelled && j.CreatedAt >= since
                          select new { j.Id, j.Number, j.CreatedAt, c.Reference, c.CategoryId, c.HouseId, c.LocationId }).ToListAsync(ct);
        var ids = jobs.Select(j => j.Id).ToList();
        var actual = (await db.JobCosts.AsNoTracking().Where(c => ids.Contains(c.JobId)).Select(c => new { c.JobId, c.Amount }).ToListAsync(ct)).GroupBy(c => c.JobId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
        var places = new Places(await db.Locations.AsNoTracking().ToListAsync(ct));
        var houseLocations = await db.Houses.AsNoTracking().ToDictionaryAsync(h => h.Id, h => h.LocationId, ct);
        var matchCategory = sameCategory != false;

        (string Key, string Name) Place(Guid? houseId, Guid? locationId)
        {
            var loc = locationId ?? (houseId is { } h && houseLocations.TryGetValue(h, out var l) ? l : null);
            return level == "street" ? places.StreetOf(loc) : houseId is { } hid ? (hid.ToString(), places.LabelOf(houseLocations.GetValueOrDefault(hid))) : loc is null ? ("none", "Unknown place") : (loc.Value.ToString(), places.LabelOf(loc));
        }

        var result = jobs.Select(j => (Place: Place(j.HouseId, j.LocationId), Category: matchCategory ? j.CategoryId : null, Job: j))
            .GroupBy(x => (x.Place, x.Category))
            .Where(g => g.Count() >= threshold && g.Key.Place.Key != "none")
            .Select(g => new RepeatedRepair(g.Key.Place.Name, g.Key.Place.Key, g.Key.Category is { } c ? categories.GetValueOrDefault(c) : null, g.Count(), g.Sum(x => actual.GetValueOrDefault(x.Job.Id)),
                g.OrderBy(x => x.Job.CreatedAt).Select(x => new RepeatedJob(x.Job.Id, x.Job.Number, x.Job.Reference, x.Job.CreatedAt, actual.GetValueOrDefault(x.Job.Id))).ToList()))
            .OrderByDescending(r => r.Repairs).ThenByDescending(r => r.TotalCost).ToList();
        return Results.Ok(result);
    }

    // JC-11 ----------------------------------------------------------------------------------------------------------

    private static async Task<List<ChargeDto>> ChargeDtosAsync(AppDbContext db, UrbanFix.Infrastructure.Storage.IObjectStorage storage, List<JobCharge> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var jobIds = rows.Select(r => r.JobId).ToList();
        var jobs = await (from j in db.Jobs.AsNoTracking() join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id where jobIds.Contains(j.Id) select new { j.Id, j.Number, c.Reference, c.Description }).ToDictionaryAsync(x => x.Id, ct);
        var costs = (await db.JobCosts.AsNoTracking().Where(c => jobIds.Contains(c.JobId)).ToListAsync(ct)).ToLookup(c => c.JobId);
        var media = (await db.ComplaintMedia.AsNoTracking().Where(m => m.JobId != null && jobIds.Contains(m.JobId.Value) && m.Kind == "image").OrderBy(m => m.CreatedAt).ToListAsync(ct)).ToLookup(m => m.JobId!.Value);
        var houseIds = rows.Select(r => r.HouseId).Distinct().ToList();
        var houseLoc = await db.Houses.AsNoTracking().Where(h => houseIds.Contains(h.Id)).ToDictionaryAsync(h => h.Id, h => h.LocationId, ct);
        var places = new Places(await db.Locations.AsNoTracking().ToListAsync(ct));
        var links = await (from r in db.ResidentLinks.AsNoTracking() join u in db.Users.AsNoTracking() on r.UserId equals u.Id where houseIds.Contains(r.HouseId) && r.ToDate == null select new { r.HouseId, u.Id, u.DisplayName, r.Relation }).ToListAsync(ct);

        return rows.Select(r =>
        {
            var job = jobs[r.JobId];
            return new ChargeDto(r.Id, r.JobId, job.Number, job.Reference, job.Description, r.HouseId, places.LabelOf(houseLoc.GetValueOrDefault(r.HouseId)),
                links.Where(l => l.HouseId == r.HouseId).Select(l => new ChargeResident(l.Id, l.DisplayName, l.Relation)).ToList(),
                r.ProposedAmount, r.FinalAmount, r.Status, ChargeRules.ReadyToPost(r.Status), r.Note, r.CreatedAt, r.DecidedAt,
                costs[r.JobId].Select(c => new ChargeLine(c.Kind, c.Description, c.Quantity, c.Amount)).ToList(),
                media[r.JobId].Where(m => m.Stage is "before" or "after").Select(m => new ChargeEvidence(m.Stage!, storage.CreateDownloadUrl(m.ThumbKey ?? m.StorageKey))).ToList());
        }).ToList();
    }

    private static async Task<IResult> ListChargesAsync(string? status, AppDbContext db, UrbanFix.Infrastructure.Storage.IObjectStorage storage, CancellationToken ct)
    {
        var wanted = status ?? ChargeStatus.Pending;
        return Results.Ok(await ChargeDtosAsync(db, storage, await db.JobCharges.AsNoTracking().Where(c => c.Status == wanted).OrderBy(c => c.CreatedAt).Take(200).ToListAsync(ct), ct));
    }

    private static async Task<IResult> DecideChargeAsync(Guid id, ChargeDecision req, ClaimsPrincipal user, AppDbContext db, UrbanFix.Infrastructure.Storage.IObjectStorage storage, CancellationToken ct)
    {
        var charge = await db.JobCharges.SingleOrDefaultAsync(c => c.Id == id, ct);
        if (charge is null) return Results.NotFound();
        var (error, status, final) = ChargeRules.Decide(charge.Status, charge.ProposedAmount, req.Action, req.Amount, req.Note);
        if (error is not null) return error == "not_pending" ? Results.Conflict(new { error, status }) : Results.BadRequest(new { error });

        charge.Status = status; charge.FinalAmount = final; charge.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        charge.DecidedBy = Guid.Parse(user.FindFirstValue("sub")!); charge.DecidedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ChargeDtosAsync(db, storage, [charge], ct))[0]);
    }
}
