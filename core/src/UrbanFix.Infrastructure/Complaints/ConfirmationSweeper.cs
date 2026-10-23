using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>Organizations are tenant-scoped, so background jobs read the list itself as the owner role.</summary>
public static class OrganizationList
{
    public static async Task<List<Guid>> ReadAsync(IConfiguration config, CancellationToken ct)
    {
        var orgs = new List<Guid>();
        await using var conn = new NpgsqlConnection(config.GetConnectionString("Owner") ?? config.GetConnectionString("App"));
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT id FROM organizations", conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) orgs.Add(reader.GetGuid(0));
        return orgs;
    }
}

/// <summary>
/// Optional auto-confirm (CP-10). For organizations that switch it on: remind the resident, then close the complaint
/// N days after sign-off, marked as auto-confirmed. By default nothing is ever closed without the resident.
/// </summary>
public sealed class ConfirmationSweeper(IServiceScopeFactory scopes, IConfiguration config, IJobFailureNotifier failures)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var changed = 0;
        var errors = new List<Exception>();
        foreach (var org in await OrganizationList.ReadAsync(config, ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(org, null);
                changed += await SweepAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<IEventBus>(), DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add(ex);
                failures.JobFailed("confirmation-sweep", $"ConfirmationSweeper (organization {org})", ex.Message);
            }
        }
        if (errors.Count > 0) throw new AggregateException("Confirmation sweep failed", errors);
        return changed;
    }

    public async Task<int> SweepAsync(AppDbContext db, IEventBus bus, DateTimeOffset now, CancellationToken ct)
    {
        var settings = await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct);
        var policy = OrgPolicy.From(settings ?? "{}");
        if (!policy.AutoConfirm) return 0;

        var waiting = await db.Complaints.Where(c => c.Status == ComplaintStatus.Resolved && c.ResolvedAt != null && c.ReportedByUserId != null).ToListAsync(ct);
        var changed = 0;
        foreach (var c in waiting)
        {
            var resolved = c.ResolvedAt!.Value;
            if (now >= resolved.AddDays(policy.AutoConfirmDays))
            {
                var reason = $"Auto-confirmed after {policy.AutoConfirmDays} days without a reply";
                c.Status = ComplaintStatus.Closed; c.ClosedAt = now; c.UpdatedAt = now; c.AutoConfirmed = true; c.StatusReason = reason;
                db.ComplaintHistory.Add(new ComplaintStatusHistory
                {
                    OrganizationId = c.OrganizationId, ComplaintId = c.Id, FromStatus = ComplaintStatus.Resolved, ToStatus = ComplaintStatus.Closed, Reason = reason,
                });
                bus.Publish(new ComplaintStatusChanged(c.OrganizationId, c.Id, c.Reference, ComplaintStatus.Resolved, ComplaintStatus.Closed, null, reason));
                changed++;
            }
            else if (c.ConfirmationRemindedAt is null && now >= resolved.AddDays(policy.ReminderDays))
            {
                c.ConfirmationRemindedAt = now;
                bus.Publish(new ConfirmationReminder(c.OrganizationId, c.Id, c.Reference));
                changed++;
            }
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}
