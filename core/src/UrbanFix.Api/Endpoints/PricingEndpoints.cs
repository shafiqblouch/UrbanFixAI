using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using UrbanFix.Api.Security;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Material price list (JC-01). Rates are append-only: a change adds a row, so old estimates keep their price.</summary>
public static class PricingEndpoints
{
    public sealed record CreatePriceItem(string Name, string? NameUr, string Unit, decimal Rate, DateTimeOffset? EffectiveFrom);
    public sealed record UpdatePriceItem(string? Name, string? NameUr, string? Unit, bool? IsActive);
    public sealed record AddRate(decimal Rate, DateTimeOffset? EffectiveFrom);
    public sealed record RateDto(Guid Id, decimal Rate, DateTimeOffset EffectiveFrom, DateTimeOffset CreatedAt);
    public sealed record PriceItemDto(Guid Id, string Name, string? NameUr, string Unit, bool IsActive, RateDto? CurrentRate);

    public static void MapPricingEndpoints(this IEndpointRouteBuilder app)
    {
        var read = AuthPolicies.For(Permissions.PricingRead);
        var manage = AuthPolicies.For(Permissions.PricingManage);

        app.MapGet("/price-items", async (DateTimeOffset? at, bool? includeInactive, AppDbContext db, CancellationToken ct) =>
        {
            var when = at ?? DateTimeOffset.UtcNow;
            var items = await db.PriceItems.AsNoTracking().Where(i => includeInactive == true || i.IsActive).OrderBy(i => i.Name).ToListAsync(ct);
            var rates = await db.PriceRates.AsNoTracking().Where(r => r.EffectiveFrom <= when).ToListAsync(ct);
            return Results.Ok(items.Select(i => ToDto(i, Current(rates.Where(r => r.ItemId == i.Id), when))));
        }).WithTags("Pricing").Produces<List<PriceItemDto>>(200).RequireAuthorization(read);

        app.MapGet("/price-items/{id:guid}/rates", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.PriceRates.AsNoTracking().Where(r => r.ItemId == id).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.CreatedAt).ToListAsync(ct)).Select(ToDto)))
            .WithTags("Pricing").Produces<List<RateDto>>(200).RequireAuthorization(read);

        app.MapPost("/price-items", async (CreatePriceItem req, ClaimsPrincipal user, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Unit)) return Results.BadRequest(new { error = "name_and_unit_required" });
            if (req.Rate < 0) return Results.BadRequest(new { error = "invalid_rate" });
            var item = new PriceItem { OrganizationId = tenant.OrganizationId!.Value, Name = req.Name.Trim(), NameUr = req.NameUr, Unit = req.Unit.Trim() };
            var rate = NewRate(item.Id, req.Rate, req.EffectiveFrom, user, tenant);
            db.PriceItems.Add(item);
            db.PriceRates.Add(rate);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { return Results.Conflict(new { error = "duplicate_name" }); }
            return Results.Created($"/price-items/{item.Id}", ToDto(item, rate));
        }).WithTags("Pricing").Produces<PriceItemDto>(201).RequireAuthorization(manage);

        app.MapPut("/price-items/{id:guid}", async (Guid id, UpdatePriceItem req, AppDbContext db, CancellationToken ct) =>
        {
            var item = await db.PriceItems.SingleOrDefaultAsync(i => i.Id == id, ct);
            if (item is null) return Results.NotFound();
            if (req.Name is { } n) item.Name = n.Trim();
            if (req.NameUr is not null) item.NameUr = req.NameUr;
            if (req.Unit is { } u) item.Unit = u.Trim();
            if (req.IsActive is { } a) item.IsActive = a;
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { return Results.Conflict(new { error = "duplicate_name" }); }
            return Results.Ok(ToDto(item, null));
        }).WithTags("Pricing").Produces<PriceItemDto>(200).RequireAuthorization(manage);

        app.MapPost("/price-items/{id:guid}/rates", async (Guid id, AddRate req, ClaimsPrincipal user, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (req.Rate < 0) return Results.BadRequest(new { error = "invalid_rate" });
            if (!await db.PriceItems.AnyAsync(i => i.Id == id, ct)) return Results.NotFound();
            var rate = NewRate(id, req.Rate, req.EffectiveFrom, user, tenant);
            db.PriceRates.Add(rate);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/price-items/{id}/rates", ToDto(rate));
        }).WithTags("Pricing").Produces<RateDto>(201).RequireAuthorization(manage);
    }

    private static PriceRate NewRate(Guid itemId, decimal rate, DateTimeOffset? from, ClaimsPrincipal user, ITenantContext tenant) => new()
    {
        OrganizationId = tenant.OrganizationId!.Value, ItemId = itemId, Rate = decimal.Round(rate, 2),
        EffectiveFrom = from ?? DateTimeOffset.UtcNow, CreatedBy = Guid.Parse(user.FindFirstValue("sub")!),
    };

    /// <summary>The rate in force at a moment: the latest one that had already taken effect (ties go to the newest entry).</summary>
    private static PriceRate? Current(IEnumerable<PriceRate> rates, DateTimeOffset at)
        => rates.Where(r => r.EffectiveFrom <= at).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.CreatedAt).FirstOrDefault();

    private static RateDto ToDto(PriceRate r) => new(r.Id, r.Rate, r.EffectiveFrom, r.CreatedAt);
    private static PriceItemDto ToDto(PriceItem i, PriceRate? current) => new(i.Id, i.Name, i.NameUr, i.Unit, i.IsActive, current is null ? null : ToDto(current));
}
