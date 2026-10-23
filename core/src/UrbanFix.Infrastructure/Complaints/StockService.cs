using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

public sealed record MaterialLine(Guid ItemId, string Item, string Unit, decimal Issued, decimal Returned, decimal Used, decimal Unexplained, bool Flagged);

/// <summary>What a job drew from the store against what it recorded using (JC-07).</summary>
public static class MaterialsReport
{
    public static async Task<IReadOnlyList<MaterialLine>> ForJobAsync(AppDbContext db, Guid jobId, CancellationToken ct)
    {
        var moves = await db.StockMovements.AsNoTracking().Where(m => m.JobId == jobId).Select(m => new { m.ItemId, m.Kind, m.Quantity }).ToListAsync(ct);
        var used = (await db.JobCosts.AsNoTracking().Where(c => c.JobId == jobId && c.Kind == "material" && c.PriceItemId != null).Select(c => new { ItemId = c.PriceItemId!.Value, c.Quantity }).ToListAsync(ct))
            .GroupBy(c => c.ItemId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));
        var ids = moves.Select(m => m.ItemId).Concat(used.Keys).Distinct().ToList();
        var items = await db.PriceItems.AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        return ids.Select(id =>
        {
            var issued = -moves.Where(m => m.ItemId == id && m.Kind == StockKinds.Issue).Sum(m => m.Quantity);
            var returned = moves.Where(m => m.ItemId == id && m.Kind == StockKinds.Return).Sum(m => m.Quantity);
            var u = used.GetValueOrDefault(id);
            items.TryGetValue(id, out var item);
            return new MaterialLine(id, item?.Name ?? "?", item?.Unit ?? "", issued, returned, u, StockRules.Unexplained(issued, returned, u), StockRules.IsDiscrepancy(issued, returned, u));
        }).OrderBy(l => l.Item).ToList();
    }
}

/// <summary>
/// The only way stock changes (JC-06): checks the movement, takes a lock on the item so two issues cannot take the last unit
/// together, records it, and sends the low-stock alert when stock reaches the threshold (JC-08). Saves, in its own transaction unless
/// the caller already started one.
/// </summary>
public sealed class StockService(AppDbContext db, IEventBus bus)
{
    public async Task<decimal> OnHandAsync(Guid itemId, CancellationToken ct) =>
        await db.StockMovements.AsNoTracking().Where(m => m.ItemId == itemId).SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;

    public async Task<Dictionary<Guid, decimal>> OnHandAsync(CancellationToken ct) =>
        (await db.StockMovements.AsNoTracking().GroupBy(m => m.ItemId).Select(g => new { g.Key, Sum = g.Sum(m => m.Quantity) }).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Sum);

    /// <summary>Runs <paramref name="work"/> in one transaction, so several movements (a count, a fulfilled request) all happen or none do.</summary>
    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct, Func<T, bool>? commit = null)
    {
        if (db.Database.CurrentTransaction is not null) return await work();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await work();
            if (commit is not null && !commit(result)) { await tx.RollbackAsync(ct); db.ChangeTracker.Clear(); return result; }
            await tx.CommitAsync(ct);
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }

    /// <param name="signedQuantity">Positive to add (receive, return), negative to take away (issue); an adjustment is either.</param>
    /// <returns>The stored movement, or an error code: quantity_required, reason_required, insufficient_stock, item_not_found, job_not_found, more_than_issued, …</returns>
    public async Task<(StockMovement? Movement, string? Error, decimal? Available)> RecordAsync(
        Guid itemId, string kind, decimal signedQuantity, string? reason, Guid userId, Guid? jobId, Guid? requestId, Guid? countId, CancellationToken ct)
    {
        if (StockRules.Check(kind, signedQuantity, reason) is { } problem) return (null, problem, null);
        var item = await db.PriceItems.AsNoTracking().SingleOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return (null, "item_not_found", null);

        if (jobId is { } jid)
        {
            var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == jid, ct);
            if (job is null) return (null, "job_not_found", null);
            if (kind == StockKinds.Issue && !JobStatus.IsActive(job.Status)) return (null, "job_not_active", null);
        }
        else if (kind is StockKinds.Issue or StockKinds.Return) return (null, "job_required", null);

        return await InTransactionAsync<(StockMovement?, string?, decimal?)>(async () =>
        {
            // One at a time per item: the second of two simultaneous issues sees the first one's effect.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM price_items WHERE id = {itemId} FOR UPDATE", ct);
            var onHand = await OnHandAsync(itemId, ct);
            if (onHand + signedQuantity < 0) return (null, "insufficient_stock", onHand);

            if (kind == StockKinds.Return)
            {
                var drawn = -await db.StockMovements.Where(m => m.JobId == jobId && m.ItemId == itemId && m.Kind == StockKinds.Issue).SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;
                var back = await db.StockMovements.Where(m => m.JobId == jobId && m.ItemId == itemId && m.Kind == StockKinds.Return).SumAsync(m => (decimal?)m.Quantity, ct) ?? 0;
                if (signedQuantity > drawn - back) return (null, "more_than_issued", drawn - back);
            }

            var movement = new StockMovement
            {
                OrganizationId = item.OrganizationId, ItemId = itemId, Kind = kind, Quantity = signedQuantity, JobId = jobId, RequestId = requestId, CountId = countId,
                Reason = string.IsNullOrWhiteSpace(reason) ? DefaultReason(kind) : reason.Trim(), UserId = userId,
            };
            db.StockMovements.Add(movement);

            var setting = await db.StockSettings.SingleOrDefaultAsync(s => s.ItemId == itemId, ct);
            if (setting is not null)
            {
                var (alert, alerted) = StockRules.NextAlert(onHand + signedQuantity, setting.LowThreshold, setting.LowAlertedAt is not null);
                if (alerted && setting.LowAlertedAt is null) setting.LowAlertedAt = DateTimeOffset.UtcNow;
                else if (!alerted) setting.LowAlertedAt = null;
                if (alert) bus.Publish(new StockLow(item.OrganizationId, itemId, item.Name, onHand + signedQuantity, setting.LowThreshold));
            }
            await db.SaveChangesAsync(ct);
            return (movement, null, onHand + signedQuantity);
        }, ct);
    }

    private static string DefaultReason(string kind) => kind switch
    {
        StockKinds.Receive => "Received into the store",
        StockKinds.Issue => "Issued to a job",
        StockKinds.Return => "Returned unused",
        _ => "Adjustment",
    };
}
