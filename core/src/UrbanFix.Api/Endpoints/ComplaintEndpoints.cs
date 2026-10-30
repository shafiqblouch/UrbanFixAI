using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UrbanFix.Api.Security;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;

namespace UrbanFix.Api.Endpoints;

/// <summary>Complaint core (CP-01 to CP-07) and jobs (JB-01).</summary>
public static class ComplaintEndpoints
{
    public sealed record CreateComplaint(string Description, string? Channel, Guid? CategoryId, Guid? HouseId, Guid? LocationId, string? LocationText, Guid? ReportedByUserId, string? ReporterPhone = null, string? AssetRef = null, bool MediaOnly = false, double? Latitude = null, double? Longitude = null);
    public sealed record CompleteLocation(Guid? HouseId, Guid? LocationId, string? LocationText);
    public sealed record Transition(ComplaintStatus To, string? Reason, Guid? MergedIntoId);
    public sealed record CreateJob(string? Instructions, Guid? AssignedTeamId);
    public sealed record StartUpload(string MimeType, long TotalBytes);

    public sealed record CreatedDto(Guid Id, string Reference, ComplaintStatus Status, bool NeedsLocation);
    public sealed record ComplaintDto(
        Guid Id, string Reference, ComplaintStatus Status, string Channel, string Description, Guid? CategoryId, string Priority,
        Guid? HouseId, Guid? LocationId, string? LocationText, string? Language, string AiStatus, string? AiCategoryKey, string? AiSummary,
        float? AiConfidence, string? HoldReason, string? StatusReason, Guid? ReportedByUserId, Guid? EnteredByUserId, Guid? MergedIntoId,
        DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool SafetyFlag = false, DateTimeOffset? DueAt = null, DateTimeOffset? RespondBy = null, bool AutoRouted = false,
        string? ReporterPhone = null, bool ReporterVerified = true, string? AssetRef = null, bool AutoConfirmed = false, string? AiModel = null, Guid? IncidentId = null, double? Latitude = null, double? Longitude = null);
    public sealed record VerifyReporter(Guid? UserId);
    public sealed record SaveFeedback(int Rating, bool ConfirmedFixed, string? Comment);
    public sealed record FeedbackDto(int Rating, bool ConfirmedFixed, string? Comment, DateTimeOffset UpdatedAt);
    public sealed record TranscriptDto(string Transcript, float Confidence, string Language, bool LowConfidence);
    public sealed record SimilarDto(Guid ComplaintId, string Reference, double Similarity);
    public sealed record HistoryDto(ComplaintStatus? From, ComplaintStatus To, Guid? ActorUserId, string? Reason, DateTimeOffset OccurredAt);
    public sealed record MediaDto(Guid Id, string Kind, string MimeType, long SizeBytes, int? Width, int? Height, string[] QualityFlags, string Url, string? ThumbUrl,
        string? Transcript = null, float? TranscriptConfidence = null, string? TranscriptLanguage = null, string? TranscriptStatus = null,
        string[]? AiLabels = null, string? AiDescription = null, bool? AiNeedsMoreInfo = null);
    public sealed record JobDto(
        Guid Id, string Number, int SequenceNo, string Status, string? Instructions, Guid? AssignedTeamId, DateTimeOffset CreatedAt,
        Guid ComplaintId, Guid? AssignedUserId, string EvidenceRule, bool RequiresCosts, bool Chargeable = false, Guid? ChargeHouseId = null);
    public sealed record ComplaintDetailDto(ComplaintDto Complaint, IReadOnlyList<HistoryDto> History, IReadOnlyList<MediaDto> Media, IReadOnlyList<JobDto> Jobs, IReadOnlyList<ComplaintStatus> AllowedNext, FeedbackDto? Feedback = null);
    public sealed record UploadDto(Guid Id, long ReceivedBytes, long TotalBytes);

    private static readonly string[] ResidentChannels = ["app", "web", "whatsapp"];

    public static void MapComplaintEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Complaints");

