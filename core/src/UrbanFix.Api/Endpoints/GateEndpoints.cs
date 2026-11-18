using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.GateHardware;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Platform;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>
/// Gate security: checkpoints (GT-00), a who-is-this lookup against the directory (GT-02), the entry/exit log
/// and who-is-inside (GT-01), and pre-authorised visitor passes (GT-03). Everything here needs the "gate"
/// feature on for the society; guards hold <c>gate.log</c>, managers also hold <c>gate.manage</c>.
/// </summary>
public static class GateEndpoints
{
    // ---- Gates (GT-00) ----
    public sealed record GateDto(Guid Id, string Name, bool IsActive);
    public sealed record SaveGate(string Name, bool? IsActive);

    // ---- Lookup (GT-02) ----
    public sealed record VehicleHit(Guid Id, string Plate, string Type, Guid? HouseId, string? HouseLabel, string Status);
    public sealed record HelpHit(Guid Id, string Name, string Role, string Status, bool AllowedNow, string? PhotoUrl, IReadOnlyList<Guid> HouseIds);
    public sealed record PassHit(Guid Id, string Code, string VisitorName, string Kind, Guid HouseId, string? HouseLabel, string? VehiclePlate, string State);
    public sealed record LookupResult(IReadOnlyList<VehicleHit> Vehicles, IReadOnlyList<HelpHit> Help, IReadOnlyList<PassHit> Passes);

    // ---- Events (GT-01) ----
    public sealed record RecordEvent(
        string Direction, string SubjectType, Guid? GateId, Guid? HouseId, Guid? VehicleId, Guid? HelpId, Guid? PassId,
        Guid? ResidentUserId, string? SubjectName, string? Phone, string? VehiclePlate, string? Decision, string? Note);
    public sealed record EventDto(
        Guid Id, Guid? GateId, string Direction, string SubjectType, Guid? HouseId, string? HouseLabel, Guid? VehicleId, Guid? HelpId,
        Guid? PassId, string? SubjectName, string? Phone, string? VehiclePlate, string? PhotoUrl, string Decision, string? Note,
        string Source, Guid? RecordedBy, DateTimeOffset RecordedAt);

    // ---- Passes (GT-03) ----
    public sealed record SavePass(
        string VisitorName, string? Phone, string? VehiclePlate, Guid HouseId, string? Kind, string? Purpose,
        DateTimeOffset? ValidFrom, DateTimeOffset? ValidTo, int? MaxUses);
    public sealed record PassDto(
        Guid Id, string Code, string VisitorName, string? Phone, string? VehiclePlate, Guid HouseId, string? HouseLabel, string Kind,
        string? Purpose, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, int? MaxUses, int UsedCount, string Status, string State);

    // ---- Visitor approval (GT-04) ----
    public sealed record VisitRequest(Guid HouseId, string VisitorName, string? Phone, string? VehiclePlate, string? Kind, string? Note);
    public sealed record Decide(bool Approve, string? Note);
    public sealed record MyHouseDto(Guid Id, string Label);

    // ---- RFID readers + boom barrier (GT-05) ----
    public sealed record GateReaderDto(Guid Id, Guid GateId, string? GateName, string Name, string Direction, bool IsActive, DateTimeOffset? LastSeenAt);
    public sealed record SaveReader(Guid GateId, string Name, string Direction);
    public sealed record UpdateReader(string? Name, string? Direction, bool? IsActive);
    /// <summary>A reader with its freshly issued key — the only time the key is ever returned in full.</summary>
    public sealed record GateReaderKeyDto(GateReaderDto Reader, string ApiKey);
    public sealed record GateApprovalDto(Guid Id, Guid? GateId, string? GateName, string Direction, string? Tag, string? VehiclePlate, string Status, DateTimeOffset CreatedAt);
    // What a reader sends and gets back.
    public sealed record ScanRequest(string? Tag);
    /// <summary>action: open (raise the barrier now) | hold (keep it shut, a guard was asked) | deny.</summary>
    public sealed record ScanResult(string Action, string Decision, string? Subject, Guid? ApprovalId);
    public sealed record OpenCommand(Guid ApprovalId, string Direction);

    public static void MapGateEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Gate").AddEndpointFilter(RequireGateFeature);
        var log = AuthPolicies.For(Permissions.GateLog);
        var manage = AuthPolicies.For(Permissions.GateManage);

