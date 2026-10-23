using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Registry;

public sealed record ImportIssue(int Line, string Field, string Message);

public sealed record ImportReport(string Kind, bool DryRun, int Rows, int Created, int Updated, int Unchanged, IReadOnlyList<ImportIssue> Issues);

/// <summary>
/// Bulk import of houses, residents, vehicles and daily help from CSV (OR-10). Every row is checked on its own: a bad row is
/// reported by line number and skipped, good rows go in. Rows are matched to what already exists (house by its place in the tree,
/// resident by phone, vehicle by plate, help by phone or name), so loading the same file again changes nothing.
/// A dry run does everything except save.
/// </summary>
public sealed class DirectoryImporter(AppDbContext db, ITenantContext tenant, PasswordService passwords)
{
    public static readonly string[] Kinds = ["houses", "residents", "vehicles", "help"];
    public const int MaxRows = 5000;
    private static readonly string[] HouseStatuses = ["occupied", "vacant", "under_construction"];
    private static readonly string[] Relations = ["owner", "tenant", "family"];

    private enum Outcome { Created, Updated, Unchanged }

    private sealed class RowError(string field, string message) : Exception(message)
    {
        public string Field { get; } = field;
    }

    private bool _dryRun;

    /// <summary>
    /// Saves what a row has built so far, so rows that must exist first (a place before its house, a person before their link) do.
    /// EF is not told these tables depend on each other. A dry run never saves.
    /// </summary>
    private Task FlushAsync(CancellationToken ct) => _dryRun ? Task.CompletedTask : db.SaveChangesAsync(ct);

    private Guid Org => tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");

