using System.Globalization;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

public sealed record SimilarComplaint(Guid ComplaintId, string Reference, double Similarity);

/// <summary>Stores and searches complaint embeddings in pgvector (AI-11). Raw SQL on purpose: no extra mapping package for one column.</summary>
public static class EmbeddingStore
{
    public static string Literal(IReadOnlyList<float> vector) => "[" + string.Join(',', vector.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";

    /// <summary>One row per complaint, source, photo and model; saving again replaces the vector.</summary>
    public static Task SaveAsync(AppDbContext db, Guid organizationId, Guid complaintId, string source, Guid? mediaId, string model, IReadOnlyList<float> vector, CancellationToken ct)
    {
        var literal = Literal(vector);
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO complaint_embeddings (id, organization_id, complaint_id, source, media_id, model, embedding)
            VALUES ({Guid.NewGuid()}, {organizationId}, {complaintId}, {source}, {mediaId}, {model}, {literal}::vector)
            ON CONFLICT (organization_id, complaint_id, source, COALESCE(media_id, '00000000-0000-0000-0000-000000000000'::uuid), model)
            DO UPDATE SET embedding = EXCLUDED.embedding, created_at = now()
            """, ct);
    }

    private sealed record PairRow(Guid ComplaintId, double Similarity);

    /// <summary>How alike a complaint's wording is to each of some others (0 to 1); complaints with no vector are left out (DP-03).</summary>
    public static async Task<Dictionary<Guid, double>> SimilarityToAsync(AppDbContext db, Guid complaintId, IReadOnlyCollection<Guid> others, CancellationToken ct)
    {
        if (others.Count == 0) return [];
        var ids = others.ToArray();
        var rows = await db.Database.SqlQuery<PairRow>($"""
            SELECT o.complaint_id AS complaint_id, MAX(1 - (o.embedding <=> s.embedding))::float8 AS similarity
            FROM complaint_embeddings s
            JOIN complaint_embeddings o ON o.organization_id = s.organization_id AND o.model = s.model
            WHERE s.complaint_id = {complaintId} AND s.source = 'text' AND o.source = 'text' AND o.complaint_id = ANY({ids})
            GROUP BY o.complaint_id
            """).ToListAsync(ct);
        return rows.ToDictionary(r => r.ComplaintId, r => Math.Clamp(r.Similarity, 0, 1));
    }

    private sealed record Row(Guid ComplaintId, string Reference, double Similarity);

    /// <summary>
    /// Other complaints in this organization whose wording or photos are closest to this one's, most similar first.
    /// Only rows from the same model are compared. Cancelled complaints (rejected, merged) are left out.
    /// </summary>
    public static async Task<IReadOnlyList<SimilarComplaint>> FindSimilarAsync(
        AppDbContext db, Guid complaintId, int days, int limit, double minSimilarity, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT o.complaint_id AS complaint_id, c.reference AS reference, MAX(1 - (o.embedding <=> s.embedding))::float8 AS similarity
            FROM complaint_embeddings s
            JOIN complaint_embeddings o ON o.organization_id = s.organization_id AND o.model = s.model AND o.complaint_id <> s.complaint_id
            JOIN complaints c ON c.organization_id = o.organization_id AND c.id = o.complaint_id
            WHERE s.complaint_id = {complaintId} AND c.created_at >= {since} AND c.status NOT IN ('Rejected', 'Merged')
            GROUP BY o.complaint_id, c.reference
            HAVING MAX(1 - (o.embedding <=> s.embedding)) >= {minSimilarity}
            ORDER BY similarity DESC
            LIMIT {limit}
            """).ToListAsync(ct);
        return rows.Select(r => new SimilarComplaint(r.ComplaintId, r.Reference, Math.Round(r.Similarity, 4))).ToList();
    }
}

/// <summary>Embeds a complaint's wording, or one of its photos' descriptions, so similar complaints can be found.</summary>
public sealed class EmbeddingJob(AppDbContext db, IAiClient ai, AiUsageContext usage, DuplicateFinder duplicates, Jobs.JobOptions jobOptions, ILogger<EmbeddingJob> log)
{
    public Task RunAsync(Guid organizationId, Guid complaintId, Guid? mediaId, PerformContext? context, CancellationToken ct)
        => RunCoreAsync(organizationId, complaintId, mediaId, context?.GetJobParameter<int>("RetryCount") ?? 0, ct);

    public async Task RunCoreAsync(Guid organizationId, Guid complaintId, Guid? mediaId, int retries, CancellationToken ct)
    {
        await db.ChangeTenantAsync(organizationId, null, ct);
        var complaint = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(c => c.Id == complaintId, ct);
        if (complaint is null) return;
        usage.ComplaintId = complaintId;

        string? text;
        string source;
        if (mediaId is { } id)
        {
            source = "photo";
            text = await db.ComplaintMedia.AsNoTracking().Where(m => m.Id == id && m.ComplaintId == complaintId).Select(m => m.AiDescription).SingleOrDefaultAsync(ct);
        }
        else
        {
            source = "text";
            text = string.Join(". ", new[] { complaint.Description, complaint.AiSummary }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
        }
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var names = complaint.ReportedByUserId is { } reporter
                ? await db.Users.AsNoTracking().Where(u => u.Id == reporter).Select(u => u.DisplayName).ToListAsync(ct)
                : [];
            var result = await ai.EmbedAsync(text, names, ct);
            await EmbeddingStore.SaveAsync(db, organizationId, complaintId, source, mediaId, result.Model, result.Vector, ct);
            // The wording is now comparable: look for complaints about the same problem (DP-03).
            if (source == "text") await duplicates.FindAsync(complaintId, ct);
        }
        catch (AiUnavailableException)
        {
            // Over the allowance or switched off: similar-complaint search just has no vector for this one.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Embedding failed for complaint {Id} (retry {Retry})", complaintId, retries);
            // Similar-complaint search is a convenience: after the last retry the complaint simply has no vector.
            if (retries >= jobOptions.Attempts) return;
            throw;
        }
    }
}

/// <summary>Sends a new complaint's wording, and later its photo descriptions, for embedding (only when an embedding model is configured).</summary>
public sealed class EnqueueEmbeddingOnCreate(IBackgroundJobClient jobs, AiOptions options) : IEventHandler<ComplaintCreated>
{
    public Task Handle(ComplaintCreated e, CancellationToken ct)
    {
        if (options.Enabled && options.EmbeddingsEnabled) jobs.Enqueue<EmbeddingJob>(j => j.RunAsync(e.OrganizationId, e.ComplaintId, null, null!, CancellationToken.None));
        return Task.CompletedTask;
    }
}
