using Microsoft.EntityFrameworkCore;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>
/// Reference numbers (CP-01, JB-01): UF-2026-00042. One counter row per organization, scope and year; the upsert
/// takes a row lock, so concurrent creations get distinct numbers, and a rolled-back creation gives its number back.
/// </summary>
public sealed class NumberGenerator(AppDbContext db, ITenantContext tenant)
{
    public const string ComplaintScope = "complaint";
    public const string JobScope = "job";

    public async Task<string> NextAsync(string scope, string prefix, CancellationToken ct)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        var year = DateTime.UtcNow.Year;
        // ToListAsync, not SingleAsync: INSERT ... RETURNING cannot be wrapped in a composed subquery.
        var values = await db.Database.SqlQuery<int>($"""
            INSERT INTO number_sequences (organization_id, scope, year, last_value) VALUES ({org}, {scope}, {year}, 1)
            ON CONFLICT (organization_id, scope, year) DO UPDATE SET last_value = number_sequences.last_value + 1
            RETURNING last_value AS "Value"
            """).ToListAsync(ct);
        var value = values.Single();
        return $"{prefix}-{year}-{value:D5}";
    }
}