    public async Task<ImportReport> ImportAsync(string kind, string csv, bool dryRun, CancellationToken ct)
    {
        _dryRun = dryRun;
        var table = Csv.Parse(csv);
        if (table.Rows.Count > MaxRows) throw new FormatException($"A file may have at most {MaxRows} rows");

        var template = await db.OrgSettings.AsNoTracking().Select(s => s.LocationTemplate).SingleOrDefaultAsync(ct) ?? "society";
        var levels = LocationTemplates.Levels[template];
        var required = kind switch
        {
            "houses" => new[] { levels[^1] },
            "residents" => ["house", "name", "phone", "relation"],
            "vehicles" => ["plate"],
            _ => ["name"],
        };
        var issues = new List<ImportIssue>();
        foreach (var column in required.Where(c => !table.Header.Contains(c)))
            issues.Add(new ImportIssue(1, column, "This column is missing from the file"));
        if (issues.Count > 0) return new ImportReport(kind, dryRun, table.Rows.Count, 0, 0, 0, issues);

        int created = 0, updated = 0, unchanged = 0;
        foreach (var row in table.Rows)
        {
            try
            {
                var outcome = kind switch
                {
                    "houses" => await HouseAsync(row, template, levels, ct),
                    "residents" => await ResidentAsync(row, ct),
                    "vehicles" => await VehicleAsync(row, ct),
                    _ => await HelpAsync(row, ct),
                };
                if (!dryRun && outcome != Outcome.Unchanged) await db.SaveChangesAsync(ct);
                switch (outcome) { case Outcome.Created: created++; break; case Outcome.Updated: updated++; break; default: unchanged++; break; }
            }
            catch (RowError e)
            {
                issues.Add(new ImportIssue(row.Line, e.Field, e.Message));
                if (!dryRun) db.ChangeTracker.Clear();
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
            {
                issues.Add(new ImportIssue(row.Line, "", pg.SqlState == PostgresErrorCodes.UniqueViolation ? "This conflicts with a record that already exists" : "The record could not be saved"));
                db.ChangeTracker.Clear();
            }
        }
        if (dryRun) db.ChangeTracker.Clear();
        return new ImportReport(kind, dryRun, table.Rows.Count, created, updated, unchanged, issues);
    }

    // Places ------------------------------------------------------------------------------------------------------

    private async Task<Location?> ChildAsync(Guid? parentId, string name, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return db.Locations.Local.FirstOrDefault(l => l.ParentId == parentId && l.Name.ToLowerInvariant() == lower)
            ?? await db.Locations.FirstOrDefaultAsync(l => l.ParentId == parentId && l.Name.ToLower() == lower, ct);
    }

    private async Task<Outcome> HouseAsync(Csv.Row row, string template, string[] levels, CancellationToken ct)
    {
        Location? parent = null;
        var made = false;
        foreach (var kind in levels)
        {
            var name = row.Get(kind);
            if (name.Length == 0)
            {
                if (kind == levels[^1]) throw new RowError(kind, "This is required");
                continue; // a society without sectors, say
            }
            var existing = await ChildAsync(parent?.Id, name, ct);
            if (existing is not null)
            {
                if (existing.Kind != kind) throw new RowError(kind, $"'{name}' already exists here as a {existing.Kind}, not a {kind}");
                parent = existing;
                continue;
            }
            if (LocationTemplates.Validate(template, parent?.Kind, kind) is { } problem) throw new RowError(kind, problem);
            var loc = new Location { OrganizationId = Org, ParentId = parent?.Id, Kind = kind, Name = name };
            loc.Path = (parent?.Path ?? "/") + loc.Id + "/";
            loc.Depth = (parent?.Depth ?? -1) + 1;
            db.Locations.Add(loc);
            await FlushAsync(ct); // a parent must be stored before its child
            parent = loc; made = true;
        }

        var plotText = row.Get("plot_size");
        decimal? plot = null;
        if (plotText.Length > 0)
        {
            if (!decimal.TryParse(plotText, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) || p <= 0) throw new RowError("plot_size", "Use a positive number");
            plot = p;
        }
        var status = row.Get("status").ToLowerInvariant();
        if (status.Length > 0 && !HouseStatuses.Contains(status)) throw new RowError("status", $"Use one of: {string.Join(", ", HouseStatuses)}");
        var category = row.Get("category");

        var house = db.Houses.Local.FirstOrDefault(h => h.LocationId == parent!.Id) ?? await db.Houses.FirstOrDefaultAsync(h => h.LocationId == parent!.Id, ct);
        if (house is null)
        {
            db.Houses.Add(new House { OrganizationId = Org, LocationId = parent!.Id, PlotSizeSqYd = plot, HouseCategory = category.Length > 0 ? category : null, Status = status.Length > 0 ? status : "occupied" });
            return Outcome.Created;
        }
        var changed = false;
        if (plot is not null && house.PlotSizeSqYd != plot) { house.PlotSizeSqYd = plot; changed = true; }
        if (category.Length > 0 && house.HouseCategory != category) { house.HouseCategory = category; changed = true; }
        if (status.Length > 0 && house.Status != status) { house.Status = status; changed = true; }
        return changed || made ? Outcome.Updated : Outcome.Unchanged;
    }

    /// <summary>Finds a house from its names from the top, e.g. "Sector A/Street 1/House 14".</summary>
    private async Task<House> HouseByPathAsync(string path, string field, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new RowError(field, "This is required");
        Location? at = null;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            at = await ChildAsync(at?.Id, part, ct) ?? throw new RowError(field, $"No place called '{part}' under '{at?.Name ?? "the top"}'");
        if (at is null) throw new RowError(field, "This is required");
        return db.Houses.Local.FirstOrDefault(h => h.LocationId == at.Id) ?? await db.Houses.FirstOrDefaultAsync(h => h.LocationId == at.Id, ct)
            ?? throw new RowError(field, $"'{path}' is not a house");
    }

    // Residents ---------------------------------------------------------------------------------------------------

    private async Task<Outcome> ResidentAsync(Csv.Row row, CancellationToken ct)
    {
        var house = await HouseByPathAsync(row.Get("house"), "house", ct);
        var name = row.Get("name");
        if (name.Length == 0) throw new RowError("name", "This is required");
        var phone = PhoneNumber.Normalize(row.Get("phone")) ?? throw new RowError("phone", "This does not look like a phone number");
        var relation = row.Get("relation").ToLowerInvariant();
        if (!Relations.Contains(relation)) throw new RowError("relation", $"Use one of: {string.Join(", ", Relations)}");
        var from = DateOnly.FromDateTime(DateTime.UtcNow);
        if (row.Get("from_date").Length > 0 && !DateOnly.TryParse(row.Get("from_date"), CultureInfo.InvariantCulture, out from)) throw new RowError("from_date", "Use a date like 2026-01-31");
        var email = row.Get("email").ToLowerInvariant();
        if (email.Length > 0 && !email.Contains('@')) throw new RowError("email", "This does not look like an email address");

        var outcome = Outcome.Unchanged;
        var user = db.Users.Local.FirstOrDefault(u => u.Phone == phone)
            ?? await db.Users.FromSql($"SELECT * FROM find_user_by_phone({phone})").FirstOrDefaultAsync(ct);
        if (user is null)
        {
            if (email.Length > 0 && await db.Users.FromSql($"SELECT * FROM find_user_for_login({email})").AnyAsync(ct)) throw new RowError("email", "Someone else already uses this email address");
            // Residents sign in with a code sent to their phone, so the password is random and nobody knows it.
            user = new User { DisplayName = name, Phone = phone, Email = email.Length > 0 ? email : $"{phone.TrimStart('+')}@phone.urbanfix.invalid", PasswordHash = passwords.Hash(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) + "aA1!") };
            db.Users.Add(user);
            await FlushAsync(ct);
            outcome = Outcome.Created;
        }
        if (!db.Memberships.Local.Any(m => m.UserId == user.Id && m.IsActive) && !await db.Memberships.AnyAsync(m => m.UserId == user.Id && m.IsActive, ct))
        {
            db.Memberships.Add(new Membership { OrganizationId = Org, UserId = user.Id, Role = Role.Resident });
            await FlushAsync(ct);
            if (outcome == Outcome.Unchanged) outcome = Outcome.Updated;
        }

        var current = db.ResidentLinks.Local.Where(l => l.HouseId == house.Id && l.ToDate == null).Concat(await db.ResidentLinks.Where(l => l.HouseId == house.Id && l.ToDate == null).ToListAsync(ct)).DistinctBy(l => l.Id).ToList();
        if (current.Any(l => l.UserId == user.Id && l.Relation == relation)) return outcome;
        if (relation != "family" && current.Any(l => l.Relation == relation)) throw new RowError("relation", $"This house already has a current {relation}");
        db.ResidentLinks.Add(new ResidentLink { OrganizationId = Org, HouseId = house.Id, UserId = user.Id, Relation = relation, FromDate = from });
        return outcome == Outcome.Unchanged ? Outcome.Updated : outcome;
    }

