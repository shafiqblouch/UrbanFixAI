using System.Net;
using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

/// <summary>An organization with one person per role, one category and one house.</summary>
public sealed class Society
{
    public Guid Org;
    public ApiSession Manager = null!, Office = null!, Supervisor = null!, Technician = null!, Resident = null!, Neighbour = null!;
    public Guid ResidentId, NeighbourId, CategoryId, HouseId, LocationId;

    public static async Task<Society> CreateAsync(TestApp app, bool linkResidentToHouse = true)
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var s = new Society { Org = org, Manager = m };
        s.Office = await app.AddMemberAsync(m, org, Role.Office);
        s.Supervisor = await app.AddMemberAsync(m, org, Role.Supervisor);
        s.Technician = await app.AddMemberAsync(m, org, Role.Technician);
        s.Resident = await app.AddMemberAsync(m, org, Role.Resident);
        s.Neighbour = await app.AddMemberAsync(m, org, Role.Resident);
        s.ResidentId = (await s.Resident.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
        s.NeighbourId = (await s.Neighbour.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

        s.CategoryId = (await m.Client.GetFromJsonAsync<List<CategoryDto>>("/categories"))!.Single(c => c.Key == "water").Id;
        var block = await (await m.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Block A", null, null, null))).Content.ReadFromJsonAsync<LocationDto>();
        var loc = await (await m.Client.PostAsJsonAsync("/locations", new CreateLocation(block!.Id, "house", "House 1", null, null, null))).Content.ReadFromJsonAsync<LocationDto>();
        var house = await (await m.Client.PostAsJsonAsync("/houses", new CreateHouse(loc!.Id, null, null, null))).Content.ReadFromJsonAsync<HouseDto>();
        s.HouseId = house!.Id; s.LocationId = loc.Id;
        if (linkResidentToHouse)
            (await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(s.ResidentId, "owner", null))).EnsureSuccessStatusCode();
        return s;
    }
}

[Collection("db")]
public class ComplaintTests(TestApp app)
{
    private static readonly Regex ReferenceFormat = new(@"^UF-\d{4}-\d{5}$");

