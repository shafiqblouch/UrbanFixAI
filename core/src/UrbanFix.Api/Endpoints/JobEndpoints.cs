using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.OrgSetup;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.SharedKernel;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Api.Endpoints;

/// <summary>
/// Job workflow (JB-02 to JB-06) and estimates / actual costs (JC-02, JC-04).
/// Technician: accept, start, update, attach evidence, complete, cannot-complete. Supervisor: assign, sign off, return.
/// </summary>
public static class JobEndpoints
{
    public sealed record AssignJob(Guid? TeamId, Guid? UserId);
    public sealed record StartWork(string? Note, double? Latitude, double? Longitude);
    public sealed record AddUpdate(string? Note, double? Latitude, double? Longitude, int[]? ChecklistDone);
    public sealed record CompleteJob(string? Note);
    public sealed record ReasonRequest(string? Reason);
    public sealed record CostLineInput(string Kind, Guid? PriceItemId, string? Description, decimal Quantity, string? Unit, decimal? UnitRate);
    public sealed record SaveEstimate(string? Note, IReadOnlyList<CostLineInput> Lines);
    public sealed record SaveCosts(IReadOnlyList<CostLineInput> Lines);

    /// <summary>`DueAt` is when the job counts as overdue: the complaint's creation time plus the limit for its priority.</summary>
    public sealed record JobListItemDto(JobDto Job, string Reference, string Priority, string Description, DateTimeOffset DueAt);
    public sealed record UpdateDto(Guid Id, Guid? AuthorId, string Kind, string? FromStatus, string? ToStatus, string? Note, double? Latitude, double? Longitude, DateTimeOffset CreatedAt);
    public sealed record CostLineDto(Guid Id, string Kind, string Description, Guid? PriceItemId, decimal Quantity, string Unit, decimal UnitRate, decimal Amount);
    public sealed record EstimateDto(Guid Id, int Version, string Status, string? Note, decimal Total, Guid? CreatedBy, DateTimeOffset CreatedAt, IReadOnlyList<CostLineDto> Lines, string ApprovalStatus = "NotRequired", Guid? ApprovalId = null);
    public sealed record EvidenceMediaDto(Guid Id, string Stage, string Kind, string Url, string? ThumbUrl, double? Latitude, double? Longitude, Guid? UploadedBy, DateTimeOffset CreatedAt);
    public sealed record ComparisonDto(
        Guid Id, Guid BeforeMediaId, Guid AfterMediaId, float SameLocationLikelihood, string ChangeNote, bool NeedsMoreInfo, float Confidence,
        bool Degraded, string? Model, string? PromptVersion, DateTimeOffset CreatedAt, bool Advisory = true);
    public sealed record VarianceDto(decimal? Estimated, decimal Actual, decimal? Difference, decimal? Percent, bool Overrun = false, int ThresholdPercent = 0);
    public sealed record OverrunDto(Guid JobId, string Number, string Reference, string Status, decimal Estimated, decimal Actual, decimal Difference, double Percent, DateTimeOffset? FlaggedAt);
    public sealed record SetChargeable(bool Chargeable, Guid? HouseId);
    public sealed record CostsResult(IReadOnlyList<CostLineDto> Lines, VarianceDto Variance);
    public sealed record JobDetailDto(
        JobDto Job, string? CompletionNote, IReadOnlyList<ChecklistItem> Checklist, IReadOnlyList<UpdateDto> Timeline,
        IReadOnlyList<EvidenceMediaDto> Evidence, EstimateDto? Estimate, IReadOnlyList<CostLineDto> Actuals, VarianceDto Variance);

    public static void MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/jobs").WithTags("Jobs");
        var read = AuthPolicies.For(Permissions.ComplaintRead);
        var assign = AuthPolicies.For(Permissions.ComplaintAssign);
        var work = AuthPolicies.For(Permissions.ComplaintWork);
        var close = AuthPolicies.For(Permissions.ComplaintClose);

