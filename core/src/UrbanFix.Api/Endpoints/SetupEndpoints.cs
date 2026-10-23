using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Organization configuration (OR-01 to OR-07).</summary>
public static class SetupEndpoints
{
    public sealed record SettingsDto(string? DisplayName, string Timezone, string Locale, string LocationTemplate, string? PrimaryColor, string? LogoKey);
    public sealed record LocationDto(Guid Id, Guid? ParentId, string Kind, string Name, string? NameUr, string Path, int Depth, double? Latitude, double? Longitude, bool IsActive);
    public sealed record CreateLocation(Guid? ParentId, string Kind, string Name, string? NameUr, double? Latitude, double? Longitude);
    public sealed record UpdateLocation(string? Name, string? NameUr, double? Latitude, double? Longitude, bool? IsActive);
    public sealed record DepartmentDto(Guid Id, string Name, string? NameUr, bool IsActive);
    public sealed record SaveDepartment(string Name, string? NameUr, bool? IsActive);
    public sealed record TeamDto(Guid Id, Guid DepartmentId, string Name, bool IsActive, IReadOnlyList<TeamMemberDto> Members);
    public sealed record TeamMemberDto(Guid Id, Guid UserId, bool IsSupervisor);
    public sealed record CreateTeam(Guid DepartmentId, string Name);
    public sealed record AddTeamMember(Guid UserId, bool IsSupervisor);
    public sealed record CategoryDto(Guid Id, string Key, string NameEn, string? NameUr, Guid? DepartmentId, string DefaultPriority, string EvidenceRule, JsonElement Checklist, bool IsActive, bool RequiresCosts);
    public sealed record SaveCategory(string Key, string NameEn, string? NameUr, Guid? DepartmentId, string? DefaultPriority, string? EvidenceRule, string[]? Checklist, bool? IsActive, bool? RequiresCosts = null);
    public sealed record HouseDto(Guid Id, Guid LocationId, string LocationPath, decimal? PlotSizeSqYd, string? HouseCategory, string Status);
    public sealed record CreateHouse(Guid LocationId, decimal? PlotSizeSqYd, string? HouseCategory, string? Status);
    public sealed record ResidentLinkDto(Guid Id, Guid HouseId, Guid UserId, string Relation, DateOnly FromDate, DateOnly? ToDate);
    public sealed record CreateResidentLink(Guid UserId, string Relation, DateOnly? FromDate);
    public sealed record EndResidentLink(DateOnly? ToDate);

    public sealed record LandmarkDto(Guid Id, string Name, string? NameUr, string[] Aliases, Guid? LocationId, double? Latitude, double? Longitude, bool IsActive);
    public sealed record SaveLandmark(string Name, string? NameUr, string[]? Aliases, Guid? LocationId, double? Latitude, double? Longitude, bool? IsActive);

    private static readonly string[] Priorities = ["Low", "Normal", "High", "Urgent"];
    private static readonly string[] EvidenceRules = ["photo", "checklist", "note", "none"];
    private static readonly string[] Relations = ["owner", "tenant", "family"];
    private static readonly string[] HouseStatuses = ["occupied", "vacant", "under_construction"];

    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var manage = AuthPolicies.For(Permissions.CatalogManage);

        // OR-01 ------------------------------------------------------------------------------------------
        app.MapGet("/organization/settings", async (AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(ToDto(await GetOrCreateSettings(db, tenant.OrganizationId!.Value, ct))))
            .WithTags("Setup").Produces<SettingsDto>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapPut("/organization/settings", async (SettingsDto req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!LocationTemplates.Levels.ContainsKey(req.LocationTemplate)) return Results.BadRequest(new { error = "unknown_location_template" });
            if (req.Locale is not ("en" or "ur")) return Results.BadRequest(new { error = "unsupported_locale" });
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(req.Timezone); } catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return Results.BadRequest(new { error = "unknown_timezone" }); }
            if (req.PrimaryColor is not null && !System.Text.RegularExpressions.Regex.IsMatch(req.PrimaryColor, "^#[0-9A-Fa-f]{6}$")) return Results.BadRequest(new { error = "invalid_color" });