    // Vehicles ----------------------------------------------------------------------------------------------------

    private async Task<Outcome> VehicleAsync(Csv.Row row, CancellationToken ct)
    {
        var key = RegistryRules.PlateKey(row.Get("plate"));
        if (key.Length < 2) throw new RowError("plate", "This is required");
        var type = row.Get("type").ToLowerInvariant();
        if (type.Length > 0 && !RegistryRules.VehicleTypes.Contains(type)) throw new RowError("type", $"Use one of: {string.Join(", ", RegistryRules.VehicleTypes)}");
        var status = row.Get("status").ToLowerInvariant();
        if (status.Length > 0 && !RegistryRules.Statuses.Contains(status)) throw new RowError("status", "Use active or inactive");
        var house = row.Get("house").Length > 0 ? await HouseByPathAsync(row.Get("house"), "house", ct) : null;
        var sticker = row.Get("sticker_code");
        if (sticker.Length > 0 && (db.Vehicles.Local.Any(v => v.PlateKey != key && string.Equals(v.StickerCode, sticker, StringComparison.OrdinalIgnoreCase))
                                   || await db.Vehicles.AnyAsync(v => v.PlateKey != key && v.StickerCode != null && v.StickerCode.ToLower() == sticker.ToLower(), ct)))
            throw new RowError("sticker_code", "Another vehicle has this sticker");

        var vehicle = db.Vehicles.Local.FirstOrDefault(v => v.PlateKey == key) ?? await db.Vehicles.FirstOrDefaultAsync(v => v.PlateKey == key, ct);
        if (vehicle is null)
        {
            db.Vehicles.Add(new Vehicle { OrganizationId = Org, Plate = row.Get("plate").ToUpperInvariant(), PlateKey = key, Type = type.Length > 0 ? type : "car", HouseId = house?.Id, StickerCode = sticker.Length > 0 ? sticker : null, Status = status.Length > 0 ? status : "active" });
            return Outcome.Created;
        }
        var changed = false;
        if (type.Length > 0 && vehicle.Type != type) { vehicle.Type = type; changed = true; }
        if (status.Length > 0 && vehicle.Status != status) { vehicle.Status = status; changed = true; }
        if (house is not null && vehicle.HouseId != house.Id) { vehicle.HouseId = house.Id; changed = true; }
        if (sticker.Length > 0 && vehicle.StickerCode != sticker) { vehicle.StickerCode = sticker; changed = true; }
        return changed ? Outcome.Updated : Outcome.Unchanged;
    }

