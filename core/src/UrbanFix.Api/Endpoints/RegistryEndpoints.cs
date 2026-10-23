using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Registry;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Vehicles (OR-08), daily help (OR-09), bulk import (OR-10), search and duplicate warnings (OR-11).</summary>
public static class RegistryEndpoints
{
    public sealed record VehicleDto(Guid Id, string Plate, string Type, Guid? HouseId, string? HouseLabel, string? StickerCode, string Status, string? Note);
    public sealed record SaveVehicle(string Plate, string? Type, Guid? HouseId, string? StickerCode, string? Status, string? Note);

    public sealed record HelpDto(
        Guid Id, string Name, string? Phone, string? IdNumberMasked, string? PhotoUrl, string Role, string Status, string[] AllowedDays,
        string? AllowedFrom, string? AllowedTo, IReadOnlyList<Guid> HouseIds, IReadOnlyList<string> Warnings);
    public sealed record SaveHelp(
        string Name, string? Phone, string? IdNumber, string? Role, string? Status, string[]? AllowedDays, string? AllowedFrom, string? AllowedTo, IReadOnlyList<Guid>? HouseIds);

    public sealed record PersonHit(Guid UserId, string DisplayName, string? Phone, string? Email, IReadOnlyList<Guid> HouseIds);
    public sealed record HouseHit(Guid Id, string Label);
    public sealed record SearchResult(IReadOnlyList<HouseHit> Houses, IReadOnlyList<PersonHit> People, IReadOnlyList<VehicleDto> Vehicles, IReadOnlyList<HelpDto> Help);

    public sealed record DuplicateGroup(string Kind, string Key, string Description, IReadOnlyList<string> Items);
    public sealed record Dismiss(string Kind, string Key);

    private static readonly string[] Quiet = [];

    public static void MapRegistryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Registry");
        var read = AuthPolicies.For(Permissions.ComplaintRead);
        var write = AuthPolicies.For(Permissions.HouseLink);

