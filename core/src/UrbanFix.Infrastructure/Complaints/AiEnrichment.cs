using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Hangfire;
using Hangfire.Server;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Complaints;

public sealed class AiOptions
{
    public string BaseUrl { get; set; } = "http://localhost:8000";
    public string ServiceToken { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>Turn on once the AI service has an embedding model configured; until then similar-complaint search stays empty.</summary>
    public bool EmbeddingsEnabled { get; set; }
    /// <summary>Price per million tokens by model name; leave a model out and its calls are counted in tokens only.</summary>
    public Dictionary<string, ModelPrice> Pricing { get; set; } = [];
}

public sealed record AiCategory(string Key, string Label, string? Description);

public sealed record AiUnderstanding(string Category, string Summary, string? LocationText, string Language, float Confidence, bool NeedsMoreInfo, string Model, string PromptVersion, AiUsage? Usage = null);

public sealed record AiComparison(float SameLocationLikelihood, string ChangeNote, bool NeedsMoreInfo, float Confidence, bool Degraded, string Model, string PromptVersion, AiUsage? Usage = null);

public sealed record AiEmbedding(IReadOnlyList<float> Vector, string Model, AiUsage? Usage = null);

public sealed record AiPhoto(IReadOnlyList<string> Labels, string Description, bool NeedsMoreInfo, float Confidence, bool SafetyFlag, bool Degraded, string Model, string PromptVersion, AiUsage? Usage = null);

public sealed record AiTranscript(string Transcript, float Confidence, string Language, bool LowConfidence, string Model, string PromptVersion, AiUsage? Usage = null);

/// <summary>Typed client for the AI service's <c>/v1/complaints/understand</c> contract (AI-02).</summary>
public sealed class HttpAiClient(HttpClient http) : IAiBackend
{
    private sealed record UsageBody(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);

    private static AiUsage? Usage(UsageBody? u) => u is null ? null : new AiUsage(u.InputTokens, u.OutputTokens);

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
        [property: JsonPropertyName("prompt_version")] string PromptVersion,
        [property: JsonPropertyName("usage")] UsageBody? Usage = null);