        g.MapGet("", ListAsync).Produces<List<JobListItemDto>>(200).RequireAuthorization(read);
        g.MapGet("/{id:guid}", GetAsync).Produces<JobDetailDto>(200).RequireAuthorization(read);
        g.MapPost("/{id:guid}/assign", AssignAsync).Produces<JobDto>(200).RequireAuthorization(assign);
        g.MapPost("/{id:guid}/accept", AcceptAsync).Produces<JobDto>(200).RequireAuthorization(work);
        g.MapPost("/{id:guid}/start", StartAsync).Produces<JobDto>(200).RequireAuthorization(work);
        g.MapPost("/{id:guid}/updates", UpdateAsync).Produces<JobDto>(200).RequireAuthorization(work);
        g.MapPost("/{id:guid}/media", AddMediaAsync).DisableAntiforgery().Produces<MediaDto>(201).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/{id:guid}/media/from-upload/{uploadId:guid}", FinishUploadAsync).Produces<MediaDto>(201).RequireAuthorization(AuthPolicies.Verified);
        g.MapPut("/{id:guid}/costs", SaveCostsAsync).Produces<CostsResult>(200).RequireAuthorization(read);
        g.MapGet("/{id:guid}/comparison", GetComparisonAsync).Produces<ComparisonDto>(200).RequireAuthorization(close);
        g.MapPost("/{id:guid}/comparison", RunComparisonAsync).Produces<ComparisonDto>(200).RequireAuthorization(close);
        g.MapPost("/{id:guid}/complete", CompleteAsync).Produces<JobDto>(200).RequireAuthorization(work);
        g.MapPost("/{id:guid}/cannot-complete", CannotCompleteAsync).Produces<JobDto>(200).RequireAuthorization(work);
        g.MapPost("/{id:guid}/sign-off", SignOffAsync).Produces<JobDto>(200).RequireAuthorization(close);
        g.MapPost("/{id:guid}/return", ReturnAsync).Produces<JobDto>(200).RequireAuthorization(close);

