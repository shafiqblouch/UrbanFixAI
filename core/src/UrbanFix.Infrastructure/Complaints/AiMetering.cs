using Microsoft.EntityFrameworkCore;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

/// <summary>Tokens a call used, summed by the AI service over every attempt the call needed (AI-13).</summary>
public sealed record AiUsage(int InputTokens, int OutputTokens);

public sealed class ModelPrice
{
    public decimal InputPerMillion { get; set; }
    public decimal OutputPerMillion { get; set; }
}

/// <summary>
/// The AI is not available to this organization right now and retrying will not help: it is switched off, or the
/// organization has used its monthly allowance. Callers fall back to rules-only mode (AI-14).
/// </summary>
public sealed class AiUnavailableException(string reason) : Exception($"AI unavailable: {reason}")
{
    public string Reason { get; } = reason;
    public const string Disabled = "disabled";
    public const string CapReached = "cap_reached";
}

/// <summary>Names the complaint an AI call is for, so its cost can be added up per complaint.</summary>
public sealed class AiUsageContext
{
    public Guid? ComplaintId { get; set; }
}

/// <summary>The raw calls to the AI service. <see cref="IAiClient"/> is this plus metering and caps.</summary>
public interface IAiBackend
{
    Task<AiPhoto> InterpretPhotoAsync(byte[] image, CancellationToken ct);
    Task<AiComparison> CompareAsync(byte[] before, byte[] after, CancellationToken ct);
    Task<AiEmbedding> EmbedAsync(string text, IReadOnlyList<string> knownNames, CancellationToken ct);
    Task<AiTranscript> TranscribeAsync(byte[] audio, string mimeType, string? languageHint, CancellationToken ct);
    Task<AiUnderstanding> UnderstandAsync(string text, IReadOnlyList<AiCategory> categories, IReadOnlyList<string> knownNames, IReadOnlyList<string> landmarks, CancellationToken ct);
}

public interface IAiClient : IAiBackend;

/// <summary>
/// Counts every AI call against the organization (AI-13). Before a call it checks the switch and the monthly token allowance;
/// after it, it appends a usage row with tokens and, where a price is configured, cost. Usage is written straight to the
/// database, so it never saves whatever else the calling job has pending.
/// </summary>
public sealed class MeteredAiClient(IAiBackend backend, AppDbContext db, ITenantContext tenant, AiOptions options, AiUsageContext context) : IAiClient
{
    public static decimal? Cost(AiOptions options, string model, AiUsage usage) =>
        options.Pricing.TryGetValue(model, out var p)
            ? Math.Round((usage.InputTokens * p.InputPerMillion + usage.OutputTokens * p.OutputPerMillion) / 1_000_000m, 6)
            : null;

    private async Task GuardAsync(CancellationToken ct)
    {
        if (!options.Enabled) throw new AiUnavailableException(AiUnavailableException.Disabled);
        var settings = await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct);
        var cap = OrgPolicy.From(settings ?? "{}").AiMonthlyTokenCap;
        if (cap == 0) throw new AiUnavailableException(AiUnavailableException.CapReached);
        if (await MonthlyTokensAsync(db, ct) >= cap) throw new AiUnavailableException(AiUnavailableException.CapReached);
    }

    public static async Task<long> MonthlyTokensAsync(AppDbContext db, CancellationToken ct)
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = await db.Database.SqlQuery<long>($"SELECT COALESCE(SUM(input_tokens + output_tokens), 0)::bigint AS \"Value\" FROM ai_usage WHERE occurred_at >= {start}").ToListAsync(ct);
        return rows.Single();
    }

    private async Task RecordAsync(string task, string model, AiUsage? usage, CancellationToken ct)
    {
        var u = usage ?? new AiUsage(0, 0);
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        var cost = Cost(options, model, u);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO ai_usage (id, organization_id, complaint_id, task, model, input_tokens, output_tokens, cost_usd)
            VALUES ({Guid.NewGuid()}, {org}, {context.ComplaintId}, {task}, {model}, {u.InputTokens}, {u.OutputTokens}, {cost})
            """, ct);
    }

    public async Task<AiUnderstanding> UnderstandAsync(string text, IReadOnlyList<AiCategory> categories, IReadOnlyList<string> knownNames, IReadOnlyList<string> landmarks, CancellationToken ct)
    {
        await GuardAsync(ct);
        var r = await backend.UnderstandAsync(text, categories, knownNames, landmarks, ct);
        await RecordAsync("understanding", r.Model, r.Usage, ct);
        return r;
    }

    public async Task<AiPhoto> InterpretPhotoAsync(byte[] image, CancellationToken ct)
    {
        await GuardAsync(ct);
        var r = await backend.InterpretPhotoAsync(image, ct);
        await RecordAsync("photo", r.Model, r.Usage, ct);
        return r;
    }

    public async Task<AiComparison> CompareAsync(byte[] before, byte[] after, CancellationToken ct)
    {
        await GuardAsync(ct);
        var r = await backend.CompareAsync(before, after, ct);
        await RecordAsync("before_after", r.Model, r.Usage, ct);
        return r;
    }

    public async Task<AiEmbedding> EmbedAsync(string text, IReadOnlyList<string> knownNames, CancellationToken ct)
    {
        await GuardAsync(ct);
        var r = await backend.EmbedAsync(text, knownNames, ct);
        await RecordAsync("embedding", r.Model, r.Usage, ct);
        return r;
    }

    public async Task<AiTranscript> TranscribeAsync(byte[] audio, string mimeType, string? languageHint, CancellationToken ct)
    {
        await GuardAsync(ct);
        var r = await backend.TranscribeAsync(audio, mimeType, languageHint, ct);
        await RecordAsync("transcription", r.Model, r.Usage, ct);
        return r;
    }
}