    private sealed record TranscriptResponse(
        [property: JsonPropertyName("transcript")] string Transcript,
        [property: JsonPropertyName("confidence")] float Confidence,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("low_confidence")] bool LowConfidence,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt_version")] string PromptVersion,
        [property: JsonPropertyName("usage")] UsageBody? Usage = null);

    private sealed record PhotoResponse(
        [property: JsonPropertyName("labels")] List<string> Labels,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("needs_more_info")] bool NeedsMoreInfo,
        [property: JsonPropertyName("confidence")] float Confidence,
        [property: JsonPropertyName("safety_flag")] bool SafetyFlag,
        [property: JsonPropertyName("degraded")] bool Degraded,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt_version")] string PromptVersion,
        [property: JsonPropertyName("usage")] UsageBody? Usage = null);

    private sealed record ComparisonResponse(
        [property: JsonPropertyName("same_location_likelihood")] float SameLocationLikelihood,
        [property: JsonPropertyName("change_note")] string ChangeNote,
        [property: JsonPropertyName("needs_more_info")] bool NeedsMoreInfo,
        [property: JsonPropertyName("confidence")] float Confidence,
        [property: JsonPropertyName("degraded")] bool Degraded,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt_version")] string PromptVersion,
        [property: JsonPropertyName("usage")] UsageBody? Usage = null);

    public async Task<AiComparison> CompareAsync(byte[] before, byte[] after, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in new[] { ("before", before), ("after", after) })
        {
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            form.Add(file, name, name);
        }
        using var response = await http.PostAsync("/v1/photos/compare", form, ct);
        response.EnsureSuccessStatusCode();
        var r = (await response.Content.ReadFromJsonAsync<ComparisonResponse>(ct))!;
        return new AiComparison(r.SameLocationLikelihood, r.ChangeNote, r.NeedsMoreInfo, r.Confidence, r.Degraded, r.Model, r.PromptVersion, Usage(r.Usage));
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("texts")] string[] Texts,
        [property: JsonPropertyName("known_names")] IReadOnlyList<string> KnownNames);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("vectors")] List<List<float>> Vectors,
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("usage")] UsageBody? Usage = null);

    public async Task<AiEmbedding> EmbedAsync(string text, IReadOnlyList<string> knownNames, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/v1/embeddings", new EmbedRequest([text], knownNames), ct);
        response.EnsureSuccessStatusCode();
        var r = (await response.Content.ReadFromJsonAsync<EmbedResponse>(ct))!;
        return new AiEmbedding(r.Vectors.Single(), r.Model, Usage(r.Usage));
    }

    public async Task<AiPhoto> InterpretPhotoAsync(byte[] image, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "photo");
        using var response = await http.PostAsync("/v1/photos/interpret", form, ct);
        response.EnsureSuccessStatusCode();
        var r = (await response.Content.ReadFromJsonAsync<PhotoResponse>(ct))!;
        return new AiPhoto(r.Labels, r.Description, r.NeedsMoreInfo, r.Confidence, r.SafetyFlag, r.Degraded, r.Model, r.PromptVersion, Usage(r.Usage));
    }

    public async Task<AiTranscript> TranscribeAsync(byte[] audio, string mimeType, string? languageHint, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        form.Add(file, "file", "voice-note");
        var url = languageHint is null ? "/v1/speech/transcribe" : $"/v1/speech/transcribe?language_hint={Uri.EscapeDataString(languageHint)}";
        using var response = await http.PostAsync(url, form, ct);
        response.EnsureSuccessStatusCode();
        var r = (await response.Content.ReadFromJsonAsync<TranscriptResponse>(ct))!;
        return new AiTranscript(r.Transcript, r.Confidence, r.Language, r.LowConfidence, r.Model, r.PromptVersion, Usage(r.Usage));
    }

    public async Task<AiUnderstanding> UnderstandAsync(string text, IReadOnlyList<AiCategory> categories, IReadOnlyList<string> knownNames, IReadOnlyList<string> landmarks, CancellationToken ct)
    {
        var body = new Request(text, categories.Select(c => new CategoryBody(c.Key, c.Label, c.Description)).ToList(), knownNames, landmarks);
        using var response = await http.PostAsJsonAsync("/v1/complaints/understand", body, ct);
        response.EnsureSuccessStatusCode();
        var r = (await response.Content.ReadFromJsonAsync<Response>(ct))!;
        return new AiUnderstanding(r.Category, r.Summary, r.LocationText, r.Language, r.Confidence, r.NeedsMoreInfo, r.Model, r.PromptVersion, Usage(r.Usage));
    }
}

