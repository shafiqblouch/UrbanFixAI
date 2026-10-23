using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

public static class AdminEndpoints
{
    public const string PlatformSlug = "platform";

    /// <summary>The optional fields set the society's first settings (PA-01), so it opens already in the right language, time zone and structure.</summary>
    public sealed record CreateOrganizationRequest(
        string Name, string Slug, string ManagerEmail, string ManagerName, string ManagerPassword,
        string? DisplayName = null, string? LocationTemplate = null, string? Timezone = null, string? Locale = null);
    public sealed record OrganizationDto(Guid Id, string Name, string Slug);
    public sealed record AddMemberRequest(string Email, string DisplayName, string? Password, Role Role, Guid? DepartmentId, Guid? GateId, string? Phone = null);
    public sealed record MemberDto(Guid MembershipId, Guid UserId, string Email, string DisplayName, Role Role, Guid? DepartmentId, Guid? GateId, bool IsActive, string? Phone = null);
    public sealed record AuditDto(Guid Id, Guid? ActorUserId, string Action, string EntityType, string EntityId, JsonElement? Before, JsonElement? After, DateTimeOffset OccurredAt, string? Ip, string? Device);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/organizations", CreateOrganization)
            .WithTags("Admin").Produces<OrganizationDto>(201).RequireAuthorization(AuthPolicies.For(Permissions.OrganizationManage));