    // Daily help --------------------------------------------------------------------------------------------------

    private async Task<Outcome> HelpAsync(Csv.Row row, CancellationToken ct)
    {
        var name = row.Get("name");
        if (name.Length == 0) throw new RowError("name", "This is required");
        string? phone = null;
        if (row.Get("phone").Length > 0) phone = PhoneNumber.Normalize(row.Get("phone")) ?? throw new RowError("phone", "This does not look like a phone number");
        var role = row.Get("role").ToLowerInvariant();
        if (role.Length > 0 && !RegistryRules.HelpRoles.Contains(role)) throw new RowError("role", $"Use one of: {string.Join(", ", RegistryRules.HelpRoles)}");
        var days = RegistryRules.ParseDays(row.Get("days")) ?? throw new RowError("days", "Use days like mon,tue,wed or weekdays");
        if (!RegistryRules.TryParseTime(row.Get("from"), out var from)) throw new RowError("from", "Use a time like 08:00");
        if (!RegistryRules.TryParseTime(row.Get("to"), out var to)) throw new RowError("to", "Use a time like 17:00");
        if (from is { } f && to is { } t && t <= f) throw new RowError("to", "This must be after the start time");

        var houses = new List<House>();
        foreach (var path in row.Get("houses").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            houses.Add(await HouseByPathAsync(path, "houses", ct));

        var help = phone is not null
            ? db.DailyHelp.Local.FirstOrDefault(h => h.Phone == phone) ?? await db.DailyHelp.FirstOrDefaultAsync(h => h.Phone == phone, ct)
            : db.DailyHelp.Local.FirstOrDefault(h => h.Phone == null && h.Name.ToLower() == name.ToLower()) ?? await db.DailyHelp.FirstOrDefaultAsync(h => h.Phone == null && h.Name.ToLower() == name.ToLower(), ct);
        var outcome = Outcome.Unchanged;
        if (help is null)
        {
            help = new DailyHelp { OrganizationId = Org, Name = name, Phone = phone, IdNumberMasked = RegistryRules.MaskId(row.Get("id_number")), Role = role.Length > 0 ? role : "other", AllowedDays = days, AllowedFrom = from, AllowedTo = to };
            db.DailyHelp.Add(help);
            await FlushAsync(ct);
            outcome = Outcome.Created;
        }
        else
        {
            var masked = RegistryRules.MaskId(row.Get("id_number"));
            var changed = help.Name != name || (role.Length > 0 && help.Role != role) || (masked is not null && help.IdNumberMasked != masked)
                          || (row.Get("days").Length > 0 && !help.AllowedDays.SequenceEqual(days)) || (from is not null && help.AllowedFrom != from) || (to is not null && help.AllowedTo != to);
            if (changed)
            {
                help.Name = name;
                if (role.Length > 0) help.Role = role;
                if (masked is not null) help.IdNumberMasked = masked;
                if (row.Get("days").Length > 0) help.AllowedDays = days;
                if (from is not null) help.AllowedFrom = from;
                if (to is not null) help.AllowedTo = to;
                outcome = Outcome.Updated;
            }
        }
        foreach (var house in houses)
        {
            var linked = db.DailyHelpHouses.Local.Any(l => l.HelpId == help.Id && l.HouseId == house.Id)
                         || (outcome != Outcome.Created && await db.DailyHelpHouses.AnyAsync(l => l.HelpId == help.Id && l.HouseId == house.Id, ct));
            if (linked) continue;
            db.DailyHelpHouses.Add(new DailyHelpHouse { OrganizationId = Org, HelpId = help.Id, HouseId = house.Id });
            if (outcome == Outcome.Unchanged) outcome = Outcome.Updated;
        }
        return outcome;
    }
}