        // Checkpoints ------------------------------------------------------------------------------------------
        g.MapGet("/gates", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Gates.AsNoTracking().OrderBy(x => x.Name)
                .Select(x => new GateDto(x.Id, x.Name, x.IsActive)).ToListAsync(ct)))
            .Produces<List<GateDto>>(200).RequireAuthorization(manage);

        g.MapPost("/gates", async (SaveGate req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            var gate = new Gate { OrganizationId = tenant.OrganizationId!.Value, Name = req.Name.Trim(), IsActive = req.IsActive ?? true };
            db.Gates.Add(gate);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/gates/{gate.Id}", new GateDto(gate.Id, gate.Name, gate.IsActive));
        }).Produces<GateDto>(201).RequireAuthorization(manage);

        g.MapPut("/gates/{id:guid}", async (Guid id, SaveGate req, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.Gates.SingleOrDefaultAsync(x => x.Id == id, ct) is not { } gate) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            gate.Name = req.Name.Trim();
            if (req.IsActive is { } active) gate.IsActive = active;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new GateDto(gate.Id, gate.Name, gate.IsActive));
        }).Produces<GateDto>(200).RequireAuthorization(manage);

        // Lookup (GT-02) ---------------------------------------------------------------------------------------
        g.MapGet("/gate/lookup", LookupAsync).Produces<LookupResult>(200).RequireAuthorization(log);

        // Entry / exit log (GT-01) ----------------------------------------------------------------------------
        g.MapPost("/gate/events", RecordAsync).Produces<EventDto>(201).RequireAuthorization(log);

        g.MapPost("/gate/events/{id:guid}/photo", async (Guid id, [FromForm] IFormFile file, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            if (await db.GateEvents.SingleOrDefaultAsync(e => e.Id == id, ct) is not { } ev) return Results.NotFound();
            if (file.Length is 0 or > 5 * 1024 * 1024) return Results.BadRequest(new { error = "invalid_photo_size" });
            await using var stream = file.OpenReadStream();
            try { ev.PhotoKey = (await storage.PutImageAsync(stream, ct)).Key; }
            catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "not_an_image" });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok((await EventDtosAsync(db, storage, [ev], ct))[0]);
        }).DisableAntiforgery().Produces<EventDto>(200).RequireAuthorization(log);

        g.MapGet("/gate/events", async (string? q, string? direction, Guid? gateId, DateTimeOffset? from, DateTimeOffset? to, int? limit,
            AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            var query = db.GateEvents.AsNoTracking().AsQueryable();
            if (direction is "in" or "out") query = query.Where(e => e.Direction == direction);
            if (gateId is { } gid) query = query.Where(e => e.GateId == gid);
            if (from is { } f) query = query.Where(e => e.RecordedAt >= f);
            if (to is { } t) query = query.Where(e => e.RecordedAt <= t);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = Like(q);
                var key = RegistryRules.PlateKey(q);
                query = query.Where(e => EF.Functions.ILike(e.SubjectName ?? "", pattern, "\\")
                    || (e.PlateKey != null && key != "" && e.PlateKey.Contains(key))
                    || (e.Phone != null && EF.Functions.ILike(e.Phone, pattern, "\\")));
            }
            var rows = await query.OrderByDescending(e => e.RecordedAt).Take(Math.Clamp(limit ?? 100, 1, 500)).ToListAsync(ct);
            return Results.Ok(await EventDtosAsync(db, storage, rows, ct));
        }).Produces<List<EventDto>>(200).RequireAuthorization(log);

        g.MapGet("/gate/inside", async (Guid? gateId, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            // Look back far enough to pair an exit with its entry without scanning the whole log.
            var since = DateTimeOffset.UtcNow.AddHours(-36);
            var query = db.GateEvents.AsNoTracking().Where(e => e.RecordedAt >= since);
            if (gateId is { } gid) query = query.Where(e => e.GateId == gid);
            var recent = await query.ToListAsync(ct);
            var inside = GateRules.Inside(recent).OrderByDescending(e => e.RecordedAt).ToList();
            return Results.Ok(await EventDtosAsync(db, storage, inside, ct));
        }).Produces<List<EventDto>>(200).RequireAuthorization(log);

        // Passes (GT-03) ---------------------------------------------------------------------------------------
        g.MapGet("/gate/passes", async (string? q, Guid? houseId, string? status, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.GatePasses.AsNoTracking().AsQueryable();
            if (houseId is { } h) query = query.Where(p => p.HouseId == h);
            if (status is "active" or "cancelled") query = query.Where(p => p.Status == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var code = GateRules.NormalizeCode(q);
                var pattern = Like(q);
                query = query.Where(p => (code != "" && p.Code == code) || EF.Functions.ILike(p.VisitorName, pattern, "\\"));
            }
            var rows = await query.OrderByDescending(p => p.CreatedAt).Take(200).ToListAsync(ct);
            return Results.Ok(await PassDtosAsync(db, rows, ct));
        }).Produces<List<PassDto>>(200).RequireAuthorization(log);

        g.MapPost("/gate/passes", async (SavePass req, ClaimsPrincipal user, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var pass = new GatePass { OrganizationId = tenant.OrganizationId!.Value, CreatedBy = Guid.Parse(user.FindFirstValue("sub")!) };
            if (await ApplyPassAsync(pass, req, db, ct) is { } error) return error;
            await SavePassWithCodeAsync(db, pass, ct);
            return Results.Created($"/gate/passes/{pass.Id}", (await PassDtosAsync(db, [pass], ct))[0]);
        }).Produces<PassDto>(201).RequireAuthorization(log);

        g.MapPost("/gate/passes/{id:guid}/cancel", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.GatePasses.SingleOrDefaultAsync(p => p.Id == id, ct) is not { } pass) return Results.NotFound();
            if (pass.Status != "cancelled") { pass.Status = "cancelled"; await db.SaveChangesAsync(ct); }
            return Results.Ok((await PassDtosAsync(db, [pass], ct))[0]);
        }).Produces<PassDto>(200).RequireAuthorization(log);

        // Guard asks the house to approve a visitor waiting at the gate (GT-04) -------------------------------
        g.MapPost("/gate/visit-requests", async (VisitRequest req, ClaimsPrincipal user, ITenantContext tenant, AppDbContext db, IEventBus bus, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.VisitorName)) return Results.BadRequest(new { error = "name_required" });
            if (!await db.Houses.AnyAsync(x => x.Id == req.HouseId, ct)) return Results.BadRequest(new { error = "house_not_found" });
            if (req.Kind is not null && !GateRules.PassKinds.Contains(req.Kind)) return Results.BadRequest(new { error = "invalid_kind" });
            string? phone = null;
            if (!string.IsNullOrWhiteSpace(req.Phone))
            {
                phone = PhoneNumber.Normalize(req.Phone);
                if (phone is null) return Results.BadRequest(new { error = "invalid_phone" });
            }
            var now = DateTimeOffset.UtcNow;
            var pass = new GatePass
            {
                OrganizationId = tenant.OrganizationId!.Value, VisitorName = req.VisitorName.Trim(), Phone = phone,
                VehiclePlate = string.IsNullOrWhiteSpace(req.VehiclePlate) ? null : req.VehiclePlate.Trim().ToUpperInvariant(),
                PlateKey = string.IsNullOrWhiteSpace(req.VehiclePlate) ? null : RegistryRules.PlateKey(req.VehiclePlate),
                HouseId = req.HouseId, Kind = req.Kind ?? "visitor", Purpose = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
                ValidFrom = now, ValidTo = now.AddHours(2), MaxUses = 1, Status = "pending", CreatedBy = Guid.Parse(user.FindFirstValue("sub")!),
            };
            await SavePassWithCodeAsync(db, pass, ct);

            var residents = await db.ResidentLinks.AsNoTracking().Where(r => r.HouseId == req.HouseId && r.ToDate == null).Select(r => r.UserId).Distinct().ToListAsync(ct);
            bus.Publish(new GateVisitRequested(pass.OrganizationId, pass.Id, pass.HouseId, pass.VisitorName, residents));
            await db.SaveChangesAsync(ct); // writes the outbox row that notifies the residents

            return Results.Created($"/gate/passes/{pass.Id}", (await PassDtosAsync(db, [pass], ct))[0]);
        }).Produces<PassDto>(201).RequireAuthorization(log);

        // Resident self-service: their own passes and the gate requests waiting for them (GT-03, GT-04) -------
        var own = AuthPolicies.For(Permissions.GatePassOwn);

        g.MapGet("/my/houses", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var ids = await MyHouseIdsAsync(db, Me(user), ct);
            var labels = await HouseLabelsAsync(db, ids, ct);
            return Results.Ok(ids.Select(h => new MyHouseDto(h, labels.GetValueOrDefault(h, ""))).ToList());
        }).Produces<List<MyHouseDto>>(200).RequireAuthorization(own);

        g.MapGet("/my/gate-passes", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var houses = await MyHouseIdsAsync(db, Me(user), ct);
            var rows = await db.GatePasses.AsNoTracking().Where(p => houses.Contains(p.HouseId)).OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync(ct);
            return Results.Ok(await PassDtosAsync(db, rows, ct));
        }).Produces<List<PassDto>>(200).RequireAuthorization(own);

        g.MapPost("/my/gate-passes", async (SavePass req, ClaimsPrincipal user, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var houses = await MyHouseIdsAsync(db, Me(user), ct);
            if (!houses.Contains(req.HouseId)) return Results.BadRequest(new { error = "not_your_house" });
            var pass = new GatePass { OrganizationId = tenant.OrganizationId!.Value, CreatedBy = Me(user) };
            if (await ApplyPassAsync(pass, req, db, ct) is { } error) return error;
            await SavePassWithCodeAsync(db, pass, ct);
            return Results.Created($"/my/gate-passes/{pass.Id}", (await PassDtosAsync(db, [pass], ct))[0]);
        }).Produces<PassDto>(201).RequireAuthorization(own);

        g.MapPost("/my/gate-passes/{id:guid}/cancel", async (Guid id, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var houses = await MyHouseIdsAsync(db, Me(user), ct);
            if (await db.GatePasses.SingleOrDefaultAsync(p => p.Id == id, ct) is not { } pass || !houses.Contains(pass.HouseId)) return Results.NotFound();
            if (pass.Status is "active" or "pending") { pass.Status = "cancelled"; await db.SaveChangesAsync(ct); }
            return Results.Ok((await PassDtosAsync(db, [pass], ct))[0]);
        }).Produces<PassDto>(200).RequireAuthorization(own);

        g.MapGet("/my/gate-visit-requests", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var houses = await MyHouseIdsAsync(db, Me(user), ct);
            var rows = await db.GatePasses.AsNoTracking().Where(p => p.Status == "pending" && houses.Contains(p.HouseId)).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
            return Results.Ok(await PassDtosAsync(db, rows, ct));
        }).Produces<List<PassDto>>(200).RequireAuthorization(own);

        g.MapPost("/my/gate-visit-requests/{id:guid}/decide", async (Guid id, Decide req, ClaimsPrincipal user, AppDbContext db, IEventBus bus, CancellationToken ct) =>
        {
            var houses = await MyHouseIdsAsync(db, Me(user), ct);
            if (await db.GatePasses.SingleOrDefaultAsync(p => p.Id == id, ct) is not { } pass || !houses.Contains(pass.HouseId)) return Results.NotFound();
            if (pass.Status != "pending") return Results.Conflict(new { error = "already_decided", state = pass.Status });
            pass.Status = req.Approve ? "active" : "denied";
            pass.DecidedBy = Me(user);
            pass.DecidedAt = DateTimeOffset.UtcNow;
            if (pass.CreatedBy is { } guard) bus.Publish(new GateVisitDecided(pass.OrganizationId, pass.Id, guard, req.Approve, pass.VisitorName));
            await db.SaveChangesAsync(ct);
            return Results.Ok((await PassDtosAsync(db, [pass], ct))[0]);
        }).Produces<PassDto>(200).RequireAuthorization(own);

        // RFID readers + boom barrier (GT-05) ------------------------------------------------------------------
        // A reader per lane, each fixed to one direction and holding its own key. Managers set them up.
        g.MapGet("/gate/readers", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.GateReaders.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
            var gates = await db.Gates.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return Results.Ok(rows.Select(r => new GateReaderDto(r.Id, r.GateId, gates.GetValueOrDefault(r.GateId), r.Name, r.Direction, r.IsActive, r.LastSeenAt)).ToList());
        }).Produces<List<GateReaderDto>>(200).RequireAuthorization(manage);

        g.MapPost("/gate/readers", async (SaveReader req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            if (!GateRules.Directions.Contains(req.Direction)) return Results.BadRequest(new { error = "invalid_direction" });
            if (!await db.Gates.AnyAsync(x => x.Id == req.GateId, ct)) return Results.BadRequest(new { error = "gate_not_found" });
            var key = GateReaderAuth.NewKey();
            var reader = new GateReader { OrganizationId = tenant.OrganizationId!.Value, GateId = req.GateId, Name = req.Name.Trim(), Direction = req.Direction, ApiKeyHash = GateReaderAuth.HashKey(key) };
            db.GateReaders.Add(reader);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/gate/readers/{reader.Id}", new GateReaderKeyDto(await ReaderDtoAsync(db, reader, ct), key));
        }).Produces<GateReaderKeyDto>(201).RequireAuthorization(manage);

        g.MapPut("/gate/readers/{id:guid}", async (Guid id, UpdateReader req, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.GateReaders.SingleOrDefaultAsync(r => r.Id == id, ct) is not { } reader) return Results.NotFound();
            if (req.Direction is { } d) { if (!GateRules.Directions.Contains(d)) return Results.BadRequest(new { error = "invalid_direction" }); reader.Direction = d; }
            if (!string.IsNullOrWhiteSpace(req.Name)) reader.Name = req.Name.Trim();
            if (req.IsActive is { } active) reader.IsActive = active;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ReaderDtoAsync(db, reader, ct));
        }).Produces<GateReaderDto>(200).RequireAuthorization(manage);

        g.MapPost("/gate/readers/{id:guid}/rotate-key", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.GateReaders.SingleOrDefaultAsync(r => r.Id == id, ct) is not { } reader) return Results.NotFound();
            var key = GateReaderAuth.NewKey();
            reader.ApiKeyHash = GateReaderAuth.HashKey(key);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new GateReaderKeyDto(await ReaderDtoAsync(db, reader, ct), key));
        }).Produces<GateReaderKeyDto>(200).RequireAuthorization(manage);

        // Cars held at the barrier, waiting for a guard to allow or deny (GT-05) -------------------------------
        g.MapGet("/gate/waiting", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.GateApprovals.AsNoTracking().Where(a => a.Status == "pending").OrderBy(a => a.CreatedAt).Take(50).ToListAsync(ct);
            var gates = await db.Gates.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return Results.Ok(rows.Select(a => new GateApprovalDto(a.Id, a.GateId, a.GateId is { } gid ? gates.GetValueOrDefault(gid) : null, a.Direction, a.Tag, a.VehiclePlate, a.Status, a.CreatedAt)).ToList());
        }).Produces<List<GateApprovalDto>>(200).RequireAuthorization(log);

        g.MapPost("/gate/waiting/{id:guid}/decide", async (Guid id, Decide req, ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.GateApprovals.SingleOrDefaultAsync(a => a.Id == id, ct) is not { } appr) return Results.NotFound();
            if (appr.Status != "pending") return Results.Conflict(new { error = "already_decided", status = appr.Status });
            var ev = new GateEvent
            {
                OrganizationId = appr.OrganizationId, GateId = appr.GateId, Direction = appr.Direction, SubjectType = "visitor",
                VehiclePlate = appr.VehiclePlate, PlateKey = appr.VehiclePlate is { } p ? RegistryRules.PlateKey(p) : null,
                Decision = req.Approve ? "allowed" : "denied", Source = "rfid", ReaderId = appr.ReaderId,
                Note = string.IsNullOrWhiteSpace(req.Note) ? (appr.Tag is { } tag ? $"RFID {tag}" : null) : req.Note.Trim(), RecordedBy = Me(user),
            };
            db.GateEvents.Add(ev);
            appr.Status = req.Approve ? "approved" : "denied";
            appr.DecidedBy = Me(user);
            appr.DecidedAt = DateTimeOffset.UtcNow;
            appr.EventId = ev.Id;
            await db.SaveChangesAsync(ct);
            var gateName = appr.GateId is { } g2 ? await db.Gates.Where(x => x.Id == g2).Select(x => x.Name).SingleOrDefaultAsync(ct) : null;
            return Results.Ok(new GateApprovalDto(appr.Id, appr.GateId, gateName, appr.Direction, appr.Tag, appr.VehiclePlate, appr.Status, appr.CreatedAt));
        }).Produces<GateApprovalDto>(200).RequireAuthorization(log);

        // The reader itself (GT-05): scan a tag, and poll for barrier opens. Authenticated by the reader's own key,
        // so anonymous to the user scheme; the society is resolved from the key before any tenant-scoped query runs.
        app.MapPost("/gate/scan", ScanAsync).Produces<ScanResult>(200).AllowAnonymous().WithTags("Gate");
        app.MapGet("/gate/commands", CommandsAsync).Produces<List<OpenCommand>>(200).AllowAnonymous().WithTags("Gate");
    }

    /// <summary>A reader read a tag: open for a registered resident car, otherwise hold it for a guard.</summary>
    private static async Task<IResult> ScanAsync(ScanRequest req, HttpContext http, GateReaderAuth readers, ITenantContext tenant, AppDbContext db, IEventBus bus, CancellationToken ct)
    {
        if (await readers.ResolveAsync(ReaderKey(http), ct) is not { } reader) return Results.Json(new { error = "invalid_reader_key" }, statusCode: StatusCodes.Status401Unauthorized);
        ((TenantContext)tenant).Set(reader.OrganizationId, null);
        if (!await FeatureFlags.IsEnabledAsync(db, Features.Gate, ct)) return Results.Json(new { error = "feature_disabled", feature = Features.Gate }, statusCode: StatusCodes.Status403Forbidden);
        _ = readers.TouchAsync(reader.ReaderId, ct);

        var tag = GateRules.NormalizeTag(req.Tag);
        // Match the sticker case-insensitively on its stored form, hitting the unique lower(sticker_code) index.
        var lookup = (req.Tag ?? "").Trim().ToLowerInvariant();
        if (lookup.Length > 0 &&
            await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Status == "active" && v.StickerCode != null && v.StickerCode.ToLower() == lookup, ct) is { } vehicle)
        {
            // Registered resident car → open automatically, logging the crossing once (a UHF reader reads repeatedly).
            var since = DateTimeOffset.UtcNow.AddSeconds(-20);
            if (!await db.GateEvents.AnyAsync(e => e.VehicleId == vehicle.Id && e.Direction == reader.Direction && e.RecordedAt >= since, ct))
            {
                db.GateEvents.Add(new GateEvent
                {
                    OrganizationId = reader.OrganizationId, GateId = reader.GateId, Direction = reader.Direction, SubjectType = "vehicle",
                    VehicleId = vehicle.Id, HouseId = vehicle.HouseId, VehiclePlate = vehicle.Plate, PlateKey = vehicle.PlateKey,
                    Decision = "allowed", Source = "rfid", ReaderId = reader.ReaderId,
                });
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new ScanResult("open", "allowed", vehicle.Plate, null));
        }

        // An unknown tag (or a presence with no tag): keep the barrier shut and ask the on-duty guards.
        var guards = await db.Memberships.AsNoTracking()
            .Where(m => m.Role == Role.Guard && m.IsActive && (m.GateId == null || m.GateId == reader.GateId))
            .Select(m => m.UserId).Distinct().ToListAsync(ct);
        var approval = new GateApproval { OrganizationId = reader.OrganizationId, GateId = reader.GateId, ReaderId = reader.ReaderId, Direction = reader.Direction, Tag = tag.Length > 0 ? tag : null };
        db.GateApprovals.Add(approval);
        bus.Publish(new GateCarWaiting(reader.OrganizationId, approval.Id, reader.GateId, reader.Direction, approval.VehiclePlate, guards));
        await db.SaveChangesAsync(ct);
        return Results.Ok(new ScanResult("hold", "pending", null, approval.Id));
    }

    /// <summary>The reader polls this for opens: approved holds at its lane that it has not raised the barrier for yet.</summary>
    private static async Task<IResult> CommandsAsync(HttpContext http, GateReaderAuth readers, ITenantContext tenant, AppDbContext db, CancellationToken ct)
    {
        if (await readers.ResolveAsync(ReaderKey(http), ct) is not { } reader) return Results.Json(new { error = "invalid_reader_key" }, statusCode: StatusCodes.Status401Unauthorized);
        ((TenantContext)tenant).Set(reader.OrganizationId, null);
        if (!await FeatureFlags.IsEnabledAsync(db, Features.Gate, ct)) return Results.Json(new { error = "feature_disabled", feature = Features.Gate }, statusCode: StatusCodes.Status403Forbidden);
        _ = readers.TouchAsync(reader.ReaderId, ct);

        var since = DateTimeOffset.UtcNow.AddSeconds(-30); // a stale decision is not re-opened; the car has gone
        var pending = await db.GateApprovals
            .Where(a => a.ReaderId == reader.ReaderId && a.Status == "approved" && a.ConsumedAt == null && a.DecidedAt >= since)
            .OrderBy(a => a.DecidedAt).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var a in pending) a.ConsumedAt = now;
        if (pending.Count > 0) await db.SaveChangesAsync(ct);
        return Results.Ok(pending.Select(a => new OpenCommand(a.Id, a.Direction)).ToList());
    }

    private static string? ReaderKey(HttpContext http) =>
        http.Request.Headers.TryGetValue("X-Gate-Key", out var k) && k.Count > 0 ? k.ToString()
        : http.Request.Headers.Authorization.ToString() is { Length: > 7 } a && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..]
        : null;

    private static async Task<GateReaderDto> ReaderDtoAsync(AppDbContext db, GateReader reader, CancellationToken ct)
    {
        var gateName = await db.Gates.AsNoTracking().Where(x => x.Id == reader.GateId).Select(x => x.Name).SingleOrDefaultAsync(ct);
        return new GateReaderDto(reader.Id, reader.GateId, gateName, reader.Name, reader.Direction, reader.IsActive, reader.LastSeenAt);
    }

    private static Guid Me(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("sub")!);

    private static async Task<List<Guid>> MyHouseIdsAsync(AppDbContext db, Guid userId, CancellationToken ct) =>
        await db.ResidentLinks.AsNoTracking().Where(r => r.UserId == userId && r.ToDate == null).Select(r => r.HouseId).Distinct().ToListAsync(ct);

    /// <summary>Inserts a pass, generating a fresh code on the vanishingly rare collision.</summary>
    private static async Task SavePassWithCodeAsync(AppDbContext db, GatePass pass, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            pass.Code = GateRules.NewPassCode();
            db.GatePasses.Add(pass);
            try { await db.SaveChangesAsync(ct); return; }
            catch (DbUpdateException ex) when (attempt < 5 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    // Feature gate -------------------------------------------------------------------------------------------
    private static async ValueTask<object?> RequireGateFeature(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        if (!await FeatureFlags.IsEnabledAsync(db, Features.Gate, ctx.HttpContext.RequestAborted))
            return Results.Json(new { error = "feature_disabled", feature = Features.Gate }, statusCode: StatusCodes.Status403Forbidden);
        return await next(ctx);
    }

    // Handlers -----------------------------------------------------------------------------------------------
    private static async Task<IResult> LookupAsync(string? q, ClaimsPrincipal user, AppDbContext db, IObjectStorage storage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Results.Ok(new LookupResult([], [], []));
        var pattern = Like(q);
        var key = RegistryRules.PlateKey(q);
        var code = GateRules.NormalizeCode(q);
        var now = DateTimeOffset.UtcNow;
        var today = TimeOnly.FromDateTime(now.UtcDateTime);
        var weekday = now.UtcDateTime.DayOfWeek;

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(v => (key != "" && v.PlateKey.Contains(key)) || EF.Functions.ILike(v.Plate, pattern, "\\") || (v.StickerCode != null && EF.Functions.ILike(v.StickerCode, pattern, "\\")))
            .OrderBy(v => v.PlateKey).Take(20).ToListAsync(ct);

        var help = await db.DailyHelp.AsNoTracking()
            .Where(h => EF.Functions.ILike(h.Name, pattern, "\\") || (h.Phone != null && EF.Functions.ILike(h.Phone, pattern, "\\")))
            .OrderBy(h => h.Name).Take(20).ToListAsync(ct);

        var passes = await db.GatePasses.AsNoTracking()
            .Where(p => (code != "" && p.Code == code) || EF.Functions.ILike(p.VisitorName, pattern, "\\") || (p.PlateKey != null && key != "" && p.PlateKey.Contains(key)))
            .OrderByDescending(p => p.CreatedAt).Take(20).ToListAsync(ct);

        var houseIds = vehicles.Where(v => v.HouseId != null).Select(v => v.HouseId!.Value)
            .Concat(passes.Select(p => p.HouseId)).ToList();
        var labels = await HouseLabelsAsync(db, houseIds, ct);
        var helpHouses = (await db.DailyHelpHouses.AsNoTracking().Where(l => help.Select(h => h.Id).Contains(l.HelpId)).ToListAsync(ct)).ToLookup(l => l.HelpId, l => l.HouseId);

        return Results.Ok(new LookupResult(
            vehicles.Select(v => new VehicleHit(v.Id, v.Plate, v.Type, v.HouseId, v.HouseId is { } h && labels.TryGetValue(h, out var l) ? l : null, v.Status)).ToList(),
            help.Select(h => new HelpHit(h.Id, h.Name, h.Role, h.Status, RegistryRules.IsAllowed(h, weekday, today),
                h.PhotoKey is null ? null : storage.CreateDownloadUrl(h.PhotoKey), helpHouses[h.Id].ToList())).ToList(),
            passes.Select(p => new PassHit(p.Id, p.Code, p.VisitorName, p.Kind, p.HouseId, labels.GetValueOrDefault(p.HouseId), p.VehiclePlate, GateRules.Check(p, now).ToString())).ToList()));
    }

    private static async Task<IResult> RecordAsync(RecordEvent req, ClaimsPrincipal user, CurrentMemberships memberships, ITenantContext tenant, AppDbContext db, IObjectStorage storage, CancellationToken ct)
    {
        if (!GateRules.Directions.Contains(req.Direction)) return Results.BadRequest(new { error = "invalid_direction" });
        if (!GateRules.SubjectTypes.Contains(req.SubjectType)) return Results.BadRequest(new { error = "invalid_subject_type" });
        var decision = req.Decision ?? "allowed";
        if (!GateRules.Decisions.Contains(decision)) return Results.BadRequest(new { error = "invalid_decision" });

        // Validate any directory references so the log never points at nothing.
        if (req.GateId is { } g && !await db.Gates.AnyAsync(x => x.Id == g, ct)) return Results.BadRequest(new { error = "gate_not_found" });
        if (req.HouseId is { } ho && !await db.Houses.AnyAsync(x => x.Id == ho, ct)) return Results.BadRequest(new { error = "house_not_found" });
        if (req.VehicleId is { } ve && !await db.Vehicles.AnyAsync(x => x.Id == ve, ct)) return Results.BadRequest(new { error = "vehicle_not_found" });
        if (req.HelpId is { } he && !await db.DailyHelp.AnyAsync(x => x.Id == he, ct)) return Results.BadRequest(new { error = "help_not_found" });

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            phone = PhoneNumber.Normalize(req.Phone);
            if (phone is null) return Results.BadRequest(new { error = "invalid_phone" });
        }

        GatePass? pass = null;
        if (req.PassId is { } pid)
        {
            pass = await db.GatePasses.SingleOrDefaultAsync(p => p.Id == pid, ct);
            if (pass is null) return Results.BadRequest(new { error = "pass_not_found" });
        }

        var ev = new GateEvent
        {
            OrganizationId = tenant.OrganizationId!.Value,
            GateId = req.GateId ?? (await memberships.GetAsync(ct)).Select(m => m.GateId).FirstOrDefault(x => x != null),
            Direction = req.Direction,
            SubjectType = req.SubjectType,
            HouseId = req.HouseId,
            VehicleId = req.VehicleId,
            HelpId = req.HelpId,
            PassId = req.PassId,
            ResidentUserId = req.ResidentUserId,
            SubjectName = string.IsNullOrWhiteSpace(req.SubjectName) ? null : req.SubjectName.Trim(),
            Phone = phone,
            VehiclePlate = string.IsNullOrWhiteSpace(req.VehiclePlate) ? null : req.VehiclePlate.Trim().ToUpperInvariant(),
            PlateKey = string.IsNullOrWhiteSpace(req.VehiclePlate) ? null : RegistryRules.PlateKey(req.VehiclePlate),
            Decision = decision,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(),
            RecordedBy = Guid.Parse(user.FindFirstValue("sub")!),
            RecordedAt = DateTimeOffset.UtcNow,
        };
        db.GateEvents.Add(ev);

        // A pass is "spent" on an allowed entry, so MaxUses is honoured.
        if (pass is not null && decision == "allowed" && req.Direction == "in") pass.UsedCount++;

        await db.SaveChangesAsync(ct);
        return Results.Created($"/gate/events/{ev.Id}", (await EventDtosAsync(db, storage, [ev], ct))[0]);
    }

    private static async Task<IResult?> ApplyPassAsync(GatePass p, SavePass req, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.VisitorName)) return Results.BadRequest(new { error = "name_required" });
        if (!await db.Houses.AnyAsync(x => x.Id == req.HouseId, ct)) return Results.BadRequest(new { error = "house_not_found" });
        if (req.Kind is not null && !GateRules.PassKinds.Contains(req.Kind)) return Results.BadRequest(new { error = "invalid_kind" });
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            phone = PhoneNumber.Normalize(req.Phone);
            if (phone is null) return Results.BadRequest(new { error = "invalid_phone" });
        }
        var from = req.ValidFrom ?? DateTimeOffset.UtcNow;
        var to = req.ValidTo ?? from.AddHours(12);
        if (to <= from) return Results.BadRequest(new { error = "valid_to_must_be_after_from" });
        if (req.MaxUses is { } m && m <= 0) return Results.BadRequest(new { error = "invalid_max_uses" });

        p.VisitorName = req.VisitorName.Trim();
        p.Phone = phone;
        p.VehiclePlate = string.IsNullOrWhiteSpace(req.VehiclePlate) ? null : req.VehiclePlate.Trim().ToUpperInvariant();
        p.PlateKey = string.IsNullOrWhiteSpace(req.VehiclePlate) ? null : RegistryRules.PlateKey(req.VehiclePlate);
        p.HouseId = req.HouseId;
        p.Kind = req.Kind ?? p.Kind;
        p.Purpose = string.IsNullOrWhiteSpace(req.Purpose) ? null : req.Purpose.Trim();
        p.ValidFrom = from;
        p.ValidTo = to;
        p.MaxUses = req.MaxUses;
        return null;
    }

    // DTO mapping --------------------------------------------------------------------------------------------
    private static async Task<List<EventDto>> EventDtosAsync(AppDbContext db, IObjectStorage storage, List<GateEvent> rows, CancellationToken ct)
    {
        var labels = await HouseLabelsAsync(db, rows.Where(e => e.HouseId != null).Select(e => e.HouseId!.Value), ct);
        return rows.Select(e => new EventDto(
            e.Id, e.GateId, e.Direction, e.SubjectType, e.HouseId, e.HouseId is { } h && labels.TryGetValue(h, out var l) ? l : null,
            e.VehicleId, e.HelpId, e.PassId, e.SubjectName, e.Phone, e.VehiclePlate,
            e.PhotoKey is null ? null : storage.CreateDownloadUrl(e.PhotoKey), e.Decision, e.Note, e.Source, e.RecordedBy, e.RecordedAt)).ToList();
    }

    private static async Task<List<PassDto>> PassDtosAsync(AppDbContext db, List<GatePass> rows, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var labels = await HouseLabelsAsync(db, rows.Select(p => p.HouseId), ct);
        return rows.Select(p => new PassDto(
            p.Id, p.Code, p.VisitorName, p.Phone, p.VehiclePlate, p.HouseId, labels.GetValueOrDefault(p.HouseId), p.Kind, p.Purpose,
            p.ValidFrom, p.ValidTo, p.MaxUses, p.UsedCount, p.Status, GateRules.Check(p, now).ToString())).ToList();
    }

    private static async Task<Dictionary<Guid, string>> HouseLabelsAsync(AppDbContext db, IEnumerable<Guid> houseIds, CancellationToken ct)
    {
        var ids = houseIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var houses = await db.Houses.AsNoTracking().Where(h => ids.Contains(h.Id))
            .Join(db.Locations.AsNoTracking(), h => h.LocationId, l => l.Id, (h, l) => new { h.Id, l.Path }).ToListAsync(ct);
        var names = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return houses.ToDictionary(h => h.Id, h => string.Join(" › ",
            h.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Where(names.ContainsKey).Select(i => names[i])));
    }

    private static string Like(string q) => "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