        g.MapPost("/complaints", CreateAsync).Produces<CreatedDto>(201).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintCreate));
        g.MapGet("/complaints", ListAsync).Produces<List<ComplaintDto>>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapGet("/complaints/{id:guid}", GetAsync).Produces<ComplaintDetailDto>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/feedback", FeedbackAsync).Produces<FeedbackDto>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/voice/transcribe", TranscribeAsync).DisableAntiforgery().Produces<TranscriptDto>(200)
            .RequireAuthorization(AuthPolicies.For(Permissions.ComplaintWork));
        g.MapGet("/complaints/{id:guid}/similar", SimilarAsync).Produces<List<SimilarDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));
        g.MapGet("/complaints/by-reference/{reference}", ByReferenceAsync).Produces<ComplaintDto>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/verify-reporter", VerifyReporterAsync).Produces<ComplaintDto>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintTriage));
        g.MapGet("/organization/policies", async (AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
            Results.Ok(OrgPolicy.From((await SetupEndpoints.GetOrCreateSettings(db, tenant.OrganizationId!.Value, ct)).Settings)))
            .Produces<OrgPolicy>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintAssign));
        g.MapPut("/organization/policies", async (OrgPolicy req, AppDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            if (req.Validate() is { } bad) return Results.BadRequest(new { error = bad });
            var settings = await SetupEndpoints.GetOrCreateSettings(db, tenant.OrganizationId!.Value, ct);
            settings.Settings = req.WriteInto(settings.Settings);
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(req);
        }).Produces<OrgPolicy>(200).RequireAuthorization(AuthPolicies.For(Permissions.OrganizationSettings));
        g.MapPost("/complaints/{id:guid}/transition", TransitionAsync).Produces<ComplaintDto>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/location", CompleteLocationAsync).Produces<ComplaintDto>(200).RequireAuthorization(AuthPolicies.Verified);

        g.MapPost("/complaints/{id:guid}/jobs", CreateJobAsync).Produces<JobDto>(201).RequireAuthorization(AuthPolicies.For(Permissions.JobCreate));
        g.MapGet("/complaints/{id:guid}/jobs", async (Guid id, ClaimsPrincipal user, AppDbContext db, CurrentMemberships memberships, CancellationToken ct) =>
            {
                var actor = await ActorAsync(user, memberships, ct);
                var jobs = await db.Jobs.AsNoTracking().Where(j => j.ComplaintId == id).OrderBy(j => j.SequenceNo).ToListAsync(ct);
                return Results.Ok(jobs.Where(actor.CanSeeJob).Select(ToDto));
            })
            .Produces<List<JobDto>>(200).RequireAuthorization(AuthPolicies.For(Permissions.ComplaintRead));

        g.MapPost("/complaints/{id:guid}/media", AddMediaAsync).DisableAntiforgery().Produces<MediaDto>(201).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/uploads", StartUploadAsync).Produces<UploadDto>(201).RequireAuthorization(AuthPolicies.Verified);
        g.MapGet("/uploads/{uploadId:guid}", async (Guid uploadId, AppDbContext db, CancellationToken ct) =>
            await db.Uploads.AsNoTracking().SingleOrDefaultAsync(u => u.Id == uploadId, ct) is { } u ? Results.Ok(new UploadDto(u.Id, u.ReceivedBytes, u.TotalBytes)) : Results.NotFound())
            .Produces<UploadDto>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapPut("/uploads/{uploadId:guid}", AppendChunkAsync).Produces<UploadDto>(200).RequireAuthorization(AuthPolicies.Verified);
        g.MapPost("/complaints/{id:guid}/media/from-upload/{uploadId:guid}", FinishUploadAsync).Produces<MediaDto>(201).RequireAuthorization(AuthPolicies.Verified);
    }

    // Access ---------------------------------------------------------------------------------------------------

    internal sealed record Actor(Guid UserId, Guid OrgId, IReadOnlySet<string> Permissions)
    {
        public bool Has(string p) => Permissions.Contains(p);

        /// <summary>Supervisors, managers and office staff see every job; a technician sees only the ones assigned to them (JB-02).</summary>
        public bool SeesAllJobs => Has(SharedKernel.Permissions.ComplaintRead) && (!Has(SharedKernel.Permissions.ComplaintWork) || Has(SharedKernel.Permissions.ComplaintAssign));
        public bool CanSeeJob(Job j) => SeesAllJobs || j.AssignedUserId == UserId;
    }

    internal static async Task<Actor> ActorAsync(ClaimsPrincipal user, CurrentMemberships memberships, CancellationToken ct)
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
        ComplaintIntake intake, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        // A photo- or voice-only complaint starts with the placeholder text; the photo or transcription job writes the real description once it has looked at the picture or listened.
        var description = req.MediaOnly && string.IsNullOrWhiteSpace(req.Description) ? IntakeText.MediaOnly : req.Description;
        if (string.IsNullOrWhiteSpace(description) || description.Length > 4000) return Results.BadRequest(new { error = "invalid_description" });

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

        if ((req.Latitude is null) != (req.Longitude is null) || req.Latitude is < -90 or > 90 || req.Longitude is < -180 or > 180)
            return Results.BadRequest(new { error = "invalid_coordinates" });
        var (lat, lon) = (req.Latitude, req.Longitude);

        var assetRef = string.IsNullOrWhiteSpace(req.AssetRef) ? null : req.AssetRef.Trim();
        if (assetRef is { Length: > 100 }) return Results.BadRequest(new { error = "invalid_asset_ref" });

        // CP-08: a phone-in complaint from a number we do not know follows the organization's policy.
        string? reporterPhone = null;
        var reporterVerified = true;
        var holdForSender = false;
        if (!string.IsNullOrWhiteSpace(req.ReporterPhone))
        {
            if (!Channels.IsStaffEntry(channel)) return Results.BadRequest(new { error = "reporter_phone_not_allowed" });
            reporterPhone = PhoneNumber.Normalize(req.ReporterPhone);
            if (reporterPhone is null) return Results.BadRequest(new { error = "invalid_phone" });
            if (reporter is null)
            {
                var policy = OrgPolicy.From((await SetupEndpoints.GetOrCreateSettings(db, actor.OrgId, ct)).Settings);
                var decision = await SenderResolver.ResolveAsync(db, reporterPhone, policy, ct);
                switch (decision.Kind)
                {
                    case SenderKind.Matched: reporter = decision.UserId; break;
                    case SenderKind.RegistrationRequired: return Results.Conflict(new { error = "registration_required", phone = reporterPhone });
                    case SenderKind.Guest: reporterVerified = false; break;
                    default: reporterVerified = false; holdForSender = true; break;
                }
            }
        }

        var (made, error) = await intake.CreateAsync(new IntakeRequest(
            actor.OrgId, channel, description, req.CategoryId, req.HouseId, req.LocationId, req.LocationText, reporter, enteredBy,
            reporterPhone, reporterVerified, holdForSender, assetRef, actor.UserId, lat, lon), ct);
        if (error is not null) return Results.BadRequest(new { error });

        var complaint = made!.Complaint;
        return Results.Created($"/complaints/{complaint.Id}", new CreatedDto(complaint.Id, complaint.Reference, complaint.Status, made.NeedsLocation));
    }

    // Read -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Filtered, paged list (WP-02). The total number of matches comes back in <c>X-Total-Count</c>.
    /// <c>locationId</c> matches that location and everything beneath it (a sector, a street...).
    /// </summary>
    /// <summary>
    /// The complaints a person may see, narrowed by the list filters. Shared by the list and the exports, so an export can never show
    /// more than the screen does (DB-04).
    /// </summary>
    internal static async Task<(IQueryable<Complaint>? Query, IResult? Error)> FilteredAsync(
        Actor actor, AppDbContext db, ComplaintStatus? status, string? q, Guid? categoryId, Guid? departmentId, Guid? locationId, string? priority,
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        var query = db.Complaints.AsNoTracking().AsQueryable();
        if (!actor.Has(Permissions.ComplaintRead))
        {
            if (!actor.Has(Permissions.ComplaintReadOwn)) return (null, Results.Forbid());
            query = query.Where(c => c.ReportedByUserId == actor.UserId);
        }
        if (status is { } s) query = query.Where(c => c.Status == s);
        if (categoryId is { } cat) query = query.Where(c => c.CategoryId == cat);
        if (priority is not null) query = query.Where(c => c.Priority == priority);
        if (from is { } f) query = query.Where(c => c.CreatedAt >= f);
        if (to is { } t) query = query.Where(c => c.CreatedAt <= t);
        if (departmentId is { } dep) query = query.Where(c => db.Categories.Any(k => k.Id == c.CategoryId && k.DepartmentId == dep));
        if (locationId is { } loc)
        {
            var path = await db.Locations.AsNoTracking().Where(l => l.Id == loc).Select(l => l.Path).SingleOrDefaultAsync(ct);
            if (path is null) return (null, Results.BadRequest(new { error = "location_not_found" }));
            query = query.Where(c => db.Locations.Any(l => l.Id == c.LocationId && l.Path.StartsWith(path)));
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.ILike(c.Reference, pattern, "\\") || EF.Functions.ILike(c.Description, pattern, "\\") || (c.LocationText != null && EF.Functions.ILike(c.LocationText, pattern, "\\")) || (c.AssetRef != null && EF.Functions.ILike(c.AssetRef, pattern, "\\")));
        }
        return (query, null);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, ComplaintStatus? status, string? q,
        Guid? categoryId, Guid? departmentId, Guid? locationId, string? priority, DateTimeOffset? from, DateTimeOffset? to,
        int? limit, int? offset, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var (query, error) = await FilteredAsync(actor, db, status, q, categoryId, departmentId, locationId, priority, from, to, ct);
        if (error is not null) return error;
        query = query!;

        http.Response.Headers["X-Total-Count"] = (await query.CountAsync(ct)).ToString();
        var rows = await query.OrderByDescending(c => c.CreatedAt).Skip(Math.Max(offset ?? 0, 0)).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct);
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
        var jobs = actor.Has(Permissions.ComplaintRead)
            ? (await db.Jobs.AsNoTracking().Where(j => j.ComplaintId == id).OrderBy(j => j.SequenceNo).ToListAsync(ct)).Where(actor.CanSeeJob).ToList()
            : [];

        var isReporter = c.ReportedByUserId == actor.UserId;
        var next = ComplaintStateMachine.NextStatuses(c.Status).Where(to => ComplaintStateMachine.Check(c.Status, to, actor.Permissions, isReporter, "x") is null).ToList();

        return Results.Ok(new ComplaintDetailDto(
            ToDto(c),
            history.Select(h => new HistoryDto(h.FromStatus, h.ToStatus, h.ActorUserId, h.Reason, h.OccurredAt)).ToList(),
            media.Select(m => new MediaDto(m.Id, m.Kind, m.MimeType, m.SizeBytes, m.Width, m.Height, m.QualityFlags,
                storage.CreateDownloadUrl(m.StorageKey), m.ThumbKey is null ? null : storage.CreateDownloadUrl(m.ThumbKey),
                m.Transcript, m.TranscriptConfidence, m.TranscriptLanguage, m.TranscriptStatus, m.AiLabels, m.AiDescription, m.AiNeedsMoreInfo)).ToList(),
            jobs.Select(ToDto).ToList(), next,
            await db.Feedback.AsNoTracking().Where(f => f.ComplaintId == id).Select(f => new FeedbackDto(f.Rating, f.ConfirmedFixed, f.Comment, f.UpdatedAt)).SingleOrDefaultAsync(ct)));
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

        // CP-10: unless the organization says otherwise, only the resident closes a complaint that has a resident to ask.
        if (c.Status == ComplaintStatus.Resolved && req.To == ComplaintStatus.Closed && c.ReportedByUserId is { } owner && owner != actor.UserId
            && OrgPolicy.From((await SetupEndpoints.GetOrCreateSettings(db, c.OrganizationId, ct)).Settings).ClosureNeedsResident)
            return Results.Json(new { error = "resident_confirmation_required" }, statusCode: 403);

        // Work on a job moves the complaint through the job workflow, which enforces evidence and sign-off (JB-05, JB-06).
        if (req.To is ComplaintStatus.InProgress or ComplaintStatus.Resolved
            || (c.Status == ComplaintStatus.InProgress && req.To == ComplaintStatus.UnderReview))
        {
            if (await db.Jobs.AnyAsync(j => j.ComplaintId == c.Id && JobStatus.Active.Contains(j.Status), ct))
                return Results.Conflict(new { error = "use_job_workflow" });
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

        // Sending the complaint back to review, rejecting or merging it cancels whatever job was still open.
        if (req.To is ComplaintStatus.UnderReview or ComplaintStatus.Rejected or ComplaintStatus.Merged)
        {
            var activeJobs = await db.Jobs.Where(j => j.ComplaintId == c.Id && JobStatus.Active.Contains(j.Status)).ToListAsync(ct);
            foreach (var job in activeJobs)
            {
                var jobFrom = job.Status;
                job.Status = JobStatus.Cancelled;
                db.JobUpdates.Add(new JobUpdate { OrganizationId = c.OrganizationId, JobId = job.Id, AuthorId = actor.UserId, Kind = "status", FromStatus = jobFrom, ToStatus = JobStatus.Cancelled, Note = c.StatusReason });
            }
        }

        Record(db, bus, c, from, actor.UserId, c.StatusReason);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(c));
    }

    internal static void Record(AppDbContext db, IEventBus bus, Complaint c, ComplaintStatus from, Guid? actor, string? reason)
    {
        db.ComplaintHistory.Add(new ComplaintStatusHistory
        {
            OrganizationId = c.OrganizationId, ComplaintId = c.Id, FromStatus = from, ToStatus = c.Status, ActorUserId = actor, Reason = reason,
        });
        bus.Publish(new ComplaintStatusChanged(c.OrganizationId, c.Id, c.Reference, from, c.Status, actor, reason));
    }

    // Feedback (JB-08) --------------------------------------------------------------------------------------------

    /// <summary>The reporter rates finished work. One answer per complaint; sending again replaces it.</summary>
    private static async Task<IResult> FeedbackAsync(Guid id, SaveFeedback req, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var user = Guid.Parse(principal.FindFirstValue("sub")!);
        var c = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || c.ReportedByUserId != user) return Results.NotFound();
        if (c.Status is not (ComplaintStatus.Resolved or ComplaintStatus.Closed)) return Results.Conflict(new { error = "not_finished", status = c.Status });
        if (req.Rating is < 1 or > 5) return Results.BadRequest(new { error = "invalid_rating" });
        if (req.Comment is { Length: > 1000 }) return Results.BadRequest(new { error = "comment_too_long" });

        var row = await db.Feedback.SingleOrDefaultAsync(f => f.ComplaintId == id, ct);
        if (row is null)
        {
            row = new ComplaintFeedback { OrganizationId = c.OrganizationId, ComplaintId = id, UserId = user };
            db.Feedback.Add(row);
        }
        row.Rating = req.Rating; row.ConfirmedFixed = req.ConfirmedFixed;
        row.Comment = string.IsNullOrWhiteSpace(req.Comment) ? null : req.Comment.Trim();
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new FeedbackDto(row.Rating, row.ConfirmedFixed, row.Comment, row.UpdatedAt));
    }

    // Voice input for notes (TA-08) -------------------------------------------------------------------------------

    /// <summary>
    /// Turns a short recording into text the technician can correct before saving. Nothing is stored: the recording is not
    /// kept and the text is only a draft until the technician saves it as a note.
    /// </summary>
    private static async Task<IResult> TranscribeAsync([FromForm] IFormFile file, string? language, IAiClient ai, AiUsageContext usage, CancellationToken ct)
    {
        if (file.Length == 0) return Results.BadRequest(new { error = "empty_file" });
        if (file.Length > 5 * 1024 * 1024) return Results.Json(new { error = "file_too_large" }, statusCode: 413);
        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();
        var kind = FileSniffer.Identify(bytes);
        if (kind is not { Kind: "audio" }) return Results.Json(new { error = "unsupported_file_type" }, statusCode: 415);

        usage.ComplaintId = null;
        try
        {
            var r = await ai.TranscribeAsync(bytes, kind.Mime, language is "en" or "ur" or "roman_ur" ? language : null, ct);
            return Results.Ok(new TranscriptDto(r.Transcript, r.Confidence, r.Language, r.LowConfidence));
        }
        catch (AiUnavailableException off) { return Results.Json(new { error = "ai_not_available", reason = off.Reason }, statusCode: 503); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return Results.Json(new { error = "ai_unavailable" }, statusCode: 502);
        }
    }

    // Similar complaints (AI-11) ---------------------------------------------------------------------------------

    private static async Task<IResult> SimilarAsync(Guid id, int? days, int? limit, double? minSimilarity, AppDbContext db, CancellationToken ct)
    {
        if (!await db.Complaints.AnyAsync(c => c.Id == id, ct)) return Results.NotFound();
        var found = await EmbeddingStore.FindSimilarAsync(db, id, Math.Clamp(days ?? 30, 1, 365), Math.Clamp(limit ?? 5, 1, 20), Math.Clamp(minSimilarity ?? 0.8, 0, 1), ct);
        return Results.Ok(found.Select(f => new SimilarDto(f.ComplaintId, f.Reference, f.Similarity)));
    }

    // Lookup and sender checks (CP-08, CP-09) --------------------------------------------------------------------

    /// <summary>A resident finds their complaint by reference. Someone else's reference looks exactly like one that does not exist.</summary>
    private static async Task<IResult> ByReferenceAsync(string reference, ClaimsPrincipal principal, AppDbContext db, CurrentMemberships memberships, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var ref_ = reference.Trim().ToUpperInvariant();
        var c = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Reference == ref_, ct);
        return c is null || !CanSee(actor, c) ? Results.NotFound() : Results.Ok(ToDto(c));
    }

    private static async Task<IResult> VerifyReporterAsync(Guid id, VerifyReporter req, AppDbContext db, CancellationToken ct)
    {
        var c = await db.Complaints.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return Results.NotFound();
        if (req.UserId is { } uid)
        {
            if (!await db.Memberships.AnyAsync(m => m.UserId == uid && m.IsActive && m.Role == Role.Resident, ct)) return Results.BadRequest(new { error = "not_a_resident" });
            c.ReportedByUserId = uid;
        }
        c.ReporterVerified = true;
        c.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(c));
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
        if (await db.Jobs.AnyAsync(j => j.ComplaintId == id && JobStatus.Active.Contains(j.Status), ct)) return Results.Conflict(new { error = "open_job_exists" });
        if (req.AssignedTeamId is { } team && !await db.Teams.AnyAsync(t => t.Id == team && t.IsActive, ct)) return Results.BadRequest(new { error = "team_not_found" });

        // No team named: the routing rules pick one for this category and location (RT-01).
        var teamId = req.AssignedTeamId ?? await RoutingEndpoints.SuggestTeamAsync(db, c.CategoryId, c.LocationId, ct);

        // Rework: a further job under the same complaint, numbered separately, sequence 2, 3, ...
        var sequence = (await db.Jobs.Where(j => j.ComplaintId == id).MaxAsync(j => (int?)j.SequenceNo, ct) ?? 0) + 1;
        // The category's evidence rule and checklist are copied onto the job (JB-05).
        var category = c.CategoryId is { } cid ? await db.Categories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cid, ct) : null;
        var checklist = category is null ? [] : System.Text.Json.JsonSerializer.Deserialize<string[]>(category.Checklist) ?? [];
        var job = new Job
        {
            OrganizationId = actor.OrgId, Number = await numbers.NextAsync(NumberGenerator.JobScope, "J", ct), ComplaintId = id,
            SequenceNo = sequence, Instructions = req.Instructions?.Trim(), AssignedTeamId = teamId, CreatedBy = actor.UserId,
            EvidenceRule = category?.EvidenceRule ?? "photo", RequiresCosts = category?.RequiresCosts ?? false,
            Checklist = JobEvidence.ToJson(checklist.Select(label => new ChecklistItem(label, false))),
        };
        db.Jobs.Add(job);
        db.JobUpdates.Add(new JobUpdate { OrganizationId = actor.OrgId, JobId = job.Id, AuthorId = actor.UserId, Kind = "created", ToStatus = JobStatus.Open, Note = job.Instructions });

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
        IMediaProcessor processor, IObjectStorage storage, IEventBus bus, CancellationToken ct)
    {
        var actor = await ActorAsync(principal, memberships, ct);
        var c = await db.Complaints.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || !CanSee(actor, c) || !CanContribute(actor, c)) return Results.NotFound();
        if (file.Length > FileSniffer.MaxBytes) return Results.Json(new { error = "file_too_large" }, statusCode: 413);

        await using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return await SaveMediaAsync(ms.ToArray(), c, actor, db, processor, storage, ct, bus: bus);
    }

    internal static async Task<IResult> SaveMediaAsync(
        byte[] bytes, Complaint c, Actor actor, AppDbContext db, IMediaProcessor processor, IObjectStorage storage, CancellationToken ct,
        Guid? jobId = null, string? stage = null, IEventBus? bus = null)
    {
        var (media, error) = await processor.ProcessAsync(bytes, actor.OrgId, c.Id, actor.UserId,
            async sha => await db.ComplaintMedia.AsNoTracking().SingleOrDefaultAsync(m => m.ComplaintId == c.Id && m.Sha256 == sha, ct), ct);

        if (error == "duplicate") return Results.Ok(ToMediaDto(media!, storage));
        if (error is not null)
            return error == "file_too_large" ? Results.Json(new { error }, statusCode: 413)
                 : error == "unsupported_file_type" ? Results.Json(new { error }, statusCode: 415)
                 : Results.BadRequest(new { error });

        media!.JobId = jobId;
        media.Stage = stage;
        db.ComplaintMedia.Add(media);
        // Voice notes are transcribed (AI-09) and complaint photos interpreted (AI-08) in the background; job evidence photos are not.
        if ((media.Kind == "audio" || (media.Kind == "image" && jobId is null)) && bus is not null) bus.Publish(new MediaAdded(actor.OrgId, media.Id, c.Id, media.Kind));
        await db.SaveChangesAsync(ct);
        return Results.Created($"/complaints/{c.Id}", ToMediaDto(media, storage));
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
        IMediaProcessor processor, IObjectStorage storage, IEventBus bus, CancellationToken ct)
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
        var result = await SaveMediaAsync(all.ToArray(), c, actor, db, processor, storage, ct, bus: bus);

        u.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        foreach (var key in u.ChunkKeys) await storage.DeleteAsync(key, ct);
        return result;
    }

    // Mapping ---------------------------------------------------------------------------------------------------------

    internal static ComplaintDto ToDto(Complaint c) => new(
        c.Id, c.Reference, c.Status, c.Channel, c.Description, c.CategoryId, c.Priority, c.HouseId, c.LocationId, c.LocationText, c.Language,
        c.AiStatus, c.AiCategoryKey, c.AiSummary, c.AiConfidence, c.HoldReason, c.StatusReason, c.ReportedByUserId, c.EnteredByUserId, c.MergedIntoId,
        c.CreatedAt, c.UpdatedAt, c.SafetyFlag, c.DueAt, c.RespondBy, c.AutoRouted, c.ReporterPhone, c.ReporterVerified, c.AssetRef, c.AutoConfirmed, c.AiModel, c.IncidentId, c.Latitude, c.Longitude);

    internal static JobDto ToDto(Job j) => new(j.Id, j.Number, j.SequenceNo, j.Status, j.Instructions, j.AssignedTeamId, j.CreatedAt, j.ComplaintId, j.AssignedUserId, j.EvidenceRule, j.RequiresCosts, j.Chargeable, j.ChargeHouseId);

    internal static MediaDto ToMediaDto(ComplaintMedia m, IObjectStorage storage) => new(
        m.Id, m.Kind, m.MimeType, m.SizeBytes, m.Width, m.Height, m.QualityFlags,
        storage.CreateDownloadUrl(m.StorageKey), m.ThumbKey is null ? null : storage.CreateDownloadUrl(m.ThumbKey),
        m.Transcript, m.TranscriptConfidence, m.TranscriptLanguage, m.TranscriptStatus, m.AiLabels, m.AiDescription, m.AiNeedsMoreInfo);
}
