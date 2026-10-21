using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Complaint core (CP-01 to CP-07) and jobs (JB-01).</summary>
public static class ComplaintEndpoints
{
    public sealed record CreateComplaint(string Description, string? Channel, Guid? CategoryId, Guid? HouseId, Guid? LocationId, string? LocationText, Guid? ReportedByUserId);
    public sealed record CompleteLocation(Guid? HouseId, Guid? LocationId, string? LocationText);
    public sealed record Transition(ComplaintStatus To, string? Reason, Guid? MergedIntoId);
    public sealed record CreateJob(string? Instructions, Guid? AssignedTeamId);
    public sealed record StartUpload(string MimeType, long TotalBytes);

    public sealed record CreatedDto(Guid Id, string Reference, ComplaintStatus Status, bool NeedsLocation);
    public sealed record ComplaintDto(
        Guid Id, string Reference, ComplaintStatus Status, string Channel, string Description, Guid? CategoryId, string Priority,
        Guid? HouseId, Guid? LocationId, string? LocationText, string? Language, string AiStatus, string? AiCategoryKey, string? AiSummary,
        float? AiConfidence, string? HoldReason, string? StatusReason, Guid? ReportedByUserId, Guid? EnteredByUserId, Guid? MergedIntoId,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    public sealed record HistoryDto(ComplaintStatus? From, ComplaintStatus To, Guid? ActorUserId, string? Reason, DateTimeOffset OccurredAt);
    public sealed record MediaDto(Guid Id, string Kind, string MimeType, long SizeBytes, int? Width, int? Height, string[] QualityFlags, string Url, string? ThumbUrl);
    public sealed record JobDto(Guid Id, string Number, int SequenceNo, string Status, string? Instructions, Guid? AssignedTeamId, DateTimeOffset CreatedAt);
    public sealed record ComplaintDetailDto(ComplaintDto Complaint, IReadOnlyList<HistoryDto> History, IReadOnlyList<MediaDto> Media, IReadOnlyList<JobDto> Jobs, IReadOnlyList<ComplaintStatus> AllowedNext);
    public sealed record UploadDto(Guid Id, long ReceivedBytes, long TotalBytes);

    private static readonly string[] ResidentChannels = ["app", "web", "whatsapp"];

    public static void MapComplaintEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Complaints");

        g.MapPost("/complaints", CreateAsync).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintCreate));
        g.MapGet("/complaints", ListAsync).RequireAuthorization(AuthPolicies.Verified);
        g.MapGet("/complaints/{id:guid}", GetAsync).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/transition", TransitionAsync).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/location", CompleteLocationAsync).RequireAuthorization(AuthPolicies.Verified);

        g.MapPost("/complaints/{id:guid}/jobs", CreateJobAsync).RequireAuthorization(AuthPolicies.For(Permissions.JobCreate));
        g.MapGet("/complaints/{id:guid}/jobs", async (Guid id, AppDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Jobs.AsNoTracking().Where(j => j.ComplaintId == id).OrderBy(j => j.SequenceNo).ToListAsync(ct)).Select(ToDto)))
            .RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));

        g.MapPost("/complaints/{id:guid}/media", AddMediaAsync).DisableAntiforgery().RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/uploads", StartUploadAsync).RequireAuthorization(AuthPolicies.Verified);
        g.MapGet("/uploads/{uploadId:guid}", async (Guid uploadId, AppDbContext db, CancellationToken ct) =>
            await db.Uploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uploadId, ct) is { } u ? Results.Ok(new UploadDto(u.Id, u.ReceivedBytes, u.TotalBytes)) : Results.NotFound())
            .RequireAuthorization(AuthPolicies.Verified);
        g.MapPut("/uploads/{uploadId:guid}", AppendChunkAsync).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/media/from-upload/{uploadId:guid}", FinishUploadAsync).RequireAuthorization(AuthPolicies.Verified);
    }

    // Access ---------------------------------------------------------------------------------------------------

    private sealed record Actor(Guid UserId, Guid OrgId, IReadOnlySet<string> Permissions)
    {
        public bool Has(string p) => Permissions.Contains(p);
    }

    private static async Task<Actor> ActorAsync(ClaimsPrincipal user, CurrentMemberships memberships, CancellationToken ct)
    {
        var perms = (await memberships.GetAsync(ct)).SelectMany(m => RolePermissions.For(m.Role)).ToHashSet();
        return new Actor(Guid.Parse(user.FindFirstValue("sub")!), Guid.Parse(user.FindFirstValue("org")!), perms);
    }

    private static bool CanSee(Actor a, Complaint c) => a.Has(Permissions.ComplaintRead) || c.ReportedByUserId == a.UserId;
    private static bool CanContribute(Actor a, Complaint c) =>
        c.ReportedByUserId == a.UserId || a.Has(Permissions.ComplaintCreateBehalf) || a.Has(Permissions.ComplaintTriage) || a.Has(Permissions.ComplaintWork);

    // Create (CP-01, CP-03, CP-05, CP-06) ----------------------------------------------------------------------

    private static async Task<IResult> CreateAsync(
        CreateComplaint req, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships,
        NumberGenerator numbers, IEventBus bus, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        if (string.IsNullOrWhiteSpace(req.Description) || req.Description.Length > 4000) return Results.BadRequest(new { error = "invalid_description" });

        var onBehalf = actor.Has(Permissions.ComplaintCreateBehalf);
        var channel = req.Channel ?? (onBehalf ? "phone" : "app");
        if (!Channels.All.Contains(channel)) return Results.BadRequest(new { error = "invalid_channel" });
        // Staff-entry channels (phone, walk-in) belong to office staff; a resident cannot pose as a phone call.
        var channelAllowed = onBehalf ? Channels.IsStaffEntry(channel) : ResidentChannels.Contains(channel);
        if (!channelAllowed) return Results.BadRequest(new { error = "channel_not_allowed" });

        Guid? reporter = actor.UserId;
        Guid? enteredBy = null;
        if (Channels.IsStaffEntry(channel))
        {
            enteredBy = actor.UserId;
            reporter = req.ReportedByUserId;
            if (reporter is { } r && !await db.Users.AnyAsync(u => u.Id == r, ct)) return Results.BadRequest(new { error = "reporter_not_found" });
        }

        Category? category = null;
        if (req.CategoryId is { } cid)
        {
            category = await db.Categories.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cid && c.IsActive, ct);
            if (category is null) return Results.BadRequest(new { error = "category_not_found" });
        }

        var houseId = req.HouseId;
        var locationId = req.LocationId;
        if (houseId is { } hid)
        {
            var house = await db.Houses.AsNoTracking().SingleOrDefaultAsync(h => h.Id == hid, ct);
            if (house is null) return Results.BadRequest(new { error = "house_not_found" });
            locationId ??= house.LocationId;
        }
        if (locationId is { } lid && !await db.Locations.AnyAsync(l => l.Id == lid, ct)) return Results.BadRequest(new { error = "location_not_found" });

        // A resident linked to exactly one current house does not have to say where they live.
        if (houseId is null && locationId is null && reporter is { } who)
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var links = await db.ResidentLinks.AsNoTracking().Where(l => l.UserId == who && (l.ToDate == null || l.ToDate >= today)).Select(l => l.HouseId).Distinct().ToListAsync(ct);
            if (links.Count == 1)
            {
                houseId = links[0];
                locationId = await db.Houses.AsNoTracking().Where(h => h.Id == houseId).Select(h => (Guid?)h.LocationId).SingleOrDefaultAsync(ct);
            }
        }

        var complaint = new Complaint
        {
            OrganizationId = actor.OrgId,
            Reference = await numbers.NextAsync(NumberGenerator.ComplaintScope, "UF", ct),
            Channel = channel,
            Description = req.Description.Trim(),
            CategoryId = category?.Id,
            Priority = category?.DefaultPriority ?? "Normal",
            HouseId = houseId,
            LocationId = locationId,
            LocationText = string.IsNullOrWhiteSpace(req.LocationText) ? null : req.LocationText.Trim(),
            ReportedByUserId = reporter,
            EnteredByUserId = enteredBy,
        };

        // CP-05: without a house or map location the complaint is held and the resident is asked for it.
        var needsLocation = complaint.HouseId is null && complaint.LocationId is null;
        if (needsLocation)
        {
            complaint.Status = ComplaintStatus.OnHold;
            complaint.HoldReason = "location_missing";
        }

        db.Complaints.Add(complaint);
        db.ComplaintHistory.Add(new ComplaintStatusHistory
        {
            OrganizationId = actor.OrgId, ComplaintId = complaint.Id, ToStatus = complaint.Status, ActorUserId = actor.UserId,
            Reason = needsLocation ? "Location missing: street number or map pin requested" : null,
        });
        bus.Publish(new ComplaintCreated(actor.OrgId, complaint.Id, complaint.Reference));
        await db.SaveChangesAsync(ct);

        return Results.Created($"/complaints/{complaint.Id}", new CreatedDto(complaint.Id, complaint.Reference, complaint.Status, needsLocation));
    }

    // Read -------------------------------------------------------------------------------------------------------

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, ComplaintStatus? status, int? limit, int? offset, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var q = db.Complaints.AsNoTracking().AsQueryable();
        if (!actor.Has(Permissions.ComplaintRead))
        {
            if (!actor.Has(Permissions.ComplaintReadOwn)) return Results.Forbid();
            q = q.Where(c => c.ReportedByUserId == actor.UserId);
        }
        if (status is { } s) q = q.Where(c => c.Status == s);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Skip(Math.Max(offset ?? 0, 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
        return Results.Ok(rows.Select(ToDto));
    }

    private static async Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, IObjectStorage storage, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanSee(actor, c)) return Results.NotFound();

        var history = await db.ComplaintHistory.AsNoTracking().Where(h => h.ComplaintId == id).OrderBy(h => h.OccurredAt).ToListAsync(ct);
        var media = await db.ComplaintMedia.AsNoTracking().Where(m => m.ComplaintId == id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var jobs = actor.Has(Permissions.ComplaintRead) ? await db.Jobs.AsNoTracking().Where(j => j.ComplaintId == id).OrderBy(j => j.SequenceNo).ToListAsync(ct) : [];

        var isReporter = c.ReportedByUserId == actor.UserId;
        var next = ComplaintStateMachine.NextStatuses(c.Status).Where(to => ComplaintStateMachine.Check(c.Status, to, actor.Permissions, isReporter, "x") is null).ToList();

        return Results.Ok(new ComplaintDetailDto(
            ToDto(c),
            history.Select(h => new HistoryDto(h.FromStatus, h.ToStatus, h.ActorUserId, h.Reason, h.OccurredAt)).ToList(),
            media.Select(m => new MediaDto(m.Id, m.Kind, m.MimeType, m.SizeBytes, m.Width, m.Height, m.QualityFlags,
                storage.CreateDownloadUrl(m.StorageKey), m.ThumbKey is null ? null : storage.CreateDownloadUrl(m.ThumbKey))).ToList(),
            jobs.Select(ToDto).ToList(), next));
    }

    // Transitions (CP-02, CP-07) -----------------------------------------------------------------------------------

    private static async Task<IResult> TransitionAsync(
        Guid id, Transition req, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanSee(actor, c)) return Results.NotFound();

        var error = ComplaintStateMachine.Check(c.Status, req.To, actor.Permissions, c.ReportedByUserId == actor.UserId, req.Reason);
        if (error is not null)
        {
            return error switch
            {
                "illegal_transition" => Results.Conflict(new { error, from = c.Status, to = req.To }),
                "forbidden" => Results.Json(new { error }, statusCode: 403),
                _ => Results.BadRequest(new { error }),
            };
        }

        if (req.To == ComplaintStatus.Merged)
        {
            if (req.MergedIntoId is not { } target || target == c.Id) return Results.BadRequest(new { error = "merge_target_required" });
            var t = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == target, ct);
            if (t is null || t.Status is ComplaintStatus.Merged or ComplaintStatus.Rejected) return Results.BadRequest(new { error = "invalid_merge_target" });
            c.MergedIntoId = target;
        }

        var from = c.Status;
        c.Status = req.To;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        c.StatusReason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        c.HoldReason = req.To == ComplaintStatus.OnHold ? c.StatusReason : null;
        if (req.To == ComplaintStatus.Resolved) c.ResolvedAt = DateTimeOffset.UtcNow;
        if (req.To == ComplaintStatus.Closed) c.ClosedAt = DateTimeOffset.UtcNow;
        if (from is ComplaintStatus.Closed or ComplaintStatus.Rejected) { c.ClosedAt = null; c.ResolvedAt = null; }

        // Jobs follow the complaint: starting work starts the job, finishing completes it, handing back cancels it.
        var openJobs = await db.Jobs.Where(j => j.ComplaintId == c.Id && (j.Status == "Open" || j.Status == "InProgress")).ToListAsync(ct);
        foreach (var job in openJobs)
        {
            job.Status = req.To switch
            {
                ComplaintStatus.InProgress => "InProgress",
                ComplaintStatus.Resolved => "Completed",
                ComplaintStatus.UnderReview or ComplaintStatus.Rejected or ComplaintStatus.Merged => "Cancelled",
                _ => job.Status,
            };
        }

        Record(db, bus, c, from, actor.UserId, c.StatusReason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(c));
    }

    private static void Record(AppDbContext db, IEventBus bus, Complaint c, ComplaintStatus from, Guid? actor, string? reason)
    {
        db.ComplaintHistory.Add(new ComplaintStatusHistory
        {
            OrganizationId = c.OrganizationId, ComplaintId = c.Id, FromStatus = from, ToStatus = c.Status, ActorUserId = actor, Reason = reason,
        });
        bus.Publish(new ComplaintStatusChanged(c.OrganizationId, c.Id, c.Reference, from, c.Status, actor, reason));
    }

    // Missing location (CP-05) -------------------------------------------------------------------------------------

    private static async Task<IResult> CompleteLocationAsync(
        Guid id, CompleteLocation req, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, IEventBus bus, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanSee(actor, c) || !CanContribute(actor, c)) return Results.NotFound();
        if (req.HouseId is null && req.LocationId is null) return Results.BadRequest(new { error = "location_required" });

        if (req.HouseId is { } hid)
        {
            var house = await db.Houses.AsNoTracking().SingleOrDefaultAsync(h => h.Id == hid, ct);
            if (house is null) return Results.BadRequest(new { error = "house_not_found" });
            c.HouseId = hid;
            c.LocationId = req.LocationId ?? house.LocationId;
        }
        else
        {
            if (!await db.Locations.AnyAsync(l => l.Id == req.LocationId, ct)) return Results.BadRequest(new { error = "location_not_found" });
            c.LocationId = req.LocationId;
        }
        if (!string.IsNullOrWhiteSpace(req.LocationText)) c.LocationText = req.LocationText.Trim();
        c.UpdatedAt = DateTimeOffset.UtcNow;

        if (c.Status == ComplaintStatus.OnHold && c.HoldReason == "location_missing")
        {
            c.Status = ComplaintStatus.New;
            c.HoldReason = null;
            Record(db, bus, c, ComplaintStatus.OnHold, actor.UserId, "Location provided");
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(c));
    }

    // Jobs (JB-01) --------------------------------------------------------------------------------------------------

    private static async Task<IResult> CreateJobAsync(
        Guid id, CreateJob req, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships,
        NumberGenerator numbers, IEventBus bus, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Results.NotFound();
        if (c.Status != ComplaintStatus.UnderReview) return Results.Conflict(new { error = "complaint_not_under_review", status = c.Status });
        if (await db.Jobs.AnyAsync(j => j.ComplaintId == id && (j.Status == "Open" || j.Status == "InProgress"), ct)) return Results.Conflict(new { error = "open_job_exists" });
        if (req.AssignedTeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team && t.IsActive, ct)) return Results.BadRequest(new { error = "team_not_found" });

        // Rework: a further job under the same complaint, numbered separately, sequence 2, 3, ...
        var sequence = (await db.Jobs.Where(j => j.ComplaintId == id).MaxAsync(j => (int?)j.SequenceNo, ct) ?? 0) + 1;
        var job = new Job
        {
            OrganizationId = actor.OrgId, Number = await numbers.NextAsync(NumberGenerator.JobScope, "J", ct), ComplaintId = id,
            SequenceNo = sequence, Instructions = req.Instructions?.Trim(), AssignedTeamId = req.AssignedTeamId, CreatedBy = actor.UserId,
        };
        db.Jobs.Add(job);

        var from = c.Status;
        c.Status = ComplaintStatus.Assigned;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        c.StatusReason = null;
        Record(db, bus, c, from, actor.UserId, $"Job {job.Number} created");
        bus.Publish(new JobCreated(actor.OrgId, job.Id, id, job.Number));
        await db.SaveChangesAsync(ct);
        return Results.Created($"/complaints/{id}/jobs", ToDto(job));
    }

    // Media (CP-04) ---------------------------------------------------------------------------------------------------

    private static async Task<IResult> AddMediaAsync(
        Guid id, [FromForm] IFormFile file, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships,
        IMediaProcessor processor, IObjectStorage storage, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanSee(actor, c) || !CanContribute(actor, c)) return Results.NotFound();
        if (file.Length > FileSniffer.MaxBytes) return Results.Json(new { error = "file_too_large" }, statusCode: 413);

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await SaveMediaAsync(ms.ToArray(), c, actor, db, processor, storage, ct);
    }

    private static async Task<IResult> SaveMediaAsync(byte[] bytes, Complaint c, Actor actor, AppDbContext db, IMediaProcessor processor, IObjectStorage storage, CancellationToken ct)
    {
        var (media, error) = await processor.ProcessAsync(bytes, actor.OrgId, c.Id, actor.UserId,
            async sha => await db.ComplaintMedia.AsNoTracking().SingleOrDefaultAsync(m => m.ComplaintId == c.Id && m.Sha256 == sha, ct), ct);

        if (error == "duplicate") return Results.Ok(ToMediaDto(media!, storage));
        if (error is not null)
            return error == "file_too_large" ? Results.Json(new { error }, statusCode: 413)
                 : error == "unsupported_file_type" ? Results.Json(new { error }, statusCode: 415)
                 : Results.BadRequest(new { error });

        db.ComplaintMedia.Add(media!);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/complaints/{c.Id}", ToMediaDto(media!, storage));
    }

    private static async Task<IResult> StartUploadAsync(StartUpload req, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        if (req.TotalBytes <= 0) return Results.BadRequest(new { error = "empty_file" });
        if (req.TotalBytes > FileSniffer.MaxBytes) return Results.Json(new { error = "file_too_large" }, statusCode: 413);
        var u = new UploadSession { OrganizationId = actor.OrgId, UserId = actor.UserId, MimeType = req.MimeType, TotalBytes = req.TotalBytes };
        db.Uploads.Add(u);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/uploads/{u.Id}", new UploadDto(u.Id, 0, u.TotalBytes));
    }

    /// <summary>The client sends the next chunk at the offset the server reports; a retried chunk at an old offset is ignored, so retries never corrupt the file.</summary>
    private static async Task<IResult> AppendChunkAsync(
        Guid uploadId, long offset, HttpRequest request, ClaimsPrincipal principal, AppDbContext db, IObjectStorage storage, CancellationToken ct)
    {
        var userId = Guid.Parse(principal.FindFirstValue("sub")!);
        var u = await db.Uploads.SingleOrDefaultAsync(x => x.Id == uploadId && x.UserId == userId, ct);
        if (u is null) return Results.NotFound();
        if (u.CompletedAt is not null) return Results.Conflict(new { error = "upload_completed" });
        if (offset < u.ReceivedBytes) return Results.Ok(new UploadDto(u.Id, u.ReceivedBytes, u.TotalBytes)); // duplicate retry
        if (offset > u.ReceivedBytes) return Results.Conflict(new { error = "offset_mismatch", receivedBytes = u.ReceivedBytes });

        await using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) return Results.BadRequest(new { error = "empty_chunk" });
        if (u.ReceivedBytes + buffer.Length > u.TotalBytes) return Results.BadRequest(new { error = "chunk_exceeds_total" });

        buffer.Position = 0;
        var key = await storage.PutTempAsync($"{u.Id:N}/{u.ChunkKeys.Length:D5}", buffer, ct);
        u.ChunkKeys = [.. u.ChunkKeys, key];
        u.ReceivedBytes += buffer.Length;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new UploadDto(u.Id, u.ReceivedBytes, u.TotalBytes));
    }

    private static async Task<IResult> FinishUploadAsync(
        Guid id, Guid uploadId, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships,
        IMediaProcessor processor, IObjectStorage storage, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanSee(actor, c) || !CanContribute(actor, c)) return Results.NotFound();
        var u = await db.Uploads.SingleOrDefaultAsync(x => x.Id == uploadId && x.UserId == actor.UserId, ct);
        if (u is null) return Results.NotFound();
        if (u.ReceivedBytes != u.TotalBytes) return Results.Conflict(new { error = "upload_incomplete", receivedBytes = u.ReceivedBytes, totalBytes = u.TotalBytes });

        using var all = new MemoryStream();
        foreach (var key in u.ChunkKeys)
        {
            await using var part = await storage.OpenReadAsync(key, ct);
            await part.CopyToAsync(all, ct);
        }
        var result = await SaveMediaAsync(all.ToArray(), c, actor, db, processor, storage, ct);

        u.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var key in u.ChunkKeys) await storage.DeleteAsync(key, ct);
        return result;
    }

    // Mapping ---------------------------------------------------------------------------------------------------------

    private static ComplaintDto ToDto(Complaint c) => new(
        c.Id, c.Reference, c.Status, c.Channel, c.Description, c.CategoryId, c.Priority, c.HouseId, c.LocationId, c.LocationText, c.Language,
        c.AiStatus, c.AiCategoryKey, c.AiSummary, c.AiConfidence, c.HoldReason, c.StatusReason, c.ReportedByUserId, c.EnteredByUserId, c.MergedIntoId,
        c.CreatedAt, c.UpdatedAt);

    private static JobDto ToDto(Job j) => new(j.Id, j.Number, j.SequenceNo, j.Status, j.Instructions, j.AssignedTeamId, j.CreatedAt);

    private static MediaDto ToMediaDto(ComplaintMedia m, IObjectStorage storage) => new(
        m.Id, m.Kind, m.MimeType, m.SizeBytes, m.Width, m.Height, m.QualityFlags,
        storage.CreateDownloadUrl(m.StorageKey), m.ThumbKey is null ? null : storage.CreateDownloadUrl(m.ThumbKey));
}