    private static async Task<CreatedDto> Create(ApiSession who, string text = "No water since morning", Guid? categoryId = null, Guid? houseId = null, Guid? locationId = null, string? channel = null, Guid? onBehalfOf = null)
    {
        var res = await who.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, channel, categoryId, houseId, locationId, null, onBehalfOf));
        Assert.True(res.StatusCode == HttpStatusCode.Created, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<CreatedDto>())!;
    }

    private static Task<HttpResponseMessage> Move(ApiSession who, Guid id, ComplaintStatus to, string? reason = null, Guid? mergeInto = null)
        => who.Client.PostAsJsonAsync($"/complaints/{id}/transition", new Transition(to, reason, mergeInto));

    private static async Task<ComplaintDetailDto> Detail(ApiSession who, Guid id)
        => (await who.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!;

    private async Task<long> OutboxEventCount(Guid org, string typeName)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE organization_id = @o AND type LIKE @t", conn);
        cmd.Parameters.AddWithValue("o", org); cmd.Parameters.AddWithValue("t", "%" + typeName + "%");
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // CP-01 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reference_numbers_have_the_agreed_format_and_each_organization_counts_from_one()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        var first = await Create(a.Resident, houseId: a.HouseId);
        var second = await Create(a.Resident, houseId: a.HouseId);
        var otherOrg = await Create(b.Resident, houseId: b.HouseId);

        Assert.Matches(ReferenceFormat, first.Reference);
        Assert.Equal($"UF-{DateTime.UtcNow.Year}-00001", first.Reference);
        Assert.Equal($"UF-{DateTime.UtcNow.Year}-00002", second.Reference);
        Assert.Equal($"UF-{DateTime.UtcNow.Year}-00001", otherOrg.Reference);
    }

    [Fact]
    public async Task Concurrent_creation_never_produces_duplicate_numbers()
    {
        var s = await Society.CreateAsync(app);
        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => Create(s.Resident, houseId: s.HouseId)));

        Assert.Equal(40, results.Select(r => r.Reference).Distinct().Count());
        var numbers = results.Select(r => int.Parse(r.Reference[^5..])).Order().ToArray();
        Assert.Equal(Enumerable.Range(1, 40), numbers); // no gaps either
    }

    // CP-02 -----------------------------------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> NoPermissions = new HashSet<string>();

    [Fact]
    public void Every_listed_transition_is_allowed_for_its_permission_and_nothing_else_is()
    {
        var statuses = Enum.GetValues<ComplaintStatus>();
        foreach (var from in statuses)
            foreach (var to in statuses)
            {
                var rule = ComplaintStateMachine.Find(from, to);
                var all = Permissions.All.ToHashSet();
                var result = ComplaintStateMachine.Check(from, to, all, false, "because");
                if (rule is null) Assert.Equal("illegal_transition", result);
                else
                {
                    // Holding the right permission (with a reason) is enough; holding everything but that permission is not.
                    Assert.True(result is null, $"{from}->{to}: {result}");
                    var without = all.Where(p => p != rule.Permission).ToHashSet();
                    Assert.Equal("forbidden", ComplaintStateMachine.Check(from, to, without, false, "because"));
                }
            }
    }

    [Fact]
    public void Transitions_that_need_a_reason_refuse_blank_ones()
    {
        foreach (var rule in ComplaintStateMachine.AllRules.Where(r => r.ReasonRequired))
        {
            var perms = new HashSet<string> { rule.Permission };
            foreach (var blank in new string?[] { null, "", "   " })
                Assert.Equal("reason_required", ComplaintStateMachine.Check(rule.From, rule.To, perms, false, blank));
            Assert.Null(ComplaintStateMachine.Check(rule.From, rule.To, perms, false, "ok"));
        }
    }

    [Fact]
    public void Reporters_may_confirm_or_reopen_but_not_triage_and_strangers_may_not_at_all()
    {
        var resident = new HashSet<string>(RolePermissions.For(Role.Resident));
        Assert.Null(ComplaintStateMachine.Check(ComplaintStatus.Resolved, ComplaintStatus.Closed, resident, actorIsReporter: true, null));
        Assert.Null(ComplaintStateMachine.Check(ComplaintStatus.Closed, ComplaintStatus.UnderReview, resident, true, "still broken"));
        Assert.Equal("forbidden", ComplaintStateMachine.Check(ComplaintStatus.Resolved, ComplaintStatus.Closed, resident, actorIsReporter: false, null));
        Assert.Equal("forbidden", ComplaintStateMachine.Check(ComplaintStatus.New, ComplaintStatus.UnderReview, resident, true, null));
        Assert.Equal("forbidden", ComplaintStateMachine.Check(ComplaintStatus.Resolved, ComplaintStatus.Closed, NoPermissions, true, null));
    }

    [Fact]
    public void Closed_and_merged_are_dead_ends_except_for_reopening()
    {
        Assert.Empty(ComplaintStateMachine.NextStatuses(ComplaintStatus.Merged));
        Assert.Equal([ComplaintStatus.UnderReview], ComplaintStateMachine.NextStatuses(ComplaintStatus.Closed));
        Assert.Equal([ComplaintStatus.UnderReview], ComplaintStateMachine.NextStatuses(ComplaintStatus.Rejected));
    }

    [Fact]
    public async Task Full_lifecycle_new_to_closed_with_an_event_and_history_entry_per_transition()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        Assert.Equal(ComplaintStatus.New, c.Status);
        Assert.Equal(1L, await OutboxEventCount(s.Org, nameof(ComplaintCreated)));

        Assert.Equal(HttpStatusCode.OK, (await Move(s.Office, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        var job = await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob("Check the main valve", null));
        Assert.Equal(HttpStatusCode.Created, job.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Technician, c.Id, ComplaintStatus.InProgress)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Technician, c.Id, ComplaintStatus.Resolved)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Resident, c.Id, ComplaintStatus.Closed)).StatusCode);

        var detail = await Detail(s.Manager, c.Id);
        Assert.Equal(ComplaintStatus.Closed, detail.Complaint.Status);
        Assert.Equal(
            [ComplaintStatus.New, ComplaintStatus.UnderReview, ComplaintStatus.Assigned, ComplaintStatus.InProgress, ComplaintStatus.Resolved, ComplaintStatus.Closed],
            detail.History.Select(h => h.To).ToArray());
        Assert.Equal("Completed", Assert.Single(detail.Jobs).Status);
        Assert.Equal(5L, await OutboxEventCount(s.Org, nameof(ComplaintStatusChanged)));
    }

    [Fact]
    public async Task Illegal_and_unauthorised_transitions_are_rejected_with_distinct_codes()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);

        var skip = await Move(s.Manager, c.Id, ComplaintStatus.Closed);
        Assert.Equal(HttpStatusCode.Conflict, skip.StatusCode);
        Assert.Contains("illegal_transition", await skip.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await Move(s.Technician, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Move(s.Resident, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        Assert.Equal((await Detail(s.Manager, c.Id)).Complaint.Status, ComplaintStatus.New);
    }

    [Fact]
    public async Task Residents_see_only_their_own_complaints_and_staff_see_all()
    {
        var s = await Society.CreateAsync(app);
        var mine = await Create(s.Resident, categoryId: s.CategoryId);
        var theirs = await Create(s.Neighbour, categoryId: s.CategoryId, houseId: s.HouseId);

        var list = await s.Resident.Client.GetFromJsonAsync<List<ComplaintDto>>("/complaints");
        Assert.Equal([mine.Id], list!.Select(x => x.Id).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await s.Resident.Client.GetAsync($"/complaints/{theirs.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Move(s.Resident, theirs.Id, ComplaintStatus.Rejected, "x")).StatusCode);

        Assert.Equal(2, (await s.Office.Client.GetFromJsonAsync<List<ComplaintDto>>("/complaints"))!.Count);
        var filtered = await s.Office.Client.GetFromJsonAsync<List<ComplaintDto>>("/complaints?status=New");
        Assert.Equal(2, filtered!.Count);
        Assert.Empty((await s.Office.Client.GetFromJsonAsync<List<ComplaintDto>>("/complaints?status=Closed"))!);
    }

    [Fact]
    public async Task Complaints_are_invisible_across_organizations()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        var c = await Create(a.Resident, categoryId: a.CategoryId);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.Client.GetAsync($"/complaints/{c.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Move(b.Manager, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        Assert.Empty((await b.Manager.Client.GetFromJsonAsync<List<ComplaintDto>>("/complaints"))!);
    }

    [Fact]
    public async Task Detail_lists_only_the_moves_the_caller_may_make()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        Assert.Contains(ComplaintStatus.UnderReview, (await Detail(s.Office, c.Id)).AllowedNext);
        Assert.Empty((await Detail(s.Resident, c.Id)).AllowedNext);
        Assert.Empty((await Detail(s.Technician, c.Id)).AllowedNext);
    }

    // CP-03 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Complaint_is_stored_and_numbered_even_when_the_AI_service_is_down()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        ai.Down = true;
        try
        {
            var created = await Create(s.Resident, categoryId: null);
            Assert.Matches(ReferenceFormat, created.Reference);

            var jobs = app.Services.CreateScope().ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>();
            await Assert.ThrowsAsync<HttpRequestException>(() => jobs.RunCoreAsync(s.Org, created.Id, retries: 0, default));

            var after = (await Detail(s.Manager, created.Id)).Complaint;
            Assert.Equal("pending", after.AiStatus); // will be retried
            Assert.Equal(ComplaintStatus.New, after.Status);

            await using var scope = app.Services.CreateAsyncScope();
            var lastTry = scope.ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>();
            var retries = scope.ServiceProvider.GetRequiredService<UrbanFix.Infrastructure.Jobs.JobOptions>().Attempts;
            await lastTry.RunCoreAsync(s.Org, created.Id, retries, default); // out of retries: gives up quietly
            Assert.Equal("failed", (await Detail(s.Manager, created.Id)).Complaint.AiStatus);
        }
        finally { ai.Down = false; }
    }

    [Fact]
    public async Task AI_enrichment_fills_category_summary_and_language_but_never_overrides_a_chosen_category()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var uncategorised = await Create(s.Resident, "pani nahi aa raha");
        var chosen = await Create(s.Resident, "pani nahi aa raha", categoryId: (await s.Manager.Client.GetFromJsonAsync<List<CategoryDto>>("/categories"))!.Single(c => c.Key == "roads").Id);

        await using var scope = app.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<ComplaintEnrichmentJob>();
        var before = ai.Calls.Count;
        await job.RunCoreAsync(s.Org, uncategorised.Id, 0, default);
        await job.RunCoreAsync(s.Org, uncategorised.Id, 0, default); // idempotent: no second AI call
        Assert.Equal(before + 1, ai.Calls.Count);
        await job.RunCoreAsync(s.Org, chosen.Id, 0, default);

        var a = (await Detail(s.Manager, uncategorised.Id)).Complaint;
        Assert.Equal(("done", "water", s.CategoryId, "High"), (a.AiStatus, a.AiCategoryKey, a.CategoryId, a.Priority));
        Assert.Equal("en", a.Language);
        Assert.NotEmpty(a.AiSummary!);

        var b = (await Detail(s.Manager, chosen.Id)).Complaint;
        Assert.Equal("water", b.AiCategoryKey);
        Assert.NotEqual(s.CategoryId, b.CategoryId); // the person's choice (roads) stays
        Assert.Contains(ai.Calls, c => c.Names.Contains("Resident") || c.Names.Any(n => n.StartsWith("Resident", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(8, ai.Calls[^1].Categories.Count);
    }

    [Fact]
    public async Task Creating_a_complaint_queues_an_enrichment_job_through_the_outbox()
    {
        var s = await Society.CreateAsync(app);
        var monitoring = app.Services.GetRequiredService<JobStorage>().GetMonitoringApi();
        var before = monitoring.EnqueuedCount("default");
        await Create(s.Resident);

        await app.Services.GetRequiredService<OutboxProcessor>().ProcessBatchAsync(default);
        Assert.True(monitoring.EnqueuedCount("default") > before);
    }

    // CP-05 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Complaint_without_a_location_is_held_until_the_resident_replies_with_one()
    {
        var s = await Society.CreateAsync(app, linkResidentToHouse: false);
        var c = await Create(s.Resident);
        Assert.True(c.NeedsLocation);
        Assert.Equal(ComplaintStatus.OnHold, c.Status);

        var held = (await Detail(s.Resident, c.Id));
        Assert.Equal("location_missing", held.Complaint.HoldReason);
        Assert.Contains("Location missing", held.History.Last().Reason);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PostAsJsonAsync($"/complaints/{c.Id}/location", new CompleteLocation(null, null, "somewhere"))).StatusCode);
        var fixedUp = await s.Resident.Client.PostAsJsonAsync($"/complaints/{c.Id}/location", new CompleteLocation(s.HouseId, null, "next to the mosque"));
        Assert.Equal(HttpStatusCode.OK, fixedUp.StatusCode);

        var after = await Detail(s.Resident, c.Id);
        Assert.Equal(ComplaintStatus.New, after.Complaint.Status);
        Assert.Null(after.Complaint.HoldReason);
        Assert.Equal((s.HouseId, s.LocationId), (after.Complaint.HouseId, after.Complaint.LocationId));
        Assert.Equal([ComplaintStatus.OnHold, ComplaintStatus.New], after.History.Select(h => h.To).ToArray());
    }

    [Fact]
    public async Task A_resident_linked_to_one_house_does_not_need_to_say_where_they_live()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident);
        Assert.False(c.NeedsLocation);
        var d = (await Detail(s.Resident, c.Id)).Complaint;
        Assert.Equal((s.HouseId, s.LocationId), (d.HouseId, d.LocationId));
    }

    [Fact]
    public async Task Only_the_reporter_or_staff_can_supply_the_missing_location()
    {
        var s = await Society.CreateAsync(app, linkResidentToHouse: false);
        var c = await Create(s.Resident);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Neighbour.Client.PostAsJsonAsync($"/complaints/{c.Id}/location", new CompleteLocation(s.HouseId, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Office.Client.PostAsJsonAsync($"/complaints/{c.Id}/location", new CompleteLocation(null, s.LocationId, null))).StatusCode);
    }

    // CP-06 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Office_entry_on_behalf_of_a_resident_records_the_staff_member_and_the_channel()
    {
        var s = await Society.CreateAsync(app);
        var created = await Create(s.Office, "Resident called: sewer overflowing", channel: "phone", houseId: s.HouseId, onBehalfOf: s.ResidentId);

        var d = (await Detail(s.Office, created.Id)).Complaint;
        var officeId = (await s.Office.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
        Assert.Equal(("phone", officeId, s.ResidentId), (d.Channel, d.EnteredByUserId, d.ReportedByUserId));
        // The resident can follow it in their own list.
        Assert.Contains(created.Id, (await s.Resident.Client.GetFromJsonAsync<List<ComplaintDto>>("/complaints"))!.Select(x => x.Id));

        var walkIn = await Create(s.Office, "Walk-in visitor", channel: "walk_in", houseId: s.HouseId);
        Assert.Equal("walk_in", (await Detail(s.Office, walkIn.Id)).Complaint.Channel);
    }

    [Fact]
    public async Task Channels_are_policed_residents_cannot_pose_as_phone_calls_and_staff_cannot_use_app()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("x", "phone", null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/complaints", new CreateComplaint("x", "app", null, s.HouseId, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("x", "carrier-pigeon", null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("  ", null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync("/complaints", new CreateComplaint("x", null, null, null, null, null, null))).StatusCode);
    }

    // CP-07 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Rejecting_needs_a_reason_and_the_resident_can_read_it()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Office, c.Id, ComplaintStatus.Rejected)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Office, c.Id, ComplaintStatus.Rejected, "  ")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await Move(s.Office, c.Id, ComplaintStatus.Rejected, "This is the neighbouring society's pipe")).StatusCode);
        var seen = await Detail(s.Resident, c.Id);
        Assert.Equal(ComplaintStatus.Rejected, seen.Complaint.Status);
        Assert.Equal("This is the neighbouring society's pipe", seen.Complaint.StatusReason);
        Assert.Equal("This is the neighbouring society's pipe", seen.History.Last().Reason);
    }

    [Fact]
    public async Task Reporter_can_reopen_a_closed_complaint_with_a_reason_but_not_without()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        await Move(s.Office, c.Id, ComplaintStatus.Rejected, "duplicate of something");
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Resident, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Resident, c.Id, ComplaintStatus.UnderReview, "It is not a duplicate, different pipe")).StatusCode);
        Assert.Equal(ComplaintStatus.UnderReview, (await Detail(s.Resident, c.Id)).Complaint.Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Move(s.Neighbour, c.Id, ComplaintStatus.UnderReview, "x")).StatusCode);
    }

    [Fact]
    public async Task Technician_who_cannot_complete_hands_back_with_a_reason_and_a_rework_job_follows()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        await Move(s.Office, c.Id, ComplaintStatus.UnderReview);
        var first = await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob("Fix leak", null))).Content.ReadFromJsonAsync<JobDto>();
        await Move(s.Technician, c.Id, ComplaintStatus.InProgress);

        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Technician, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Technician, c.Id, ComplaintStatus.UnderReview, "Needs a 4-inch valve we do not stock")).StatusCode);

        var mid = await Detail(s.Resident, c.Id);
        Assert.Equal("Needs a 4-inch valve we do not stock", mid.Complaint.StatusReason);
        Assert.Equal("Cancelled", Assert.Single((await Detail(s.Manager, c.Id)).Jobs).Status);

        var rework = await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob("Fit new valve", null))).Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal((1, 2), (first!.SequenceNo, rework!.SequenceNo));
        Assert.NotEqual(first.Number, rework.Number);
        Assert.Equal(2, (await Detail(s.Manager, c.Id)).Jobs.Count);
    }

    [Fact]
    public async Task Failed_verification_sends_the_complaint_back_for_a_rework_job()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        await Move(s.Office, c.Id, ComplaintStatus.UnderReview);
        await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob(null, null));
        await Move(s.Technician, c.Id, ComplaintStatus.InProgress);
        await Move(s.Technician, c.Id, ComplaintStatus.Resolved);

        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Resident, c.Id, ComplaintStatus.UnderReview)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Resident, c.Id, ComplaintStatus.UnderReview, "Still leaking")).StatusCode);
        var again = await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob("Redo", null));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(2, (await again.Content.ReadFromJsonAsync<JobDto>())!.SequenceNo);
    }

    [Fact]
    public async Task Merging_a_duplicate_points_it_at_the_surviving_complaint()
    {
        var s = await Society.CreateAsync(app);
        var keep = await Create(s.Resident, categoryId: s.CategoryId);
        var dupe = await Create(s.Resident, categoryId: s.CategoryId);

        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Office, dupe.Id, ComplaintStatus.Merged, "duplicate")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Office, dupe.Id, ComplaintStatus.Merged, "duplicate", dupe.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Move(s.Office, dupe.Id, ComplaintStatus.Merged, "duplicate", keep.Id)).StatusCode);
        Assert.Equal(keep.Id, (await Detail(s.Office, dupe.Id)).Complaint.MergedIntoId);
        // A merged complaint cannot itself be a merge target.
        var third = await Create(s.Resident, categoryId: s.CategoryId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move(s.Office, third.Id, ComplaintStatus.Merged, "dup", dupe.Id)).StatusCode);
    }

    // JB-01 -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Job_numbers_are_unique_formatted_and_a_job_needs_a_complaint_under_review()
    {
        var s = await Society.CreateAsync(app);
        var c1 = await Create(s.Resident, categoryId: s.CategoryId);
        var c2 = await Create(s.Resident, categoryId: s.CategoryId);

        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c1.Id}/jobs", new CreateJob(null, null))).StatusCode);
        await Move(s.Office, c1.Id, ComplaintStatus.UnderReview);
        await Move(s.Office, c2.Id, ComplaintStatus.UnderReview);
        var j1 = await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c1.Id}/jobs", new CreateJob("one", null))).Content.ReadFromJsonAsync<JobDto>();
        var j2 = await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c2.Id}/jobs", new CreateJob("two", null))).Content.ReadFromJsonAsync<JobDto>();

        Assert.Matches(@"^J-\d{4}-\d{5}$", j1!.Number);
        Assert.NotEqual(j1.Number, j2!.Number);
        Assert.Equal(ComplaintStatus.Assigned, (await Detail(s.Manager, c1.Id)).Complaint.Status);
        // A second job while one is open is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c1.Id}/jobs", new CreateJob("again", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync($"/complaints/{c2.Id}/jobs", new CreateJob(null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync($"/complaints/{c1.Id}/jobs")).StatusCode);
    }

    [Fact]
    public async Task Job_can_be_assigned_to_a_team_of_the_organization_only()
    {
        var s = await Society.CreateAsync(app);
        var dept = await (await s.Manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Water", null, null))).Content.ReadFromJsonAsync<DepartmentDto>();
        var team = await (await s.Manager.Client.PostAsJsonAsync("/teams", new CreateTeam(dept!.Id, "Crew A"))).Content.ReadFromJsonAsync<TeamDto>();
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        await Move(s.Office, c.Id, ComplaintStatus.UnderReview);

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob(null, Guid.NewGuid()))).StatusCode);
        var ok = await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob(null, team!.Id))).Content.ReadFromJsonAsync<JobDto>();
        Assert.Equal(team.Id, ok!.AssignedTeamId);
    }

    // Audit -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Complaint_changes_appear_in_the_audit_trail()
    {
        var s = await Society.CreateAsync(app);
        var c = await Create(s.Resident, categoryId: s.CategoryId);
        await Move(s.Office, c.Id, ComplaintStatus.UnderReview);
        var trail = await s.Manager.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>($"/audit?entityType=Complaint&entityId={c.Id}&order=asc");
        Assert.Equal(["created", "updated"], trail!.Select(t => t.Action).ToArray());
        Assert.Equal("New", trail[1].Before!.Value.GetProperty("Status").GetString());
        Assert.Equal("UnderReview", trail[1].After!.Value.GetProperty("Status").GetString());
    }
}
