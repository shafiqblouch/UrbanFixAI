using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>People in the organization, so screens can show names instead of ids and offer technicians to assign.</summary>
public static class DirectoryEndpoints
{
    public sealed record PersonDto(Guid UserId, string DisplayName, Role Role, IReadOnlyList<Guid> TeamIds);

    public static void MapDirectoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/directory", async (Role? role, ITenantContext tenant, AppDbContext db, CancellationToken ct) =>
        {
            var org = tenant.OrganizationId!.Value;
            var people = await (from m in db.Memberships.AsNoTracking()
                                join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                                where m.OrganizationId == org && m.IsActive && (role == null || m.Role == role)
                                orderby u.DisplayName
                                select new { u.Id, u.DisplayName, m.Role }).ToListAsync(ct);
            var teams = (await db.TeamMembers.AsNoTracking().Select(t => new { t.UserId, t.TeamId }).ToListAsync(ct)).ToLookup(t => t.UserId, t => t.TeamId);
            return Results.Ok(people.Select(p => new PersonDto(p.Id, p.DisplayName, p.Role, teams[p.Id].ToList())));
        }).WithTags("Directory").Produces<List<PersonDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));
    }
}
