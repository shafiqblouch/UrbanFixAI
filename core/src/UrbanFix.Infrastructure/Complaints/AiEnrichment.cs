using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

public sealed class AiOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string ServiceToken { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed record AiCategory(string Key, string Label, string? Description);

public sealed record AiUnderstanding(string Category, string Summary, string? LocationText, string Language, float Confidence, bool NeedsMoreInfo, string Model, string PromptVersion);

public interface IAiClient
{
    Task<AiUnderstanding> UnderstandAsync(string text, IReadOnlyList<AiCategory> categories, IReadOnlyList<string> knownNames, IReadOnlyList<string> landmarks, CancellationToken ct);
}

/// <summary>Typed client for the AI service's <c>/v1/complaints/understand</c> contract (AI-02).</summary>
public sealed class HttpAiClient(HttpClient http) : IAiClient
{
    private sealed record Request(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("categories")] IReadOnlyList<CategoryBody> Categories,
        [property: JsonPropertyName("known_names")] IReadOnlyList<string> KnownNames,
        [property: JsonPropertyName("landmarks")] IReadOnlyList<string> Landmarks);

    private sealed record CategoryBody(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("description")] string? Description);

    private sealed record Response(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("location_text")] string? LocationText,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("confidence")] float Confidence,
        [property: JsonPropertyName("needs_more_info")] bool NeedsMoreInfo,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt_version")] string PromptVersion);

    public async Task<AiUnderstanding> UnderstandAsync(string text, IReadOnlyList<AiCategory> categories, IReadOnlyList<string> knownNames, IReadOnlyList<string> landmarks, CancellationToken ct)
    {
        var body = new Request(text, categories.Select(c => new CategoryBody(c.Key, c.Label, c.Description)).ToList(), knownNames, landmarks);
        using var response = await http.PostAsJsonAsync("/v1/complaints/understand", body, ct);
        response.EnsureSuccessStatusCode();
        var r = (await response.Content.ReadFromJsonAsync<Response>(ct))!;
        return new AiUnderstanding(r.Category, r.Summary, r.LocationText, r.Language, r.Confidence, r.NeedsMoreInfo, r.Model, r.PromptVersion);
    }
}

/// <summary>Turns a stored complaint's <see cref="ComplaintCreated"/> event into a background enrichment job.</summary>
public sealed class EnqueueEnrichmentOnCreate(IBackgroundJobClient jobs, AiOptions options) : IEventHandler<ComplaintCreated>
{
    public Task Handle(ComplaintCreated e, CancellationToken ct)
    {
        if (options.Enabled) jobs.Enqueue<ComplaintEnrichmentJob>(j => j.RunAsync(e.OrganizationId, e.ComplaintId, null!, CancellationToken.None));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Asks the AI service to categorise and summarise a complaint (CP-03). The complaint already exists, so any
/// failure here only affects the enrichment: after the last retry it is marked "failed" and stays in the
/// supervisor queue for manual triage. Safe to run twice.
/// </summary>
public sealed class ComplaintEnrichmentJob(AppDbContext db, IAiClient ai, Jobs.JobOptions jobOptions, ILogger<ComplaintEnrichmentJob> log)
{
    public Task RunAsync(Guid organizationId, Guid complaintId, PerformContext? context, CancellationToken ct)
        => RunCoreAsync(organizationId, complaintId, context?.GetJobParameter<int>("RetryCount") ?? 0, ct);

    /// <param name="retries">How many times Hangfire has already retried this job; at the configured maximum a failure is final.</param>
    public async Task RunCoreAsync(Guid organizationId, Guid complaintId, int retries, CancellationToken ct)
    {
        await db.ChangeTenantAsync(organizationId, null, ct);
        var complaint = await db.Complaints.SingleOrDefaultAsync(c => c.Id == complaintId, ct);
        if (complaint is null || complaint.AiStatus == "done") return;

        try
        {
            var categories = await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToListAsync(ct);
            var names = new List<string>();
            if (complaint.ReportedByUserId is { } reporter)
                names.AddRange(await db.Users.AsNoTracking().Where(u => u.Id == reporter).Select(u => u.DisplayName).ToListAsync(ct));

            // Landmark names and their aliases, so "near the mosque" can be read as a place (OR-03).
            var landmarkRows = await db.Landmarks.AsNoTracking().Where(l => l.IsActive).OrderBy(l => l.Name).Take(100).ToListAsync(ct);
            var landmarks = landmarkRows.Select(l => l.Aliases.Length == 0 ? l.Name : $"{l.Name} (also: {string.Join(", ", l.Aliases)})").ToList();

            var result = await ai.UnderstandAsync(
                complaint.Description,
                categories.Select(c => new AiCategory(c.Key, c.NameEn, null)).ToList(),
                names, landmarks, ct);

            complaint.AiStatus = "done";
            complaint.AiCategoryKey = result.Category;
            complaint.AiSummary = result.Summary;
            complaint.AiConfidence = result.Confidence;
            complaint.AiModel = result.Model;
            complaint.AiPromptVersion = result.PromptVersion;
            complaint.Language = result.Language;

            // The AI suggests; it never overrides a category a person already chose.
            if (complaint.CategoryId is null && categories.FirstOrDefault(c => c.Key == result.Category) is { } match)
            {
                complaint.CategoryId = match.Id;
                complaint.Priority = match.DefaultPriority;
            }
            if (complaint.LocationText is null && result.LocationText is not null) complaint.LocationText = result.LocationText;
            complaint.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "AI enrichment failed for complaint {Id} (retry {Retry})", complaintId, retries);
            if (retries >= jobOptions.Attempts)
            {
                complaint.AiStatus = "failed";
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            throw;
        }
    }
}