        app.MapGet("/admin/organizations", async (AppDbContext db, CancellationToken ct) =>
                Results.Ok(await db.Organizations.AsNoTracking().OrderBy(o => o.Name).Select(o => new OrganizationDto(o.Id, o.Name, o.Slug)).ToListAsync(ct)))
            .WithTags("Admin").Produces<List<OrganizationDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.OrganizationManage));

        app.MapGet("/members", async (AppDbContext db, CancellationToken ct) =>
            {
                var rows = await db.Memberships.AsNoTracking()
                    .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
                    .OrderBy(x => x.u.Email)
                    .Select(x => new MemberDto(x.m.Id, x.u.Id, x.u.Email, x.u.DisplayName, x.m.Role, x.m.DepartmentId, x.m.GateId, x.m.IsActive, x.u.Phone))
                    .ToListAsync(ct);
                return Results.Ok(rows);
            })
            .WithTags("Members").Produces<List<MemberDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.MembershipManage));

        app.MapPost("/members", AddMember).WithTags("Members").Produces<MemberDto>(201).RequireAuthorization(AuthPolicies.For(Permissions.MembershipManage));

        app.MapDelete("/members/{membershipId:guid}", async (Guid membershipId, AppDbContext db, CancellationToken ct) =>
            {
                var m = await db.Memberships.SingleOrDefaultAsync(x => x.Id == membershipId, ct);
                if (m is null) return Results.NotFound();
                m.IsActive = false;
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .WithTags("Members").RequireAuthorization(AuthPolicies.For(Permissions.MembershipManage));

        app.MapGet("/audit", QueryAudit).WithTags("Audit").Produces<List<AuditDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.AuditRead));
    }

    private static async Task<IResult> CreateOrganization(
        CreateOrganizationRequest req, ClaimsPrincipal caller, AppDbContext db, PasswordService passwords, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Slug)) return Results.BadRequest(new { error = "name_and_slug_required" });
        if (req.Slug == PlatformSlug) return Results.BadRequest(new { error = "reserved_slug" });
        if (req.LocationTemplate is not null && !UrbanFix.OrgSetup.LocationTemplates.Levels.ContainsKey(req.LocationTemplate)) return Results.BadRequest(new { error = "unknown_location_template" });
        if (req.Locale is not (null or "en" or "ur")) return Results.BadRequest(new { error = "unsupported_locale" });
        if (req.Timezone is not null)
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(req.Timezone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return Results.BadRequest(new { error = "unknown_timezone" }); }
        }
        if (await db.Organizations.AnyAsync(o => o.Slug == req.Slug, ct)) return Results.Conflict(new { error = "slug_taken" });

        var actor = Guid.Parse(caller.FindFirstValue("sub")!);
        var platformOrg = Guid.Parse(caller.FindFirstValue("org")!);

        var org = new Organization { Name = req.Name.Trim(), Slug = req.Slug.Trim().ToLowerInvariant() };
        db.Organizations.Add(org);
        db.AuditLog.Add(new AuditEntry
        {
            OrganizationId = platformOrg, ActorUserId = actor, Action = "created", EntityType = nameof(Organization),
            EntityId = org.Id.ToString(), After = JsonSerializer.Serialize(new { org.Name, org.Slug }),
        });
        await db.SaveChangesAsync(ct);

        var manager = await FindOrCreateUser(db, passwords, req.ManagerEmail, req.ManagerName, req.ManagerPassword, ct);
        if (manager.Error is not null) return manager.Error;

        // The first manager's membership belongs to the new organization, so switch the DB context to it.
        await db.ChangeTenantAsync(org.Id, actor, ct);
        db.Memberships.Add(new Membership { OrganizationId = org.Id, UserId = manager.User!.Id, Role = Role.Manager });
        await db.SaveChangesAsync(ct);

        // Day-one configuration: default settings and the seeded category templates.
        var settings = await SetupEndpoints.GetOrCreateSettings(db, org.Id, ct);
        if (req.DisplayName is { Length: > 0 }) settings.DisplayName = req.DisplayName.Trim();
        if (req.LocationTemplate is not null) settings.LocationTemplate = req.LocationTemplate;
        if (req.Timezone is not null) settings.Timezone = req.Timezone;
        if (req.Locale is not null) settings.Locale = req.Locale;
        await db.SaveChangesAsync(ct);
        await SetupEndpoints.SeedCategoriesAsync(db, org.Id, ct);

        return Results.Created($"/admin/organizations/{org.Id}", new OrganizationDto(org.Id, org.Name, org.Slug));
    }

    private static async Task<IResult> AddMember(AddMemberRequest req, ClaimsPrincipal caller, AppDbContext db, PasswordService passwords, ITenantContext tenant, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId!.Value;
        var orgSlug = await db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Slug).SingleAsync(ct);
        if (req.Role == Role.Support) return Results.BadRequest(new { error = "role_not_assignable" });
        if ((req.Role == Role.PlatformAdmin) != (orgSlug == PlatformSlug))
            return Results.BadRequest(new { error = "role_not_allowed_in_organization" });

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            phone = PhoneNumber.Normalize(req.Phone);
            if (phone is null) return Results.BadRequest(new { error = "invalid_phone" });
        }

        var user = await FindOrCreateUser(db, passwords, req.Email, req.DisplayName, req.Password, ct, phone);
        if (user.Error is not null) return user.Error;

        var membership = new Membership
        {
            OrganizationId = orgId, UserId = user.User!.Id, Role = req.Role, DepartmentId = req.DepartmentId, GateId = req.GateId,
        };
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/members/{membership.Id}", new MemberDto(membership.Id, user.User.Id, user.User.Email, user.User.DisplayName, req.Role, req.DepartmentId, req.GateId, true, user.User.Phone));
    }

    private static async Task<(User? User, IResult? Error)> FindOrCreateUser(
        AppDbContext db, PasswordService passwords, string email, string displayName, string? password, CancellationToken ct, string? phone = null)
    {
        email = email.Trim().ToLowerInvariant();
        var existing = await db.Users.FromSql($"SELECT * FROM find_user_for_login({email})").AsNoTracking().SingleOrDefaultAsync(ct);
        if (phone is not null)
        {
            var owner = await db.Users.FromSql($"SELECT * FROM find_user_by_phone({phone})").AsNoTracking().SingleOrDefaultAsync(ct);
            if (owner is not null && owner.Id != existing?.Id) return (null, Results.Conflict(new { error = "phone_taken" }));
        }
        if (existing is not null)
        {
            if (phone is not null && existing.Phone is null)
            {
                var tracked = await db.Users.SingleAsync(u => u.Id == existing.Id, ct);
                tracked.Phone = phone;
                await db.SaveChangesAsync(ct);
                return (tracked, null);
            }
            return (existing, null);
        }

        var problem = password is null ? "A temporary password is required for a new user." : PasswordService.Validate(password);
        if (problem is not null) return (null, Results.BadRequest(new { error = "weak_password", detail = problem }));

        var user = new User { Email = email, DisplayName = displayName.Trim(), PasswordHash = passwords.Hash(password!), Phone = phone };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return (user, null);
    }

    private static async Task<IResult> QueryAudit(
        AppDbContext db, string? entityType, string? entityId, Guid? actorUserId, string? action,
        DateTimeOffset? from, DateTimeOffset? to, int? limit, int? offset, string? order, CancellationToken ct)
    {
        var q = db.AuditLog.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(entityType)) q = q.Where(a => a.EntityType == entityType);
        if (!string.IsNullOrEmpty(entityId)) q = q.Where(a => a.EntityId == entityId);
        if (actorUserId is { } actor) q = q.Where(a => a.ActorUserId == actor);
        if (!string.IsNullOrEmpty(action)) q = q.Where(a => a.Action == action);
        if (from is { } f) q = q.Where(a => a.OccurredAt >= f);
        if (to is { } t) q = q.Where(a => a.OccurredAt <= t);

        q = order == "asc" ? q.OrderBy(a => a.OccurredAt).ThenBy(a => a.Id) : q.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.Id);
        var rows = await q.Skip(Math.Max(offset ?? 0, 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);

        return Results.Ok(rows.Select(a => new AuditDto(
            a.Id, a.ActorUserId, a.Action, a.EntityType, a.EntityId,
            a.Before is null ? null : JsonDocument.Parse(a.Before).RootElement.Clone(),
            a.After is null ? null : JsonDocument.Parse(a.After).RootElement.Clone(),
            a.OccurredAt, a.Ip, a.Device)));
    }
}