            var s = await GetOrCreateSettings(db, tenant.OrganizationId!.Value, ct);
            if (s.LocationTemplate != req.LocationTemplate && await db.Locations.AnyAsync(ct))
                return Results.Conflict(new { error = "template_locked", detail = "Locations already exist; the template can no longer change." });

            s.DisplayName = req.DisplayName; s.Timezone = req.Timezone; s.Locale = req.Locale;
            s.LocationTemplate = req.LocationTemplate; s.PrimaryColor = req.PrimaryColor; s.LogoKey = req.LogoKey;
            s.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(s));
        }).WithTags("Setup").Produces<SettingsDto>(200).RequireAuthorization(AuthPolicies.For(Permissions.OrganizationSettings));

        // OR-02 ------------------------------------------------------------------------------------------
        app.MapGet("/locations", async (Guid? parentId, bool? includeInactive, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Locations.AsNoTracking().Where(l => l.ParentId == parentId);
            if (includeInactive != true) q = q.Where(l => l.IsActive);
            return Results.Ok((await q.OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(ct)).Select(ToDto));
        }).WithTags("Setup").Produces<List<LocationDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapGet("/locations/tree", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Locations.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Path).ToListAsync(ct)).Select(ToDto)))
            .WithTags("Setup").Produces<List<LocationDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapGet("/locations/{id:guid}/descendants", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var root = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
            if (root is null) return Results.NotFound();
            var rows = await db.Locations.AsNoTracking().Where(l => l.Path.StartsWith(root.Path) && l.Id != id && l.IsActive).OrderBy(l => l.Path).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto));
        }).WithTags("Setup").Produces<List<LocationDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapPost("/locations", async (CreateLocation req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            var settings = await GetOrCreateSettings(db, tenant.OrganizationId!.Value, ct);
            Location? parent = null;
            if (req.ParentId is { } pid)
            {
                parent = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == pid, ct);
                if (parent is null) return Results.BadRequest(new { error = "parent_not_found" });
            }
            if (LocationTemplates.Validate(settings.LocationTemplate, parent?.Kind, req.Kind) is { } problem)
                return Results.BadRequest(new { error = "invalid_level", detail = problem });

            var loc = new Location
            {
                OrganizationId = tenant.OrganizationId!.Value, ParentId = parent?.Id, Kind = req.Kind, Name = req.Name.Trim(), NameUr = req.NameUr,
                Latitude = req.Latitude, Longitude = req.Longitude,
            };
            loc.Path = (parent?.Path ?? "/") + loc.Id + "/";
            loc.Depth = (parent?.Depth ?? -1) + 1;
            db.Locations.Add(loc);
            return await SaveOrConflict(db, ct, () => Results.Created($"/locations/{loc.Id}", ToDto(loc)), "duplicate_name");
        }).WithTags("Setup").Produces<LocationDto>(201).RequireAuthorization(manage);

        app.MapPut("/locations/{id:guid}", async (Guid id, UpdateLocation req, AppDbContext db, CancellationToken ct) =>
        {
            var loc = await db.Locations.SingleOrDefaultAsync(l => l.Id == id, ct);
            if (loc is null) return Results.NotFound();
            if (req.Name is { } n) loc.Name = n.Trim();
            if (req.NameUr is not null) loc.NameUr = req.NameUr;
            if (req.Latitude is not null) loc.Latitude = req.Latitude;
            if (req.Longitude is not null) loc.Longitude = req.Longitude;
            if (req.IsActive is { } active) loc.IsActive = active;
            return await SaveOrConflict(db, ct, () => Results.Ok(ToDto(loc)), "duplicate_name");
        }).WithTags("Setup").Produces<LocationDto>(200).RequireAuthorization(manage);

        // OR-03 ------------------------------------------------------------------------------------------
        app.MapGet("/landmarks", async (string? q, Guid? locationId, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Landmarks.AsNoTracking().Where(l => l.IsActive);
            if (locationId is { } lid) query = query.Where(l => l.LocationId == lid);
            var rows = await query.OrderBy(l => l.Name).ToListAsync(ct);
            // Search the name, the Urdu name and every alias; small lists, so filter in memory for case-insensitive Unicode matching.
            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = q.Trim();
                rows = rows.Where(l => l.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
                    || (l.NameUr?.Contains(needle, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || l.Aliases.Any(a => a.Contains(needle, StringComparison.CurrentCultureIgnoreCase))).ToList();
            }
            return Results.Ok(rows.Select(ToDto));
        }).WithTags("Setup").Produces<List<LandmarkDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapPost("/landmarks", async (SaveLandmark req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            if (req.LocationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid, ct)) return Results.BadRequest(new { error = "location_not_found" });
            var l = new Landmark { OrganizationId = tenant.OrganizationId!.Value };
            ApplyLandmark(l, req);
            db.Landmarks.Add(l);
            return await SaveOrConflict(db, ct, () => Results.Created($"/landmarks/{l.Id}", ToDto(l)), "duplicate_name");
        }).WithTags("Setup").Produces<LandmarkDto>(201).RequireAuthorization(manage);

        app.MapPut("/landmarks/{id:guid}", async (Guid id, SaveLandmark req, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            var l = await db.Landmarks.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (l is null) return Results.NotFound();
            if (req.LocationId is { } lid && !await db.Locations.AnyAsync(x => x.Id == lid, ct)) return Results.BadRequest(new { error = "location_not_found" });
            ApplyLandmark(l, req);
            return await SaveOrConflict(db, ct, () => Results.Ok(ToDto(l)), "duplicate_name");
        }).WithTags("Setup").Produces<LandmarkDto>(200).RequireAuthorization(manage);

        // OR-04 ------------------------------------------------------------------------------------------
        app.MapGet("/departments", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Departments.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct)).Select(d => new DepartmentDto(d.Id, d.Name, d.NameUr, d.IsActive))))
            .WithTags("Setup").Produces<List<DepartmentDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapPost("/departments", async (SaveDepartment req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "name_required" });
            var d = new Department { OrganizationId = tenant.OrganizationId!.Value, Name = req.Name.Trim(), NameUr = req.NameUr };
            db.Departments.Add(d);
            return await SaveOrConflict(db, ct, () => Results.Created($"/departments/{d.Id}", new DepartmentDto(d.Id, d.Name, d.NameUr, d.IsActive)), "duplicate_name");
        }).WithTags("Setup").Produces<DepartmentDto>(201).RequireAuthorization(manage);

        app.MapPut("/departments/{id:guid}", async (Guid id, SaveDepartment req, AppDbContext db, CancellationToken ct) =>
        {
            var d = await db.Departments.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            d.Name = req.Name.Trim(); d.NameUr = req.NameUr; if (req.IsActive is { } a) d.IsActive = a;
            return await SaveOrConflict(db, ct, () => Results.Ok(new DepartmentDto(d.Id, d.Name, d.NameUr, d.IsActive)), "duplicate_name");
        }).WithTags("Setup").Produces<DepartmentDto>(200).RequireAuthorization(manage);

        app.MapGet("/teams", async (AppDbContext db, CancellationToken ct) =>
        {
            var teams = await db.Teams.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct);
            var members = await db.TeamMembers.AsNoTracking().ToListAsync(ct);
            return Results.Ok(teams.Select(t => new TeamDto(t.Id, t.DepartmentId, t.Name, t.IsActive,
                members.Where(m => m.TeamId == t.Id).Select(m => new TeamMemberDto(m.Id, m.UserId, m.IsSupervisor)).ToList())));
        }).WithTags("Setup").Produces<List<TeamDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapPost("/teams", async (CreateTeam req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!await db.Departments.AnyAsync(d => d.Id == req.DepartmentId, ct)) return Results.BadRequest(new { error = "department_not_found" });
            var t = new Team { OrganizationId = tenant.OrganizationId!.Value, DepartmentId = req.DepartmentId, Name = req.Name.Trim() };
            db.Teams.Add(t);
            return await SaveOrConflict(db, ct, () => Results.Created($"/teams/{t.Id}", new TeamDto(t.Id, t.DepartmentId, t.Name, t.IsActive, [])), "duplicate_name");
        }).WithTags("Setup").Produces<TeamDto>(201).RequireAuthorization(manage);

        app.MapPost("/teams/{teamId:guid}/members", async (Guid teamId, AddTeamMember req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!await db.Teams.AnyAsync(t => t.Id == teamId, ct)) return Results.NotFound();
            if (!await db.Memberships.AnyAsync(m => m.UserId == req.UserId && m.IsActive, ct)) return Results.BadRequest(new { error = "user_not_in_organization" });
            var m = new TeamMember { OrganizationId = tenant.OrganizationId!.Value, TeamId = teamId, UserId = req.UserId, IsSupervisor = req.IsSupervisor };
            db.TeamMembers.Add(m);
            return await SaveOrConflict(db, ct, () => Results.Created($"/teams/{teamId}/members/{m.Id}", new TeamMemberDto(m.Id, m.UserId, m.IsSupervisor)), "already_member");
        }).WithTags("Setup").Produces<TeamMemberDto>(201).RequireAuthorization(manage);

        app.MapDelete("/teams/{teamId:guid}/members/{memberId:guid}", async (Guid teamId, Guid memberId, AppDbContext db, CancellationToken ct) =>
        {
            var m = await db.TeamMembers.SingleOrDefaultAsync(x => x.Id == memberId && x.TeamId == teamId, ct);
            if (m is null) return Results.NotFound();
            db.TeamMembers.Remove(m);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).WithTags("Setup").RequireAuthorization(manage);

        // OR-05 ------------------------------------------------------------------------------------------
        app.MapGet("/categories", async (bool? includeInactive, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Categories.AsNoTracking().AsQueryable();
            if (includeInactive != true) q = q.Where(c => c.IsActive);
            return Results.Ok((await q.OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync(ct)).Select(ToDto));
        }).WithTags("Setup").Produces<List<CategoryDto>>(200).RequireAuthorization(AuthPolicies.Verified);

        app.MapPost("/categories", async (SaveCategory req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (Validate(req) is { } bad) return bad;
            var c = new Category { OrganizationId = tenant.OrganizationId!.Value };
            Apply(c, req);
            db.Categories.Add(c);
            return await SaveOrConflict(db, ct, () => Results.Created($"/categories/{c.Id}", ToDto(c)), "duplicate_key");
        }).WithTags("Setup").Produces<CategoryDto>(201).RequireAuthorization(manage);

        app.MapPut("/categories/{id:guid}", async (Guid id, SaveCategory req, AppDbContext db, CancellationToken ct) =>
        {
            if (Validate(req) is { } bad) return bad;
            var c = await db.Categories.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound();
            Apply(c, req);
            return await SaveOrConflict(db, ct, () => Results.Ok(ToDto(c)), "duplicate_key");
        }).WithTags("Setup").Produces<CategoryDto>(200).RequireAuthorization(manage);

        app.MapPost("/categories/seed-templates", async (AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(new { added = await SeedCategoriesAsync(db, tenant.OrganizationId!.Value, ct) }))
            .WithTags("Setup").RequireAuthorization(manage);

        // OR-06, OR-07 -----------------------------------------------------------------------------------
        app.MapGet("/houses", async (Guid? underLocationId, AppDbContext db, CancellationToken ct) =>
        {
            var q = db.Houses.AsNoTracking().Join(db.Locations, h => h.LocationId, l => l.Id, (h, l) => new { h, l.Path });
            if (underLocationId is { } root)
            {
                var prefix = await db.Locations.AsNoTracking().Where(l => l.Id == root).Select(l => l.Path).SingleOrDefaultAsync(ct);
                if (prefix is null) return Results.NotFound();
                q = q.Where(x => x.Path.StartsWith(prefix));
            }
            return Results.Ok((await q.OrderBy(x => x.Path).ToListAsync(ct)).Select(x => ToDto(x.h, x.Path)));
        }).WithTags("Setup").Produces<List<HouseDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));

        app.MapPost("/houses", async (CreateHouse req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var settings = await GetOrCreateSettings(db, tenant.OrganizationId!.Value, ct);
            var loc = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == req.LocationId, ct);
            if (loc is null) return Results.BadRequest(new { error = "location_not_found" });
            if (loc.Kind != LocationTemplates.LeafKind(settings.LocationTemplate))
                return Results.BadRequest(new { error = "not_a_house_level", detail = $"Houses attach to '{LocationTemplates.LeafKind(settings.LocationTemplate)}' locations." });
            if (req.Status is not null && !HouseStatuses.Contains(req.Status)) return Results.BadRequest(new { error = "invalid_status" });
            if (req.PlotSizeSqYd is <= 0) return Results.BadRequest(new { error = "invalid_plot_size" });

            var h = new House { OrganizationId = tenant.OrganizationId!.Value, LocationId = loc.Id, PlotSizeSqYd = req.PlotSizeSqYd, HouseCategory = req.HouseCategory, Status = req.Status ?? "occupied" };
            db.Houses.Add(h);
            return await SaveOrConflict(db, ct, () => Results.Created($"/houses/{h.Id}", ToDto(h, loc.Path)), "location_already_has_house");
        }).WithTags("Setup").Produces<HouseDto>(201).RequireAuthorization(manage);

        app.MapGet("/houses/{houseId:guid}/residents", async (Guid houseId, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.ResidentLinks.AsNoTracking().Where(r => r.HouseId == houseId).OrderByDescending(r => r.FromDate).ToListAsync(ct)).Select(ToDto)))
            .WithTags("Setup").Produces<List<ResidentLinkDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));

        app.MapPost("/houses/{houseId:guid}/residents", async (Guid houseId, CreateResidentLink req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (!Relations.Contains(req.Relation)) return Results.BadRequest(new { error = "invalid_relation" });
            if (!await db.Houses.AnyAsync(h => h.Id == houseId, ct)) return Results.NotFound();
            var org = tenant.OrganizationId!.Value;

            // A linked person is a resident of this organization: give them the Resident role if they have none yet.
            if (!await db.Memberships.AnyAsync(m => m.UserId == req.UserId && m.IsActive, ct))
            {
                if (!await db.Users.AnyAsync(u => u.Id == req.UserId, ct)) return Results.BadRequest(new { error = "user_not_found" });
                db.Memberships.Add(new Membership { OrganizationId = org, UserId = req.UserId, Role = Role.Resident });
            }
            var link = new ResidentLink { OrganizationId = org, HouseId = houseId, UserId = req.UserId, Relation = req.Relation, FromDate = req.FromDate ?? DateOnly.FromDateTime(DateTime.UtcNow) };
            db.ResidentLinks.Add(link);
            return await SaveOrConflict(db, ct, () => Results.Created($"/resident-links/{link.Id}", ToDto(link)), "house_already_has_current_" + req.Relation);
        }).WithTags("Setup").Produces<ResidentLinkDto>(201).RequireAuthorization(AuthPolicies.For(Permissions.HouseLink));

        // Moving out ends the link; the row stays as history.
        app.MapPost("/resident-links/{id:guid}/end", async (Guid id, EndResidentLink req, AppDbContext db, CancellationToken ct) =>
        {
            var link = await db.ResidentLinks.SingleOrDefaultAsync(r => r.Id == id, ct);
            if (link is null) return Results.NotFound();
            if (link.ToDate is not null) return Results.Conflict(new { error = "already_ended" });
            var end = req.ToDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            if (end < link.FromDate) return Results.BadRequest(new { error = "end_before_start" });
            link.ToDate = end;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(link));
        }).WithTags("Setup").Produces<ResidentLinkDto>(200).RequireAuthorization(AuthPolicies.For(Permissions.HouseLink));
    }

    // Shared helpers ---------------------------------------------------------------------------------------

    public static async Task<OrganizationSettings> GetOrCreateSettings(AppDbContext db, Guid orgId, CancellationToken ct)
    {
        var s = await db.OrgSettings.SingleOrDefaultAsync(x => x.OrganizationId == orgId, ct);
        if (s is not null) return s;
        s = new OrganizationSettings { OrganizationId = orgId };
        db.OrgSettings.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public static async Task<int> SeedCategoriesAsync(AppDbContext db, Guid orgId, CancellationToken ct)
    {
        var existing = (await db.Categories.Select(c => c.Key).ToListAsync(ct)).ToHashSet();
        var order = existing.Count;
        var added = 0;
        foreach (var seed in CategoryTemplates.All.Where(s => !existing.Contains(s.Key)))
        {
            db.Categories.Add(new Category
            {
                OrganizationId = orgId, Key = seed.Key, NameEn = seed.NameEn, NameUr = seed.NameUr, DefaultPriority = seed.Priority,
                EvidenceRule = seed.Evidence, Checklist = JsonSerializer.Serialize(seed.Checklist), SortOrder = order++,
            });
            added++;
        }
        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }

    private static async Task<IResult> SaveOrConflict(AppDbContext db, CancellationToken ct, Func<IResult> ok, string conflictCode)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return ok();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Conflict(new { error = conflictCode });
        }
    }

    private static IResult? Validate(SaveCategory r)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(r.Key ?? "", "^[a-z0-9_\\-]{1,64}$")) return Results.BadRequest(new { error = "invalid_key" });
        if (string.IsNullOrWhiteSpace(r.NameEn)) return Results.BadRequest(new { error = "name_required" });
        if (r.DefaultPriority is not null && !Priorities.Contains(r.DefaultPriority)) return Results.BadRequest(new { error = "invalid_priority" });
        if (r.EvidenceRule is not null && !EvidenceRules.Contains(r.EvidenceRule)) return Results.BadRequest(new { error = "invalid_evidence_rule" });
        return null;
    }

    private static void Apply(Category c, SaveCategory r)
    {
        c.Key = r.Key; c.NameEn = r.NameEn.Trim(); c.NameUr = r.NameUr; c.DepartmentId = r.DepartmentId;
        c.DefaultPriority = r.DefaultPriority ?? "Normal"; c.EvidenceRule = r.EvidenceRule ?? "photo";
        c.Checklist = JsonSerializer.Serialize(r.Checklist ?? []);
        if (r.RequiresCosts is { } rc) c.RequiresCosts = rc;
        if (r.IsActive is { } a) c.IsActive = a;
    }

    private static void ApplyLandmark(Landmark l, SaveLandmark r)
    {
        l.Name = r.Name.Trim(); l.NameUr = r.NameUr; l.LocationId = r.LocationId; l.Latitude = r.Latitude; l.Longitude = r.Longitude;
        l.Aliases = (r.Aliases ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (r.IsActive is { } a) l.IsActive = a;
    }

    private static LandmarkDto ToDto(Landmark l) => new(l.Id, l.Name, l.NameUr, l.Aliases, l.LocationId, l.Latitude, l.Longitude, l.IsActive);
    private static SettingsDto ToDto(OrganizationSettings s) => new(s.DisplayName, s.Timezone, s.Locale, s.LocationTemplate, s.PrimaryColor, s.LogoKey);
    private static LocationDto ToDto(Location l) => new(l.Id, l.ParentId, l.Kind, l.Name, l.NameUr, l.Path, l.Depth, l.Latitude, l.Longitude, l.IsActive);
    private static CategoryDto ToDto(Category c) => new(c.Id, c.Key, c.NameEn, c.NameUr, c.DepartmentId, c.DefaultPriority, c.EvidenceRule, JsonDocument.Parse(c.Checklist).RootElement.Clone(), c.IsActive, c.RequiresCosts);
    private static HouseDto ToDto(House h, string path) => new(h.Id, h.LocationId, path, h.PlotSizeSqYd, h.HouseCategory, h.Status);
    private static ResidentLinkDto ToDto(ResidentLink r) => new(r.Id, r.HouseId, r.UserId, r.Relation, r.FromDate, r.ToDate);
}