/// <summary>Turns a stored complaint's <see cref="ComplaintCreated"/> event into a background enrichment job.</summary>
public sealed class EnqueueEnrichmentOnCreate(IBackgroundJobClient jobs, AiOptions options) : IEventHandler<ComplaintCreated>
{
    public Task Handle(ComplaintCreated e, CancellationToken ct)
    {
        // Always queued: with the AI off or over its allowance the job falls back to rules-only mode (AI-14).
        jobs.Enqueue<ComplaintEnrichmentJob>(j => j.RunAsync(e.OrganizationId, e.ComplaintId, null!, CancellationToken.None));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Asks the AI service to categorise and summarise a complaint (CP-03). The complaint already exists, so any
/// failure here only affects the enrichment: after the last retry it is marked "failed" and stays in the
/// supervisor queue for manual triage. Safe to run twice.
/// </summary>
public sealed class ComplaintEnrichmentJob(
    AppDbContext db, IAiClient ai, ComplaintPlanner planner, AiUsageContext usage, DuplicateFinder duplicates, AiOptions options, Jobs.JobOptions jobOptions, ILogger<ComplaintEnrichmentJob> log)
{
    public Task RunAsync(Guid organizationId, Guid complaintId, PerformContext? context, CancellationToken ct)
        => RunCoreAsync(organizationId, complaintId, context?.GetJobParameter<int>("RetryCount") ?? 0, ct);

    /// <param name="retries">How many times Hangfire has already retried this job; at the configured maximum a failure is final.</param>
    public async Task RunCoreAsync(Guid organizationId, Guid complaintId, int retries, CancellationToken ct)
    {
        await db.ChangeTenantAsync(organizationId, null, ct);
        var complaint = await db.Complaints.SingleOrDefaultAsync(c => c.Id == complaintId, ct);
        if (complaint is null || complaint.AiStatus is "done") return;
        usage.ComplaintId = complaintId;

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
            var chosenByPerson = complaint.CategoryId is not null;
            if (complaint.CategoryId is null && categories.FirstOrDefault(c => c.Key == result.Category) is { } match)
            {
                complaint.CategoryId = match.Id;
                complaint.Priority = match.DefaultPriority;
            }
            if (complaint.LocationText is null && result.LocationText is not null) complaint.LocationText = result.LocationText;
            complaint.UpdatedAt = DateTimeOffset.UtcNow;

            // Priority, safety and deadlines for the category now on the complaint; assigns it when the category allows (RT-02, RT-04 to RT-06).
            var current = categories.FirstOrDefault(c => c.Id == complaint.CategoryId);
            await planner.ApplyAsync(complaint, current, chosenByPerson, result.Confidence, ct);
            db.AiSuggestions.Add(new AiSuggestion
            {
                OrganizationId = organizationId, ComplaintId = complaintId, Status = "done", Attempt = retries + 1,
                CategoryKey = result.Category, Summary = result.Summary, Language = result.Language, LocationText = result.LocationText,
                NeedsMoreInfo = result.NeedsMoreInfo, Confidence = result.Confidence, Model = result.Model, PromptVersion = result.PromptVersion,
            });
            await db.SaveChangesAsync(ct);
            // Without embeddings the embedding job never runs, so look for duplicates on place, time and category here.
            if (!options.EmbeddingsEnabled) await duplicates.FindAsync(complaintId, ct);
        }
        catch (AiUnavailableException off)
        {
            // Switched off or over its allowance: retrying cannot help, so go straight to rules-only mode.
            await RulesOnlyAsync(complaint, noMatchStatus: "skipped", ct);
            log.LogInformation("AI not used for complaint {Id}: {Reason}", complaintId, off.Reason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "AI enrichment failed for complaint {Id} (retry {Retry})", complaintId, retries);
            // Every failed attempt is kept, so the supervisor and operators can see what happened.
            db.AiSuggestions.Add(new AiSuggestion
            {
                OrganizationId = organizationId, ComplaintId = complaintId, Status = "failed", Attempt = retries + 1,
                Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
            });
            if (retries >= jobOptions.Attempts)
            {
                await db.SaveChangesAsync(CancellationToken.None);
                await RulesOnlyAsync(complaint, noMatchStatus: "failed", CancellationToken.None);
                return;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>
    /// A plain keyword guess at the category (AI-14), so the complaint still reaches the supervisor queue with something filled in.
    /// It is only ever a suggestion: low confidence, so it can never route anything by itself, and it never replaces a person's choice.
    /// </summary>
    private async Task RulesOnlyAsync(Complaint complaint, string noMatchStatus, CancellationToken ct)
    {
        var categories = await db.Categories.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        var guess = RulesOnlyClassifier.Classify(complaint.Description, categories.Select(c => new RulesOnlyClassifier.Candidate(c.Key, c.NameEn)));
        if (guess is null)
        {
            complaint.AiStatus = noMatchStatus;
        }
        else
        {
            complaint.AiStatus = "rules_only";
            complaint.AiCategoryKey = guess.Key;
            complaint.AiConfidence = RulesOnlyClassifier.Confidence;
            complaint.AiModel = RulesOnlyClassifier.Model;
            complaint.AiPromptVersion = RulesOnlyClassifier.Version;
        }
        complaint.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Sends a newly attached voice note for transcription and a photo for interpretation.</summary>
public sealed class EnqueueTranscriptionOnAudio(IBackgroundJobClient jobs, AiOptions options) : IEventHandler<MediaAdded>
{
    public Task Handle(MediaAdded e, CancellationToken ct)
    {
        if (!options.Enabled) return Task.CompletedTask;
        if (e.Kind == "audio") jobs.Enqueue<VoiceTranscriptionJob>(j => j.RunAsync(e.OrganizationId, e.MediaId, null!, CancellationToken.None));
        else if (e.Kind == "image") jobs.Enqueue<PhotoInterpretationJob>(j => j.RunAsync(e.OrganizationId, e.MediaId, null!, CancellationToken.None));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Transcribes a voice note (AI-09) so staff can read it beside the audio. The recording is never lost: a failed
/// attempt only leaves the transcript empty. Every attempt is kept as a suggestion row (AI-15). Safe to run twice.
/// </summary>
public sealed class VoiceTranscriptionJob(
    AppDbContext db, IAiClient ai, ComplaintPlanner planner, AiUsageContext usage, IBackgroundJobClient jobs, Storage.IObjectStorage storage, Jobs.JobOptions jobOptions, ILogger<VoiceTranscriptionJob> log)
{
    public Task RunAsync(Guid organizationId, Guid mediaId, PerformContext? context, CancellationToken ct)
        => RunCoreAsync(organizationId, mediaId, context?.GetJobParameter<int>("RetryCount") ?? 0, ct);

    public async Task RunCoreAsync(Guid organizationId, Guid mediaId, int retries, CancellationToken ct)
    {
        await db.ChangeTenantAsync(organizationId, null, ct);
        var media = await db.ComplaintMedia.SingleOrDefaultAsync(m => m.Id == mediaId, ct);
        if (media is null || media.Kind != "audio" || media.TranscriptStatus == "done") return;
        usage.ComplaintId = media.ComplaintId;

        try
        {
            await using var stream = await storage.OpenReadAsync(media.StorageKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var complaintLanguage = await db.Complaints.AsNoTracking().Where(c => c.Id == media.ComplaintId).Select(c => c.Language).SingleOrDefaultAsync(ct);
            var result = await ai.TranscribeAsync(buffer.ToArray(), media.MimeType, complaintLanguage is "en" or "ur" or "roman_ur" ? complaintLanguage : null, ct);

            media.Transcript = result.Transcript; media.TranscriptConfidence = result.Confidence;
            media.TranscriptLanguage = result.Language; media.TranscriptStatus = "done";
            db.AiSuggestions.Add(new AiSuggestion
            {
                OrganizationId = organizationId, ComplaintId = media.ComplaintId, Task = "transcription", Status = "done", Attempt = retries + 1,
                Summary = result.Transcript, Language = result.Language, Confidence = result.Confidence, Model = result.Model, PromptVersion = result.PromptVersion,
            });
            var complaint = await db.Complaints.SingleOrDefaultAsync(c => c.Id == media.ComplaintId, ct);
            // Danger words in what was said make it a safety matter, whatever the typed text said (AI-10).
            if (complaint is not null && SafetyTerms.IsSafety(result.Transcript)) await planner.FlagSafetyAsync(complaint, ct);

            // A complaint sent as only a voice note gets its words from the transcript, and is then read by the AI like any other.
            var useAsDescription = complaint is not null && complaint.Description == IntakeText.MediaOnly && !string.IsNullOrWhiteSpace(result.Transcript);
            if (useAsDescription)
            {
                complaint!.Description = result.Transcript.Trim().Length > 4000 ? result.Transcript.Trim()[..4000] : result.Transcript.Trim();
                complaint.AiStatus = "pending";
                complaint.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            if (useAsDescription) jobs.Enqueue<ComplaintEnrichmentJob>(j => j.RunAsync(organizationId, complaint!.Id, null!, CancellationToken.None));
        }
        catch (AiUnavailableException)
        {
            media.TranscriptStatus = "skipped"; // the recording is kept; staff can listen to it
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Transcription failed for media {Id} (retry {Retry})", mediaId, retries);
            db.AiSuggestions.Add(new AiSuggestion
            {
                OrganizationId = organizationId, ComplaintId = media.ComplaintId, Task = "transcription", Status = "failed", Attempt = retries + 1,
                Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
            });
            if (retries >= jobOptions.Attempts)
            {
                media.TranscriptStatus = "failed";
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}

/// <summary>
/// Looks at a complaint photo (AI-08) and keeps the fixed labels and cautious description beside it. A hazard label
/// (open manhole, exposed wire, sewage overflow, fallen tree) flags the complaint for safety (AI-10). Advisory only.
/// </summary>
public sealed class PhotoInterpretationJob(
    AppDbContext db, IAiClient ai, ComplaintPlanner planner, AiUsageContext usage, IBackgroundJobClient jobs, AiOptions options, Storage.IObjectStorage storage, Jobs.JobOptions jobOptions, ILogger<PhotoInterpretationJob> log)
{
    public Task RunAsync(Guid organizationId, Guid mediaId, PerformContext? context, CancellationToken ct)
        => RunCoreAsync(organizationId, mediaId, context?.GetJobParameter<int>("RetryCount") ?? 0, ct);

    public async Task RunCoreAsync(Guid organizationId, Guid mediaId, int retries, CancellationToken ct)
    {
        await db.ChangeTenantAsync(organizationId, null, ct);
        var media = await db.ComplaintMedia.SingleOrDefaultAsync(m => m.Id == mediaId, ct);
        if (media is null || media.Kind != "image" || media.AiStatus == "done") return;
        usage.ComplaintId = media.ComplaintId;

        try
        {
            await using var stream = await storage.OpenReadAsync(media.StorageKey, ct);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            var result = await ai.InterpretPhotoAsync(buffer.ToArray(), ct);

            media.AiStatus = "done"; media.AiLabels = [.. result.Labels]; media.AiDescription = result.Description; media.AiNeedsMoreInfo = result.NeedsMoreInfo;
            db.AiSuggestions.Add(new AiSuggestion
            {
                OrganizationId = organizationId, ComplaintId = media.ComplaintId, Task = "photo", Status = "done", Attempt = retries + 1,
                CategoryKey = result.Labels.FirstOrDefault(), Summary = result.Description, NeedsMoreInfo = result.NeedsMoreInfo,
                Confidence = result.Confidence, Model = result.Model, PromptVersion = result.PromptVersion,
            });
            if (result.SafetyFlag && await db.Complaints.SingleOrDefaultAsync(c => c.Id == media.ComplaintId, ct) is { } complaint)
                await planner.FlagSafetyAsync(complaint, ct);
            await db.SaveChangesAsync(ct);
            if (options.EmbeddingsEnabled && !result.Degraded)
                jobs.Enqueue<EmbeddingJob>(j => j.RunAsync(organizationId, media.ComplaintId, media.Id, null!, CancellationToken.None));
        }
        catch (AiUnavailableException)
        {
            media.AiStatus = "skipped";
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Photo interpretation failed for media {Id} (retry {Retry})", mediaId, retries);
            db.AiSuggestions.Add(new AiSuggestion
            {
                OrganizationId = organizationId, ComplaintId = media.ComplaintId, Task = "photo", Status = "failed", Attempt = retries + 1,
                Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message,
            });
            if (retries >= jobOptions.Attempts)
            {
                media.AiStatus = "failed";
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }
}
