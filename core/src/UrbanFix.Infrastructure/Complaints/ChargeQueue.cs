using Microsoft.EntityFrameworkCore;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>Puts a finished chargeable job in front of accounts (JC-11), once, with the cost as it stood.</summary>
public static class ChargeQueue
{
    public static async Task EnsureAsync(AppDbContext db, Job job, CancellationToken ct)
    {
        if (!job.Chargeable || job.ChargeHouseId is not { } house || job.Status != JobStatus.Completed) return;
        if (await db.JobCharges.AnyAsync(c => c.JobId == job.Id && c.Status != ChargeStatus.Cancelled, ct)) return;
        var total = await db.JobCosts.AsNoTracking().Where(c => c.JobId == job.Id).SumAsync(c => (decimal?)c.Amount, ct) ?? 0;
        db.JobCharges.Add(new JobCharge { OrganizationId = job.OrganizationId, JobId = job.Id, HouseId = house, ProposedAmount = total });
    }
}

/// <summary>When a chargeable job is signed off, it joins the review queue.</summary>
public sealed class QueueChargeOnCompletion(AppDbContext db) : IEventHandler<JobStatusChanged>
{
    public async Task Handle(JobStatusChanged e, CancellationToken ct)
    {
        if (e.To != JobStatus.Completed) return;
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == e.JobId, ct);
        if (job is null) return;
        await ChargeQueue.EnsureAsync(db, job, ct);
        await db.SaveChangesAsync(ct);
    }
}