        // OR-08 ---------------------------------------------------------------------------------------------------
        g.MapGet("/vehicles", async (string? q, Guid? houseId, string? status, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Vehicles.AsNoTracking().AsQueryable();
            if (houseId is { } h) query = query.Where(v => v.HouseId == h);
            if (status is not null) query = query.Where(v => v.Status == status);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var key = RegistryRules.PlateKey(q);
                var pattern = Like(q);
                query = query.Where(v => (key != "" && v.PlateKey.Contains(key)) || EF.Functions.ILike(v.Plate, pattern, "\\") || (v.StickerCode != null && EF.Functions.ILike(v.StickerCode, pattern, "\\")));
            }
            var rows = await query.OrderBy(v => v.PlateKey).Take(200).ToListAsync(ct);
            return Results.Ok(await ToDtosAsync(db, rows, ct));
        }).Produces<List<VehicleDto>>(200).RequireAuthorization(read);

        g.MapPost("/vehicles", async (SaveVehicle req, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var v = new Vehicle { OrganizationId = tenant.OrganizationId!.Value };
            if (await ApplyAsync(v, req, db, ct) is { } error) return error;
            db.Vehicles.Add(v);
            return await SaveVehicleAsync(db, v, created: true, ct);
        }).Produces<VehicleDto>(201).RequireAuthorization(write);

        g.MapPut("/vehicles/{id:guid}", async (Guid id, SaveVehicle req, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.Vehicles.SingleOrDefaultAsync(v => v.Id == id, ct) is not { } v) return Results.NotFound();
            if (await ApplyAsync(v, req, db, ct) is { } error) return error;
            return await SaveVehicleAsync(db, v, created: false, ct);
        }).Produces<VehicleDto>(200).RequireAuthorization(write);

        // OR-09 ---------------------------------------------------------------------------------------------------
        g.MapGet("/daily-help", async (string? q, Guid? houseId, string? status, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            var query = db.DailyHelp.AsNoTracking().AsQueryable();
            if (status is not null) query = query.Where(h => h.Status == status);
            if (houseId is { } hid) query = query.Where(h => db.DailyHelpHouses.Any(l => l.HelpId == h.Id && l.HouseId == hid));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = Like(q);
                query = query.Where(h => EF.Functions.ILike(h.Name, pattern, "\\") || (h.Phone != null && EF.Functions.ILike(h.Phone, pattern, "\\")));
            }
            return Results.Ok(await HelpDtosAsync(db, storage, await query.OrderBy(h => h.Name).Take(200).ToListAsync(ct), null, ct));
        }).Produces<List<HelpDto>>(200).RequireAuthorization(read);

        g.MapPost("/daily-help", async (SaveHelp req, ITenantContext tenant, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            var h = new DailyHelp { OrganizationId = tenant.OrganizationId!.Value };
            if (await ApplyAsync(h, req, db, ct) is { } error) return error;
            var warnings = await HelpWarningsAsync(db, h, ct);
            db.DailyHelp.Add(h);
            foreach (var house in (req.HouseIds ?? []).Distinct()) db.DailyHelpHouses.Add(new DailyHelpHouse { OrganizationId = h.OrganizationId, HelpId = h.Id, HouseId = house });
            await db.SaveChangesAsync(ct);
            return Results.Created($"/daily-help/{h.Id}", (await HelpDtosAsync(db, storage, [h], warnings, ct))[0]);
        }).Produces<HelpDto>(201).RequireAuthorization(write);

        g.MapPut("/daily-help/{id:guid}", async (Guid id, SaveHelp req, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            if (await db.DailyHelp.SingleOrDefaultAsync(h => h.Id == id, ct) is not { } h) return Results.NotFound();
            if (await ApplyAsync(h, req, db, ct) is { } error) return error;
            var warnings = await HelpWarningsAsync(db, h, ct);
            if (req.HouseIds is { } wanted)
            {
                var existing = await db.DailyHelpHouses.Where(l => l.HelpId == id).ToListAsync(ct);
                db.DailyHelpHouses.RemoveRange(existing.Where(l => !wanted.Contains(l.HouseId)));
                foreach (var house in wanted.Distinct().Where(w => existing.All(l => l.HouseId != w)))
                    db.DailyHelpHouses.Add(new DailyHelpHouse { OrganizationId = h.OrganizationId, HelpId = id, HouseId = house });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok((await HelpDtosAsync(db, storage, [h], warnings, ct))[0]);
        }).Produces<HelpDto>(200).RequireAuthorization(write);

        g.MapPost("/daily-help/{id:guid}/photo", async (Guid id, [FromForm] IFormFile file, AppDbContext db, IObjectStorage storage, CancellationToken ct) =>
        {
            if (await db.DailyHelp.SingleOrDefaultAsync(h => h.Id == id, ct) is not { } h) return Results.NotFound();
            if (file.Length is 0 or > 5 * 1024 * 1024) return Results.BadRequest(new { error = "invalid_photo_size" });
            await using var stream = file.OpenReadStream();
            try { h.PhotoKey = (await storage.PutImageAsync(stream, ct)).Key; }
            catch (Exception ex) when (ex is SixLabors.ImageSharp.UnknownImageFormatException or SixLabors.ImageSharp.InvalidImageContentException or NotSupportedException)
            {
                return Results.BadRequest(new { error = "not_an_image" });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok((await HelpDtosAsync(db, storage, [h], null, ct))[0]);
        }).DisableAntiforgery().Produces<HelpDto>(200).RequireAuthorization(write);

        // OR-10 ---------------------------------------------------------------------------------------------------
        g.MapPost("/directory/import", async (string kind, bool? dryRun, HttpRequest request, DirectoryImporter importer, CancellationToken ct) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync(ct);
            if (text.Length > 5_000_000) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (!DirectoryImporter.Kinds.Contains(kind)) return Results.BadRequest(new { error = "unknown_kind", kinds = DirectoryImporter.Kinds });
            try { return Results.Ok(await importer.ImportAsync(kind, text, dryRun == true, ct)); }
            catch (FormatException ex) { return Results.BadRequest(new { error = "invalid_csv", detail = ex.Message }); }
        }).Accepts<string>("text/csv").Produces<ImportReport>(200).RequireAuthorization(AuthPolicies.For(Permissions.CatalogManage));

        // OR-11 ---------------------------------------------------------------------------------------------------
        g.MapGet("/directory/search", SearchAsync).Produces<SearchResult>(200).RequireAuthorization(read);
        g.MapGet("/directory/duplicates", DuplicatesAsync).Produces<List<DuplicateGroup>>(200).RequireAuthorization(write);
        g.MapPost("/directory/duplicates/dismiss", async (Dismiss req, ClaimsPrincipal user, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Kind) || string.IsNullOrWhiteSpace(req.Key)) return Results.BadRequest(new { error = "kind_and_key_required" });
            if (!await db.DirectoryDismissals.AnyAsync(d => d.Kind == req.Kind && d.Key == req.Key, ct))
            {
                db.DirectoryDismissals.Add(new DirectoryDismissal { OrganizationId = tenant.OrganizationId!.Value, Kind = req.Kind, Key = req.Key, DismissedBy = Guid.Parse(user.FindFirstValue("sub")!) });
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        }).RequireAuthorization(write);
    }

    // Helpers -------------------------------------------------------------------------------------------------------

    private static string Like(string q) => "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    private static async Task<Dictionary<Guid, string>> HouseLabelsAsync(AppDbContext db, IEnumerable<Guid> houseIds, CancellationToken ct)
    {
        var ids = houseIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var houses = await db.Houses.AsNoTracking().Where(h => ids.Contains(h.Id)).Join(db.Locations.AsNoTracking(), h => h.LocationId, l => l.Id, (h, l) => new { h.Id, l.Path }).ToListAsync(ct);
        var names = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Name }).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return houses.ToDictionary(h => h.Id, h => string.Join(" › ", h.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).Where(names.ContainsKey).Select(i => names[i])));
    }

    private static async Task<List<VehicleDto>> ToDtosAsync(AppDbContext db, List<Vehicle> rows, CancellationToken ct)
    {
        var labels = await HouseLabelsAsync(db, rows.Where(v => v.HouseId != null).Select(v => v.HouseId!.Value), ct);
        return rows.Select(v => new VehicleDto(v.Id, v.Plate, v.Type, v.HouseId, v.HouseId is { } h && labels.TryGetValue(h, out var l) ? l : null, v.StickerCode, v.Status, v.Note)).ToList();
    }

    private static async Task<IResult?> ApplyAsync(Vehicle v, SaveVehicle req, AppDbContext db, CancellationToken ct)
    {
        var key = RegistryRules.PlateKey(req.Plate);
        if (key.Length < 2) return Results.BadRequest(new { error = "plate_required" });
        if (req.Type is not null && !RegistryRules.VehicleTypes.Contains(req.Type)) return Results.BadRequest(new { error = "invalid_type" });
        if (req.Status is not null && !RegistryRules.Statuses.Contains(req.Status)) return Results.BadRequest(new { error = "invalid_status" });
        if (req.HouseId is { } h && !await db.Houses.AnyAsync(x => x.Id == h, ct)) return Results.BadRequest(new { error = "house_not_found" });
        v.Plate = req.Plate.Trim().ToUpperInvariant(); v.PlateKey = key; v.Type = req.Type ?? v.Type; v.HouseId = req.HouseId;
        v.StickerCode = string.IsNullOrWhiteSpace(req.StickerCode) ? null : req.StickerCode.Trim();
        v.Status = req.Status ?? v.Status; v.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        return null;
    }

    private static async Task<IResult> SaveVehicleAsync(AppDbContext db, Vehicle v, bool created, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            db.ChangeTracker.Clear();
            var sticker = pg.ConstraintName?.Contains("sticker") == true;
            var other = sticker ? null : await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.PlateKey == v.PlateKey && x.Id != v.Id, ct);
            return Results.Conflict(new { error = sticker ? "duplicate_sticker" : "duplicate_plate", existingId = other?.Id, existingHouseId = other?.HouseId });
        }
        var dto = (await ToDtosAsync(db, [v], ct))[0];
        return created ? Results.Created($"/vehicles/{v.Id}", dto) : Results.Ok(dto);
    }

    private static async Task<IResult?> ApplyAsync(DailyHelp h, SaveHelp req, AppDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            phone = PhoneNumber.Normalize(req.Phone);
            if (phone is null) return Results.BadRequest(new { error = "invalid_phone" });
        }
        if (req.Role is not null && !RegistryRules.HelpRoles.Contains(req.Role)) return Results.BadRequest(new { error = "invalid_role" });
        if (req.Status is not null && !RegistryRules.Statuses.Contains(req.Status)) return Results.BadRequest(new { error = "invalid_status" });
        if (req.AllowedDays is { } days && !days.All(RegistryRules.Days.Contains)) return Results.BadRequest(new { error = "invalid_days" });
        if (!RegistryRules.TryParseTime(req.AllowedFrom, out var from) || !RegistryRules.TryParseTime(req.AllowedTo, out var to)) return Results.BadRequest(new { error = "invalid_time" });
        if (from is { } f && to is { } t && t <= f) return Results.BadRequest(new { error = "to_must_be_after_from" });
        foreach (var house in req.HouseIds ?? []) if (!await db.Houses.AnyAsync(x => x.Id == house, ct)) return Results.BadRequest(new { error = "house_not_found" });

        h.Name = req.Name.Trim(); h.Phone = phone; h.Role = req.Role ?? h.Role; h.Status = req.Status ?? h.Status;
        if (req.IdNumber is not null) h.IdNumberMasked = RegistryRules.MaskId(req.IdNumber); // the full number is never stored
        if (req.AllowedDays is not null) h.AllowedDays = req.AllowedDays.Distinct().OrderBy(d => Array.IndexOf(RegistryRules.Days, d)).ToArray();
        h.AllowedFrom = from; h.AllowedTo = to;
        return null;
    }

    /// <summary>Things staff may want to look at when a record is saved (OR-11). The save still goes ahead.</summary>
    private static async Task<List<string>> HelpWarningsAsync(AppDbContext db, DailyHelp h, CancellationToken ct)
    {
        var warnings = new List<string>();
        if (h.Phone is null) return warnings;
        if (await db.DailyHelp.AnyAsync(x => x.Phone == h.Phone && x.Id != h.Id, ct)) warnings.Add("phone_used_by_other_help");
        if (await db.Users.AnyAsync(u => u.Phone == h.Phone, ct)) warnings.Add("phone_belongs_to_a_resident");
        return warnings;
    }

    private static async Task<List<HelpDto>> HelpDtosAsync(AppDbContext db, IObjectStorage storage, List<DailyHelp> rows, IReadOnlyList<string>? warnings, CancellationToken ct)
    {
        var ids = rows.Select(r => r.Id).ToList();
        var links = (await db.DailyHelpHouses.AsNoTracking().Where(l => ids.Contains(l.HelpId)).ToListAsync(ct)).ToLookup(l => l.HelpId, l => l.HouseId);
        return rows.Select(h => new HelpDto(h.Id, h.Name, h.Phone, h.IdNumberMasked, h.PhotoKey is null ? null : storage.CreateDownloadUrl(h.PhotoKey), h.Role, h.Status,
            h.AllowedDays, h.AllowedFrom?.ToString("HH:mm"), h.AllowedTo?.ToString("HH:mm"), links[h.Id].ToList(), warnings ?? Quiet)).ToList();
    }

    private static async Task<IResult> SearchAsync(string? q, AppDbContext db, IObjectStorage storage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Results.Ok(new SearchResult([], [], [], []));
        var pattern = Like(q);
        var key = RegistryRules.PlateKey(q);

        var houseRows = await db.Houses.AsNoTracking().Join(db.Locations.AsNoTracking(), h => h.LocationId, l => l.Id, (h, l) => new { h.Id, l.Name }).Where(x => EF.Functions.ILike(x.Name, pattern, "\\")).Take(20).ToListAsync(ct);
        var labels = await HouseLabelsAsync(db, houseRows.Select(h => h.Id), ct);

        var people = await (from u in db.Users.AsNoTracking()
                            where db.Memberships.Any(m => m.UserId == u.Id && m.IsActive && m.Role == Role.Resident)
                                  && (EF.Functions.ILike(u.DisplayName, pattern, "\\") || (u.Phone != null && EF.Functions.ILike(u.Phone, pattern, "\\")) || EF.Functions.ILike(u.Email, pattern, "\\"))
                            orderby u.DisplayName
                            select new { u.Id, u.DisplayName, u.Phone, u.Email }).Take(20).ToListAsync(ct);
        var userIds = people.Select(p => p.Id).ToList();
        var homes = (await db.ResidentLinks.AsNoTracking().Where(r => userIds.Contains(r.UserId) && r.ToDate == null).Select(r => new { r.UserId, r.HouseId }).ToListAsync(ct)).ToLookup(r => r.UserId, r => r.HouseId);

        var vehicles = await db.Vehicles.AsNoTracking().Where(v => (key != "" && v.PlateKey.Contains(key)) || EF.Functions.ILike(v.Plate, pattern, "\\") || (v.StickerCode != null && EF.Functions.ILike(v.StickerCode, pattern, "\\"))).OrderBy(v => v.PlateKey).Take(20).ToListAsync(ct);
        var help = await db.DailyHelp.AsNoTracking().Where(h => EF.Functions.ILike(h.Name, pattern, "\\") || (h.Phone != null && EF.Functions.ILike(h.Phone, pattern, "\\"))).OrderBy(h => h.Name).Take(20).ToListAsync(ct);

        return Results.Ok(new SearchResult(
            houseRows.Select(h => new HouseHit(h.Id, labels.GetValueOrDefault(h.Id, h.Name))).ToList(),
            people.Select(p => new PersonHit(p.Id, p.DisplayName, p.Phone, p.Email.EndsWith(".invalid") ? null : p.Email, homes[p.Id].ToList())).ToList(),
            await ToDtosAsync(db, vehicles, ct), await HelpDtosAsync(db, storage, help, null, ct)));
    }

    /// <summary>Records that look like the same thing entered twice, for staff to check (OR-11). Ones staff dismissed are left out.</summary>
    private static async Task<IResult> DuplicatesAsync(AppDbContext db, CancellationToken ct)
    {
        var dismissed = (await db.DirectoryDismissals.AsNoTracking().Select(d => d.Kind + "|" + d.Key).ToListAsync(ct)).ToHashSet();
        var groups = new List<DuplicateGroup>();

        var help = await db.DailyHelp.AsNoTracking().Where(h => h.Phone != null).Select(h => new { h.Id, h.Name, h.Phone }).ToListAsync(ct);
        foreach (var g in help.GroupBy(h => h.Phone!).Where(g => g.Count() > 1))
            groups.Add(new DuplicateGroup("help_same_phone", g.Key, $"{g.Count()} daily-help records share the number {g.Key}", g.Select(h => $"{h.Name} ({h.Id})").ToList()));

        var residentPhones = (await db.Users.AsNoTracking().Where(u => u.Phone != null && db.Memberships.Any(m => m.UserId == u.Id && m.Role == Role.Resident)).Select(u => new { u.Phone, u.DisplayName }).ToListAsync(ct)).ToLookup(u => u.Phone!);
        foreach (var h in help.Where(h => residentPhones.Contains(h.Phone!)).GroupBy(h => h.Phone!))
            groups.Add(new DuplicateGroup("help_resident_phone", h.Key, $"A daily-help record and a resident share the number {h.Key}", h.Select(x => $"{x.Name} (help)").Concat(residentPhones[h.Key].Select(r => $"{r.DisplayName} (resident)")).ToList()));

        // Owners and tenants live in one house; family may be in more. Two current homes is worth a look.
        var homes = await db.ResidentLinks.AsNoTracking().Where(r => r.ToDate == null && r.Relation != "family").Select(r => new { r.UserId, r.HouseId }).ToListAsync(ct);
        var multi = homes.GroupBy(h => h.UserId).Where(g => g.Select(x => x.HouseId).Distinct().Count() > 1).ToList();
        if (multi.Count > 0)
        {
            var names = await db.Users.AsNoTracking().Where(u => multi.Select(m => m.Key).Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var labels = await HouseLabelsAsync(db, homes.Select(h => h.HouseId), ct);
            foreach (var g in multi)
                groups.Add(new DuplicateGroup("resident_two_houses", g.Key.ToString(), $"{names.GetValueOrDefault(g.Key, "A resident")} is an owner or tenant of more than one house",
                    g.Select(x => labels.GetValueOrDefault(x.HouseId, x.HouseId.ToString())).Distinct().ToList()));
        }

        return Results.Ok(groups.Where(g => !dismissed.Contains(g.Kind + "|" + g.Key)).OrderBy(g => g.Kind).ThenBy(g => g.Key).ToList());
    }
}
