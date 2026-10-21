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

    public sealed record CreateOrganizationRequest(string Name, string Slug, string ManagerEmail, string ManagerName, string ManagerPassword);
    public sealed record OrganizationDto(Guid Id, string Name, string Slug);
    public sealed record AddMemberRequest(string Email, string DisplayName, string? Password, Role Role, Guid? DepartmentId, Guid? GateId);
    public sealed record MemberDto(Guid MembershipId, Guid UserId, string Email, string DisplayName, Role Role, Guid? DepartmentId, Guid? GateId, bool IsActive);
    public sealed record AuditDto(Guid Id, Guid? ActorUserId, string Action, string EntityType, string EntityId, JsonElement? Before, JsonElement? After, DateTimeOffset OccurredAt, string? Ip, string? Device);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/organizations", CreateOrganization)
            .WithTags("Admin").RequireAuthorization(AuthPolicies.For(Permissions.OrganizationManage));

        app.MapGet("/admin/organizations", async (AppDbContext db, CancellationToken ct) =>
                Results.Ok(await db.Organizations.AsNoTracking().OrderBy(o => o.Name).Select(o => new OrganizationDto(o.Id, o.Name, o.Slug)).ToListAsync(ct)))
            .WithTags("Admin").RequireAuthorization(AuthPolicies.For(Permissions.OrganizationManage));

        app.MapGet("/members", async (AppDbContext db, CancellationToken ct) =>
            {
                var rows = await db.Memberships.AsNoTracking()
                    .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
                    .OrderBy(x => x.u.Email)
                    .Select(x => new MemberDto(x.m.Id, x.u.Id, x.u.Email, x.u.DisplayName, x.m.Role, x.m.DepartmentId, x.m.GateId, x.m.IsActive))
                    .ToListAsync(ct);
                return Results.Ok(rows);
            })
            .WithTags("Members").RequireAuthorization(AuthPolicies.For(Permissions.MembershipManage));

        app.MapPost("/members", AddMember).WithTags("Members").RequireAuthorization(AuthPolicies.For(Permissions.MembershipManage));

        app.MapDelete("/members/{membershipId:guid}", async (Guid membershipId, AppDbContext db, CancellationToken ct) =>
            {
                var m = await db.Memberships.SingleOrDefaultAsync(x => x.Id == membershipId, ct);
                if (m is null) return Results.NotFound();
                m.IsActive = false;
                await db.SaveChangesAsync(ct);
                return Results.NoContent();
            })
            .WithTags("Members").RequireAuthorization(AuthPolicies.For(Permissions.MembershipManage));

        app.MapGet("/audit", QueryAudit).WithTags("Audit").RequireAuthorization(AuthPolicies.For(Permissions.AuditRead));
    }

    private static async Task<IResult> CreateOrganization(
        CreateOrganizationRequest req, ClaimsPrincipal caller, AppDbContext db, PasswordService passwords, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Slug)) return Results.BadRequest(new { error = "name_and_slug_required" });
        if (req.Slug == PlatformSlug) return Results.BadRequest(new { error = "reserved_slug" });
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

        return Results.Created($"/admin/organizations/{org.Id}", new OrganizationDto(org.Id, org.Name, org.Slug));
    }

    private static async Task<IResult> AddMember(AddMemberRequest req, ClaimsPrincipal caller, AppDbContext db, PasswordService passwords, ITenantContext tenant, CancellationToken ct)
    {
        var orgId = tenant.OrganizationId!.Value;
        var orgSlug = await db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Slug).SingleAsync(ct);
        if ((req.Role == Role.PlatformAdmin) != (orgSlug == PlatformSlug))
            return Results.BadRequest(new { error = "role_not_allowed_in_organization" });

        var user = await FindOrCreateUser(db, passwords, req.Email, req.DisplayName, req.Password, ct);
        if (user.Error is not null) return user.Error;

        var membership = new Membership
        {
            OrganizationId = orgId, UserId = user.User!.Id, Role = req.Role, DepartmentId = req.DepartmentId, GateId = req.GateId,
        };
        db.Memberships.Add(membership);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/members/{membership.Id}", new MemberDto(membership.Id, user.User.Id, user.User.Email, user.User.DisplayName, req.Role, req.DepartmentId, req.GateId, true));
    }

    private static async Task<(User? User, IResult? Error)> FindOrCreateUser(
        AppDbContext db, PasswordService passwords, string email, string displayName, string? password, CancellationToken ct)
    {
        email = email.Trim().ToLowerInvariant();
        var existing = await db.Users.FromSql($"SELECT * FROM find_user_for_login({email})").AsNoTracking().SingleOrDefaultAsync(ct);
        if (existing is not null) return (existing, null);

        var problem = password is null ? "A temporary password is required for a new user." : PasswordService.Validate(password);
        if (problem is not null) return (null, Results.BadRequest(new { error = "weak_password", detail = problem }));

        var user = new User { Email = email, DisplayName = displayName.Trim(), PasswordHash = passwords.Hash(password!) };
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