        g.MapGet("/overruns", OverrunsAsync).Produces<List<OverrunDto>>(200).RequireAuthorization(assign);
        g.MapPost("/{id:guid}/chargeable", ChargeableAsync).Produces<JobDto>(200).RequireAuthorization(assign);
        g.MapPost("/{id:guid}/estimates", SaveEstimateAsync).Produces<EstimateDto>(201).RequireAuthorization(AuthPolicies.For(Permissions.EstimateCreate));
        g.MapGet("/{id:guid}/estimates", EstimatesAsync).Produces<List<EstimateDto>>(200).RequireAuthorization(read);
    }

    // Helpers -----------------------------------------------------------------------------------------------------

    private sealed record Ctx(Actor Actor, Job Job, Complaint Complaint)
    {
        public bool IsAssignee => Job.AssignedUserId == Actor.UserId && Actor.Has(Permissions.ComplaintWork);
    }

    private static async Task<Ctx?> LoadAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, CancellationToken ct)
    {
        var actor = await ActorAsync(user, memberships, ct);
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || !actor.CanSeeJob(job)) return null;
        var complaint = await db.Complaints.SingleAsync(c => c.Id == job.ComplaintId, ct);
        return new Ctx(actor, job, complaint);
    }

    private static void Log(AppDbContext db, Ctx x, string kind, string? from = null, string? to = null, string? note = null, double? lat = null, double? lon = null) =>
        db.JobUpdates.Add(new JobUpdate
        {
            OrganizationId = x.Job.OrganizationId, JobId = x.Job.Id, AuthorId = x.Actor.UserId, Kind = kind,
            FromStatus = from, ToStatus = to, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(), Latitude = lat, Longitude = lon,
        });

    /// <summary>Moves the job along the state machine, logging it on the timeline. Returns false for an illegal move.</summary>
    private static bool MoveJob(AppDbContext db, IEventBus bus, Ctx x, string to, string? note = null, double? lat = null, double? lon = null)
    {
        var from = x.Job.Status;
        if (!JobStateMachine.CanMove(from, to)) return false;
        x.Job.Status = to;
        var now = DateTimeOffset.UtcNow;
        switch (to)
        {
            case JobStatus.Accepted: x.Job.AcceptedAt = now; break;
            case JobStatus.InProgress: x.Job.StartedAt ??= now; break;
            case JobStatus.PendingVerification: x.Job.SubmittedAt = now; break;
            case JobStatus.Completed: x.Job.CompletedAt = now; break;
        }
        Log(db, x, "status", from, to, note, lat, lon);
        bus.Publish(new JobStatusChanged(x.Job.OrganizationId, x.Job.Id, x.Job.ComplaintId, x.Job.Number, from, to, x.Actor.UserId, note));
        return true;
    }

    /// <summary>System-driven complaint move that follows a job event; the job rules, not the complaint permission table, authorise it.</summary>
    private static void MoveComplaint(AppDbContext db, IEventBus bus, Ctx x, ComplaintStatus to, string? reason)
    {
        var c = x.Complaint;
        if (c.Status == to) return;
        var from = c.Status;
        c.Status = to;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        c.StatusReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (to == ComplaintStatus.Resolved) c.ResolvedAt = DateTimeOffset.UtcNow;
        Record(db, bus, c, from, x.Actor.UserId, c.StatusReason);
    }

    private static IResult Illegal(Job j, string to) => Results.Conflict(new { error = "illegal_job_transition", from = j.Status, to });

    // Read ----------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        string? status, bool? mine, int? limit, int? offset, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, CancellationToken ct)
    {
        var actor = await ActorAsync(user, memberships, ct);
        var q = db.Jobs.AsNoTracking().AsQueryable();
        if (!actor.SeesAllJobs || mine == true) q = q.Where(j => j.AssignedUserId == actor.UserId);
        if (!string.IsNullOrEmpty(status)) q = q.Where(j => j.Status == status);
        var rows = await (from j in q
                          join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                          orderby j.CreatedAt descending
                          select new { Job = j, c.Reference, c.Priority, c.Description, c.CreatedAt })
            .Skip(Math.Max(offset ?? 0, 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return Results.Ok(rows.Select(r => new JobListItemDto(ToDto(r.Job), r.Reference, r.Priority, r.Description, r.CreatedAt + Sla.For(r.Priority))));
    }

    private static async Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IObjectStorage storage, CancellationToken ct)
    {
        var actor = await ActorAsync(user, memberships, ct);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null || !actor.CanSeeJob(job)) return Results.NotFound();

        var timeline = await db.JobUpdates.AsNoTracking().Where(u => u.JobId == id).OrderBy(u => u.CreatedAt).ToListAsync(ct);
        var media = await db.ComplaintMedia.AsNoTracking().Where(m => m.JobId == id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var actuals = await db.JobCosts.AsNoTracking().Where(c => c.JobId == id).OrderBy(c => c.CreatedAt).ToListAsync(ct);
        var estimate = await CurrentEstimateAsync(db, id, ct);

        return Results.Ok(new JobDetailDto(
            ToDto(job), job.CompletionNote, JobEvidence.ParseChecklist(job.Checklist),
            timeline.Select(u => new UpdateDto(u.Id, u.AuthorId, u.Kind, u.FromStatus, u.ToStatus, u.Note, u.Latitude, u.Longitude, u.CreatedAt)).ToList(),
            media.Select(m => new EvidenceMediaDto(m.Id, m.Stage!, m.Kind, storage.CreateDownloadUrl(m.StorageKey),
                m.ThumbKey is null ? null : storage.CreateDownloadUrl(m.ThumbKey), m.Latitude, m.Longitude, m.UploadedBy, m.CreatedAt)).ToList(),
            estimate is null ? null : await ToEstimateDtoAsync(db, estimate, ct),
            actuals.Select(LineDto).ToList(),
            Variance(estimate?.Total, actuals.Sum(a => a.Amount), await OverrunThresholdAsync(db, ct))));
    }

    // Before/after comparison (AI-12) ---------------------------------------------------------------------------

    private static ComparisonDto ComparisonOf(JobComparison c) =>
        new(c.Id, c.BeforeMediaId, c.AfterMediaId, c.SameLocationLikelihood, c.ChangeNote, c.NeedsMoreInfo, c.Confidence, c.Degraded, c.Model, c.PromptVersion, c.CreatedAt);

    private static async Task<IResult> GetComparisonAsync(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.JobComparisons.AsNoTracking().Where(c => c.JobId == id).OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct) is { } c
            ? Results.Ok(ComparisonOf(c)) : Results.NotFound();

    /// <summary>Compares the latest "before" and "after" photos. Asking again for the same pair returns the stored answer unless <c>refresh</c> is set.</summary>
    private static async Task<IResult> RunComparisonAsync(
        Guid id, bool? refresh, ClaimsPrincipal user, AppDbContext db, IAiClient ai, AiUsageContext usage, IObjectStorage storage, CancellationToken ct)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == id, ct)) return Results.NotFound();
        var photos = await db.ComplaintMedia.AsNoTracking().Where(m => m.JobId == id && m.Kind == "image").OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
        var before = photos.FirstOrDefault(m => m.Stage == EvidenceStage.Before);
        var after = photos.FirstOrDefault(m => m.Stage == EvidenceStage.After);
        if (before is null || after is null) return Results.Conflict(new { error = "needs_before_and_after_photos" });

        if (refresh != true && await db.JobComparisons.AsNoTracking()
                .Where(c => c.JobId == id && c.BeforeMediaId == before.Id && c.AfterMediaId == after.Id && !c.Degraded)
                .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct) is { } existing)
            return Results.Ok(ComparisonOf(existing));

        async Task<byte[]> Read(ComplaintMedia m)
        {
            await using var s = await storage.OpenReadAsync(m.StorageKey, ct);
            using var buffer = new MemoryStream();
            await s.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }

        AiComparison result;
        usage.ComplaintId = before.ComplaintId;
        try { result = await ai.CompareAsync(await Read(before), await Read(after), ct); }
        catch (AiUnavailableException off) { return Results.Json(new { error = "ai_not_available", reason = off.Reason }, statusCode: 503); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return Results.Json(new { error = "ai_unavailable" }, statusCode: 502);
        }

        var row = new JobComparison
        {
            OrganizationId = before.OrganizationId, JobId = id, BeforeMediaId = before.Id, AfterMediaId = after.Id,
            SameLocationLikelihood = result.SameLocationLikelihood, ChangeNote = result.ChangeNote, NeedsMoreInfo = result.NeedsMoreInfo,
            Confidence = result.Confidence, Degraded = result.Degraded, Model = result.Model, PromptVersion = result.PromptVersion,
            RequestedBy = Guid.Parse(user.FindFirstValue("sub")!),
        };
        db.JobComparisons.Add(row);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ComparisonOf(row));
    }

    private static VarianceDto Variance(decimal? estimated, decimal actual, int thresholdPercent = 0)
    {
        decimal? diff = estimated is null ? null : actual - estimated;
        decimal? pct = estimated is > 0 ? Math.Round((actual - estimated.Value) / estimated.Value * 100, 1) : null;
        return new VarianceDto(estimated, actual, diff, pct, thresholdPercent > 0 && ApprovalRules.IsOverrun(estimated, actual, thresholdPercent), thresholdPercent);
    }

    private static async Task<int> OverrunThresholdAsync(AppDbContext db, CancellationToken ct) =>
        OrgPolicy.From(await db.OrgSettings.AsNoTracking().Select(s => s.Settings).SingleOrDefaultAsync(ct) ?? "{}").OverrunPercent;

    // Assignment (JB-02) -------------------------------------------------------------------------------------------------

    private static async Task<IResult> AssignAsync(
        Guid id, AssignJob req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null) return Results.NotFound();
        if (x.Job.Status is not (JobStatus.Open or JobStatus.Accepted or JobStatus.InProgress)) return Results.Conflict(new { error = "job_not_assignable", status = x.Job.Status });
        if (req.TeamId is null && req.UserId is null) return Results.BadRequest(new { error = "team_or_user_required" });

        var teamId = req.TeamId ?? x.Job.AssignedTeamId;
        if (req.TeamId is { } t && !await db.Teams.AnyAsync(team => team.Id == t && team.IsActive, ct)) return Results.BadRequest(new { error = "team_not_found" });

        var userId = req.UserId ?? x.Job.AssignedUserId;
        if (req.UserId is { } u)
        {
            if (!await db.Memberships.AnyAsync(m => m.UserId == u && m.Role == Role.Technician && m.IsActive, ct)) return Results.BadRequest(new { error = "not_a_technician" });
        }
        // A technician must belong to the job's team; changing the team drops an assignee who is not in the new one.
        if (teamId is { } tid && userId is { } uid && !await db.TeamMembers.AnyAsync(m => m.TeamId == tid && m.UserId == uid, ct))
        {
            if (req.UserId is not null) return Results.BadRequest(new { error = "user_not_in_team" });
            userId = null;
        }

        var changedUser = userId != x.Job.AssignedUserId;
        x.Job.AssignedTeamId = teamId;
        x.Job.AssignedUserId = userId;
        if (changedUser && x.Job.Status == JobStatus.Accepted)
        {
            // The new technician has not agreed to the job yet. (A job already in progress carries on with its new owner.)
            x.Job.Status = JobStatus.Open;
            x.Job.AcceptedAt = null;
            Log(db, x, "status", JobStatus.Accepted, JobStatus.Open, "Reassigned");
        }
        Log(db, x, userId is null || !changedUser ? "assigned_team" : "assigned", note: userId is { } n && changedUser ? $"user:{n}" : $"team:{teamId}");
        bus.Publish(new JobAssigned(x.Job.OrganizationId, x.Job.Id, x.Job.ComplaintId, x.Job.Number, changedUser ? userId : null, teamId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    private static async Task<IResult> AcceptAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null || !x.IsAssignee) return Results.NotFound();
        if (!MoveJob(db, bus, x, JobStatus.Accepted)) return Illegal(x.Job, JobStatus.Accepted);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    // Progress (JB-03) --------------------------------------------------------------------------------------------------

    private static async Task<IResult> StartAsync(
        Guid id, StartWork? req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null || !x.IsAssignee) return Results.NotFound();
        // An estimate above the society's limit must be approved before work begins (JC-03).
        if (await CurrentEstimateAsync(db, id, ct) is { } estimate && !ApprovalRules.AllowsWork(estimate.ApprovalStatus))
            return Results.Conflict(new { error = "estimate_not_approved", approval = estimate.ApprovalStatus });
        if (!MoveJob(db, bus, x, JobStatus.InProgress, req?.Note, req?.Latitude, req?.Longitude)) return Illegal(x.Job, JobStatus.InProgress);
        if (x.Complaint.Status == ComplaintStatus.Assigned) MoveComplaint(db, bus, x, ComplaintStatus.InProgress, null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, AddUpdate req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null || !x.IsAssignee) return Results.NotFound();
        if (x.Job.Status != JobStatus.InProgress) return Results.Conflict(new { error = "job_not_in_progress", status = x.Job.Status });
        if (req.Latitude is < -90 or > 90 || req.Longitude is < -180 or > 180) return Results.BadRequest(new { error = "invalid_coordinates" });
        if ((req.Latitude is null) != (req.Longitude is null)) return Results.BadRequest(new { error = "invalid_coordinates" });

        if (req.ChecklistDone is { } done)
        {
            var items = JobEvidence.ParseChecklist(x.Job.Checklist);
            if (done.Any(i => i < 0 || i >= items.Count)) return Results.BadRequest(new { error = "invalid_checklist_item" });
            x.Job.Checklist = JobEvidence.ToJson(items.Select((item, i) => item with { Done = done.Contains(i) }));
        }
        if (string.IsNullOrWhiteSpace(req.Note) && req.ChecklistDone is null && req.Latitude is null) return Results.BadRequest(new { error = "empty_update" });

        Log(db, x, req.ChecklistDone is not null && string.IsNullOrWhiteSpace(req.Note) ? "checklist" : "note", note: req.Note, lat: req.Latitude, lon: req.Longitude);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    // Evidence (JB-05) ----------------------------------------------------------------------------------------------------

    private static IResult? CheckMediaAccess(Ctx? x, string? stage)
    {
        if (x is null || !(x.IsAssignee || x.Actor.Has(Permissions.ComplaintAssign))) return Results.NotFound();
        if (stage is null || !EvidenceStage.All.Contains(stage)) return Results.BadRequest(new { error = "invalid_stage" });
        if (x.Job.Status is not (JobStatus.Accepted or JobStatus.InProgress or JobStatus.PendingVerification))
            return Results.Conflict(new { error = "job_not_open_for_evidence", status = x.Job.Status });
        return null;
    }

    private static async Task<IResult> AddMediaAsync(
        Guid id, string? stage, [FromForm] IFormFile file, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships,
        IMediaProcessor processor, IObjectStorage storage, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (CheckMediaAccess(x, stage) is { } denied) return denied;
        if (file.Length > FileSniffer.MaxBytes) return Results.Json(new { error = "file_too_large" }, statusCode: 413);

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await SaveEvidenceAsync(ms.ToArray(), x!, stage!, db, processor, storage, ct);
    }

    private static async Task<IResult> FinishUploadAsync(
        Guid id, Guid uploadId, string? stage, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships,
        IMediaProcessor processor, IObjectStorage storage, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (CheckMediaAccess(x, stage) is { } denied) return denied;
        var u = await db.Uploads.SingleOrDefaultAsync(s => s.Id == uploadId && s.UserId == x!.Actor.UserId, ct);
        if (u is null) return Results.NotFound();
        if (u.ReceivedBytes != u.TotalBytes) return Results.Conflict(new { error = "upload_incomplete", receivedBytes = u.ReceivedBytes, totalBytes = u.TotalBytes });

        using var all = new MemoryStream();
        foreach (var key in u.ChunkKeys)
        {
            await using var part = await storage.OpenReadAsync(key, ct);
            await part.CopyToAsync(all, ct);
        }
        var result = await SaveEvidenceAsync(all.ToArray(), x!, stage!, db, processor, storage, ct);
        u.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var key in u.ChunkKeys) await storage.DeleteAsync(key, ct);
        return result;
    }

    private static async Task<IResult> SaveEvidenceAsync(byte[] bytes, Ctx x, string stage, AppDbContext db, IMediaProcessor processor, IObjectStorage storage, CancellationToken ct)
    {
        var result = await SaveMediaAsync(bytes, x.Complaint, x.Actor, db, processor, storage, ct, x.Job.Id, stage);
        // Only a newly stored photo belongs on the timeline; a duplicate upload comes back as 200.
        if (result is IStatusCodeHttpResult { StatusCode: 201 })
        {
            Log(db, x, "media", note: stage);
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    // Completion (JB-05, JB-06, JB-04) ---------------------------------------------------------------------------------------

    private static async Task<IResult> CompleteAsync(
        Guid id, CompleteJob req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null || !x.IsAssignee) return Results.NotFound();
        if (x.Job.Status != JobStatus.InProgress) return Illegal(x.Job, JobStatus.PendingVerification);

        var note = string.IsNullOrWhiteSpace(req.Note) ? x.Job.CompletionNote : req.Note.Trim();
        var media = await db.ComplaintMedia.AsNoTracking().Where(m => m.JobId == id && m.Kind == "image").Select(m => m.Stage).ToListAsync(ct);
        var hasCosts = await db.JobCosts.AnyAsync(c => c.JobId == id, ct);
        var missing = JobEvidence.Missing(x.Job.EvidenceRule, media.Contains(EvidenceStage.Before), media.Contains(EvidenceStage.After),
            JobEvidence.ParseChecklist(x.Job.Checklist), note, x.Job.RequiresCosts, hasCosts);
        if (missing.Count > 0) return Results.UnprocessableEntity(new { error = "evidence_missing", missing });

        x.Job.CompletionNote = note;
        MoveJob(db, bus, x, JobStatus.PendingVerification, note);

        // What the store issued against what the technician says was used: differences are flagged for the supervisor (JC-07).
        var off = (await MaterialsReport.ForJobAsync(db, id, ct)).Where(m => m.Flagged).ToList();
        if (off.Count > 0)
        {
            Log(db, x, "materials", note: "Materials do not add up: " + string.Join("; ", off.Select(m => $"{m.Item} issued {m.Issued:0.###}, returned {m.Returned:0.###}, used {m.Used:0.###}")));
            bus.Publish(new MaterialsMismatch(x.Job.OrganizationId, x.Job.Id, x.Job.Number, off.Count));
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    private static async Task<IResult> CannotCompleteAsync(
        Guid id, ReasonRequest req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null || !x.IsAssignee) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "reason_required" });
        if (!MoveJob(db, bus, x, JobStatus.Returned, req.Reason)) return Illegal(x.Job, JobStatus.Returned);

        // The complaint goes back to the supervisor with the reason; they can raise a new job (JB-04).
        MoveComplaint(db, bus, x, ComplaintStatus.UnderReview, req.Reason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    private static async Task<IResult> SignOffAsync(
        Guid id, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null) return Results.NotFound();
        if (!MoveJob(db, bus, x, JobStatus.Completed)) return Illegal(x.Job, JobStatus.Completed);
        MoveComplaint(db, bus, x, ComplaintStatus.Resolved, null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    private static async Task<IResult> ReturnAsync(
        Guid id, ReasonRequest req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "reason_required" });
        if (!MoveJob(db, bus, x, JobStatus.InProgress, req.Reason)) return Illegal(x.Job, JobStatus.InProgress);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(x.Job));
    }

    // Estimates (JC-02) and actual costs (JC-04) -----------------------------------------------------------------------------

    /// <summary>Turns user input into priced lines. Price-list items always take the rate in force now; only non-material lines may carry a typed rate.</summary>
    private static async Task<(List<(string Kind, string Description, Guid? ItemId, Guid? RateId, decimal Quantity, string Unit, decimal Rate)>? Lines, IResult? Error)> PriceAsync(
        IReadOnlyList<CostLineInput>? input, AppDbContext db, CancellationToken ct)
    {
        if (input is null || input.Count == 0) return (null, Results.BadRequest(new { error = "lines_required" }));
        if (input.Count > 100) return (null, Results.BadRequest(new { error = "too_many_lines" }));

        var now = DateTimeOffset.UtcNow;
        var itemIds = input.Where(l => l.PriceItemId is not null).Select(l => l.PriceItemId!.Value).Distinct().ToList();
        var items = await db.PriceItems.AsNoTracking().Where(i => itemIds.Contains(i.Id) && i.IsActive).ToDictionaryAsync(i => i.Id, ct);
        var rates = await db.PriceRates.AsNoTracking().Where(r => itemIds.Contains(r.ItemId) && r.EffectiveFrom <= now).ToListAsync(ct);

        var lines = new List<(string, string, Guid?, Guid?, decimal, string, decimal)>();
        foreach (var l in input)
        {
            if (!CostKind.All.Contains(l.Kind)) return (null, Results.BadRequest(new { error = "invalid_kind" }));
            if (l.Quantity <= 0) return (null, Results.BadRequest(new { error = "invalid_quantity" }));
            if (l.PriceItemId is { } itemId)
            {
                if (!items.TryGetValue(itemId, out var item)) return (null, Results.BadRequest(new { error = "price_item_not_found" }));
                var rate = rates.Where(r => r.ItemId == itemId).OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.CreatedAt).FirstOrDefault();
                if (rate is null) return (null, Results.BadRequest(new { error = "no_current_rate" }));
                lines.Add((l.Kind, item.Name, itemId, rate.Id, l.Quantity, item.Unit, rate.Rate));
                continue;
            }
            // Materials must come from the price list; labour, transport and other lines carry a typed rate.
            if (l.Kind == "material") return (null, Results.BadRequest(new { error = "price_item_required" }));
            if (string.IsNullOrWhiteSpace(l.Description)) return (null, Results.BadRequest(new { error = "description_required" }));
            if (l.UnitRate is not { } typed || typed < 0) return (null, Results.BadRequest(new { error = "invalid_rate" }));
            lines.Add((l.Kind, l.Description.Trim(), null, null, l.Quantity, string.IsNullOrWhiteSpace(l.Unit) ? (l.Kind == "labour" ? "hour" : "each") : l.Unit.Trim(), typed));
        }
        return (lines, null);
    }

    private static T Fill<T>(T line, Guid org, (string Kind, string Description, Guid? ItemId, Guid? RateId, decimal Quantity, string Unit, decimal Rate) p) where T : CostLine
    {
        line.OrganizationId = org; line.Kind = p.Kind; line.Description = p.Description; line.PriceItemId = p.ItemId; line.RateId = p.RateId;
        line.Quantity = p.Quantity; line.Unit = p.Unit; line.UnitRate = p.Rate; line.Amount = Pricing.Amount(p.Quantity, p.Rate);
        return line;
    }

    private static async Task<IResult> SaveEstimateAsync(
        Guid id, SaveEstimate req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, ApprovalService approvals, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        // A technician estimates only their own jobs; supervisors and managers estimate any.
        if (x is null || !(x.IsAssignee || x.Actor.Has(Permissions.ComplaintAssign))) return Results.NotFound();
        // The estimate is agreed before work starts (JC-02).
        if (x.Job.Status is not (JobStatus.Open or JobStatus.Accepted)) return Results.Conflict(new { error = "work_started", status = x.Job.Status });

        var (priced, error) = await PriceAsync(req.Lines, db, ct);
        if (error is not null) return error;

        var org = x.Job.OrganizationId;
        var previous = await db.Estimates.Where(e => e.JobId == id).OrderByDescending(e => e.Version).ToListAsync(ct);
        foreach (var p in previous.Where(e => e.Status == "Current")) p.Status = "Superseded";
        await approvals.CancelPendingForJobAsync(id, ct); // a revision replaces any estimate still waiting for an answer
        // Superseding must reach the database before the new Current row, because of the one-current unique index.
        await db.SaveChangesAsync(ct);

        var estimate = new Estimate
        {
            OrganizationId = org, JobId = id, Version = (previous.FirstOrDefault()?.Version ?? 0) + 1,
            Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim(), CreatedBy = x.Actor.UserId,
        };
        var lines = priced!.Select(p => Fill(new EstimateLine { EstimateId = estimate.Id }, org, p)).ToList();
        estimate.Total = Pricing.Total(lines);
        db.Estimates.Add(estimate);
        db.EstimateLines.AddRange(lines);
        Log(db, x, "estimate", note: $"v{estimate.Version} total {estimate.Total:0.00}");
        await approvals.RequestForEstimateAsync(estimate, x.Job, x.Actor.UserId, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/jobs/{id}/estimates", ToEstimateDto(estimate, lines));
    }

    private static async Task<IResult> EstimatesAsync(Guid id, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        if (x is null) return Results.NotFound();
        var estimates = await db.Estimates.AsNoTracking().Where(e => e.JobId == id).OrderByDescending(e => e.Version).ToListAsync(ct);
        var ids = estimates.Select(e => e.Id).ToList();
        var lines = (await db.EstimateLines.AsNoTracking().Where(l => ids.Contains(l.EstimateId)).ToListAsync(ct)).ToLookup(l => l.EstimateId);
        return Results.Ok(estimates.Select(e => ToEstimateDto(e, lines[e.Id].ToList())));
    }

    private static async Task<IResult> SaveCostsAsync(
        Guid id, SaveCosts req, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var x = await LoadAsync(id, user, db, memberships, ct);
        // The technician records what they used while working; a supervisor can correct it while it awaits verification.
        if (x is null || !(x.IsAssignee || x.Actor.Has(Permissions.ComplaintAssign))) return Results.NotFound();
        var open = x.IsAssignee ? x.Job.Status == JobStatus.InProgress : x.Job.Status is JobStatus.InProgress or JobStatus.PendingVerification;
        if (!open) return Results.Conflict(new { error = "costs_locked", status = x.Job.Status });

        var (priced, error) = await PriceAsync(req.Lines, db, ct);
        if (error is not null) return error;

        db.JobCosts.RemoveRange(await db.JobCosts.Where(c => c.JobId == id).ToListAsync(ct));
        var lines = priced!.Select(p => Fill(new JobCost { JobId = id, RecordedBy = x.Actor.UserId }, x.Job.OrganizationId, p)).ToList();
        db.JobCosts.AddRange(lines);
        Log(db, x, "costs", note: $"total {Pricing.Total(lines):0.00}");

        // Over the estimate by more than the society allows: flag it once for a supervisor or manager to look at (JC-05).
        var estimate = await CurrentEstimateAsync(db, id, ct);
        var actual = Pricing.Total(lines);
        var threshold = await OverrunThresholdAsync(db, ct);
        var overrun = threshold > 0 && ApprovalRules.IsOverrun(estimate?.Total, actual, threshold);
        if (overrun && x.Job.OverrunFlaggedAt is null)
        {
            x.Job.OverrunFlaggedAt = DateTimeOffset.UtcNow;
            bus.Publish(new CostOverrun(x.Job.OrganizationId, x.Job.Id, x.Job.Number, x.Complaint.Reference, estimate!.Total, actual, ApprovalRules.OverrunPercent(estimate.Total, actual) ?? 0));
        }
        else if (!overrun && x.Job.OverrunFlaggedAt is not null) x.Job.OverrunFlaggedAt = null; // corrected back under the limit
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CostsResult(lines.Select(LineDto).ToList(), Variance(estimate?.Total, actual, threshold)));
    }

    /// <summary>Jobs whose actual cost went over the estimate by more than the society's threshold (JC-05).</summary>
    private static async Task<IResult> OverrunsAsync(AppDbContext db, CancellationToken ct) => Results.Ok(await OverrunListAsync(db, ct));

    /// <summary>Jobs over their estimate by more than the society's threshold; also behind the dashboard widget (DB-07).</summary>
    internal static async Task<List<OverrunDto>> OverrunListAsync(AppDbContext db, CancellationToken ct)
    {
        var threshold = await OverrunThresholdAsync(db, ct);
        if (threshold <= 0) return [];
        var rows = await (from e in db.Estimates.AsNoTracking()
                          join j in db.Jobs.AsNoTracking() on e.JobId equals j.Id
                          join c in db.Complaints.AsNoTracking() on j.ComplaintId equals c.Id
                          where e.Status == "Current"
                          select new { Job = j, c.Reference, Estimated = e.Total }).ToListAsync(ct);
        var ids = rows.Select(r => r.Job.Id).ToList();
        var actuals = (await db.JobCosts.AsNoTracking().Where(c => ids.Contains(c.JobId)).Select(c => new { c.JobId, c.Amount }).ToListAsync(ct))
            .GroupBy(c => c.JobId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var flagged = rows.Select(r => (r, Actual: actuals.GetValueOrDefault(r.Job.Id)))
            .Where(x => ApprovalRules.IsOverrun(x.r.Estimated, x.Actual, threshold))
            .Select(x => new OverrunDto(x.r.Job.Id, x.r.Job.Number, x.r.Reference, x.r.Job.Status, x.r.Estimated, x.Actual, x.Actual - x.r.Estimated,
                ApprovalRules.OverrunPercent(x.r.Estimated, x.Actual) ?? 0, x.r.Job.OverrunFlaggedAt))
            .OrderByDescending(o => o.Percent).ToList();
        return flagged;
    }

    /// <summary>Marks a job's cost as chargeable to a house, or clears it (JB-10). Supervisors and managers only; the audit log keeps who changed it.</summary>
    private static async Task<IResult> ChargeableAsync(Guid id, SetChargeable req, AppDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs.SingleOrDefaultAsync(j => j.Id == id, ct);
        if (job is null) return Results.NotFound();
        if (req.Chargeable)
        {
            if (req.HouseId is not { } house) return Results.BadRequest(new { error = "house_required" });
            if (!await db.Houses.AnyAsync(h => h.Id == house, ct)) return Results.BadRequest(new { error = "house_not_found" });
            job.Chargeable = true; job.ChargeHouseId = house;
        }
        else
        {
            job.Chargeable = false; job.ChargeHouseId = null;
            foreach (var pending in await db.JobCharges.Where(c => c.JobId == id && c.Status == ChargeStatus.Pending).ToListAsync(ct)) pending.Status = ChargeStatus.Cancelled;
        }
        // Marked after the work was finished: it still goes to accounts to review.
        await ChargeQueue.EnsureAsync(db, job, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(job));
    }

    private static Task<Estimate?> CurrentEstimateAsync(AppDbContext db, Guid jobId, CancellationToken ct) =>
        db.Estimates.AsNoTracking().SingleOrDefaultAsync(e => e.JobId == jobId && e.Status == "Current", ct);

    private static async Task<EstimateDto> ToEstimateDtoAsync(AppDbContext db, Estimate e, CancellationToken ct) =>
        ToEstimateDto(e, await db.EstimateLines.AsNoTracking().Where(l => l.EstimateId == e.Id).ToListAsync(ct));

    private static EstimateDto ToEstimateDto(Estimate e, IReadOnlyList<EstimateLine> lines) =>
        new(e.Id, e.Version, e.Status, e.Note, e.Total, e.CreatedBy, e.CreatedAt, lines.Select(LineDto).ToList(), e.ApprovalStatus, e.ApprovalId);

    private static CostLineDto LineDto(CostLine l) => new(l.Id, l.Kind, l.Description, l.PriceItemId, l.Quantity, l.Unit, l.UnitRate, l.Amount);
}
