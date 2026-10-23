using System.Net;
using System.Net.Http.Headers;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Infrastructure.Platform;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.PlatformEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class PlatformTests(TestApp app)
{
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private HttpClient Bearer(string token)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UrbanFix-Tests/1.0");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // PA-01 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_overview_lists_every_society_with_its_size_and_only_platform_admins_see_it()
    {
        var s = await Society.CreateAsync(app);
        var admin = await app.LoginPlatformAdminAsync();
        var rows = await Ok<List<PlatformOrganizationDto>>(admin.Client.GetAsync("/admin/organizations/overview"));
        var mine = Assert.Single(rows, r => r.Id == s.Org);
        Assert.True(mine.Members >= 5);
        Assert.Equal(1, mine.Houses);
        Assert.DoesNotContain(rows, r => r.Slug == "platform");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.Client.GetAsync("/admin/organizations/overview")).StatusCode);
    }

    [Fact]
    public async Task A_society_can_be_created_already_set_to_its_language_time_zone_and_structure()
    {
        var admin = await app.LoginPlatformAdminAsync();
        var slug = "org-" + Guid.NewGuid().ToString("N")[..8];
        var res = await admin.Client.PostAsJsonAsync("/admin/organizations",
            new AdminEndpoints.CreateOrganizationRequest(slug, slug, $"m@{slug}.test", "Manager", TestSecrets.UserPassword, "Gulshan Heights", "building", "Asia/Dubai", "ur"));
        var org = (await res.Content.ReadFromJsonAsync<AdminEndpoints.OrganizationDto>())!;
        var manager = await app.LoginAsync($"m@{slug}.test", TestSecrets.UserPassword, org.Id);
        var settings = await Ok<SettingsDto>(manager.Client.GetAsync("/organization/settings"));
        Assert.Equal(("Gulshan Heights", "building", "Asia/Dubai", "ur"), (settings.DisplayName, settings.LocationTemplate, settings.Timezone, settings.Locale));

        string Slug() => "org-" + Guid.NewGuid().ToString("N")[..8];
        async Task<HttpStatusCode> Try(string? template, string? zone, string? locale)
        {
            var s = Slug();
            return (await admin.Client.PostAsJsonAsync("/admin/organizations", new AdminEndpoints.CreateOrganizationRequest(s, s, $"m@{s}.test", "M", TestSecrets.UserPassword, null, template, zone, locale))).StatusCode;
        }
        Assert.Equal(HttpStatusCode.BadRequest, await Try("castle", null, null));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(null, "Mars/Olympus", null));
        Assert.Equal(HttpStatusCode.BadRequest, await Try(null, null, "fr"));
    }

    [Fact]
    public async Task A_new_society_shows_what_is_still_to_set_up_and_is_ready_once_the_basics_exist()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var admin = await app.LoginPlatformAdminAsync();
        var first = await Ok<OnboardingDto>(admin.Client.GetAsync($"/admin/organizations/{org}/onboarding"));
        Assert.True(first.Steps.Single(x => x.Key == "manager").Done);
        Assert.True(first.Steps.Single(x => x.Key == "categories").Done); // the standard categories are seeded
        Assert.False(first.Steps.Single(x => x.Key == "houses").Done);
        Assert.False(first.ReadyForUse);

        var block = await Ok<LocationDto>(manager.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Block A", null, null, null)));
        var place = await Ok<LocationDto>(manager.Client.PostAsJsonAsync("/locations", new CreateLocation(block.Id, "house", "House 1", null, null, null)));
        await Ok<HouseDto>(manager.Client.PostAsJsonAsync("/houses", new CreateHouse(place.Id, null, null, null)));
        var dept = await Ok<DepartmentDto>(manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Water", null, null)));
        await Ok<TeamDto>(manager.Client.PostAsJsonAsync("/teams", new CreateTeam(dept.Id, "Water team")));

        var ready = await Ok<OnboardingDto>(admin.Client.GetAsync($"/admin/organizations/{org}/onboarding"));
        Assert.True(ready.ReadyForUse);
        Assert.Equal(1, ready.Steps.Single(x => x.Key == "houses").Count);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync($"/admin/organizations/{Guid.NewGuid()}/onboarding")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.Client.GetAsync($"/admin/organizations/{org}/onboarding")).StatusCode);
    }

    [Fact]
    public async Task Support_is_not_a_role_anyone_can_be_given()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        var res = await manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest("s@x.test", "S", TestSecrets.UserPassword, Role.Support, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("role_not_assignable", await res.Content.ReadAsStringAsync());
    }

    // PA-02 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_feature_can_be_switched_on_for_one_society_only_without_a_deployment()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        var admin = await app.LoginPlatformAdminAsync();

        var before = await Ok<List<FeatureDto>>(admin.Client.GetAsync($"/admin/organizations/{a.Org}/features"));
        Assert.True(before.Single(f => f.Feature == Features.AutoRouting).Enabled);
        Assert.False(before.Single(f => f.Feature == Features.Gate).Enabled);

        await Ok<FeatureDto>(admin.Client.PutAsJsonAsync($"/admin/organizations/{a.Org}/features/{Features.Gate}", new SetFeature(true)));
        var mineA = await Ok<Dictionary<string, bool>>(a.Manager.Client.GetAsync("/features"));
        var mineB = await Ok<Dictionary<string, bool>>(b.Manager.Client.GetAsync("/features"));
        Assert.True(mineA[Features.Gate]);
        Assert.False(mineB[Features.Gate]); // the other society is untouched

        await admin.Client.PutAsJsonAsync($"/admin/organizations/{a.Org}/features/{Features.Gate}", new SetFeature(false));
        Assert.False((await Ok<Dictionary<string, bool>>(a.Manager.Client.GetAsync("/features")))[Features.Gate]);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PutAsJsonAsync($"/admin/organizations/{a.Org}/features/teleport", new SetFeature(true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Manager.Client.PutAsJsonAsync($"/admin/organizations/{a.Org}/features/{Features.Gate}", new SetFeature(true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PutAsJsonAsync($"/admin/organizations/{Guid.NewGuid()}/features/{Features.Gate}", new SetFeature(true))).StatusCode);
    }

    [Fact]
    public async Task Switching_automatic_routing_off_stops_a_category_being_made_automatic_and_the_change_is_audited()
    {
        var s = await Society.CreateAsync(app);
        var admin = await app.LoginPlatformAdminAsync();
        await admin.Client.PutAsJsonAsync($"/admin/organizations/{s.Org}/features/{Features.AutoRouting}", new SetFeature(false));

        var res = await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new RoutingEndpoints.SaveAutoRoute(true, null));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("feature_disabled", await res.Content.ReadAsStringAsync());
        // Turning it off is never refused.
        Assert.Equal(HttpStatusCode.OK, (await s.Manager.Client.PutAsJsonAsync($"/categories/{s.CategoryId}/auto-route", new RoutingEndpoints.SaveAutoRoute(false, null))).StatusCode);

        var trail = await Ok<List<AdminEndpoints.AuditDto>>(s.Manager.Client.GetAsync("/audit?entityType=Feature"));
        Assert.Contains(trail, e => e.EntityId == Features.AutoRouting && e.Action == "feature_changed");
    }

    // PA-03 / PA-04 ---------------------------------------------------------------------------------------------------

    private async Task<Guid> ComplaintIn(Society s) =>
        (await Ok<CreatedDto>(s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null)))).Id;

    [Fact]
    public async Task AI_quality_is_worked_out_per_society_category_and_language_from_the_suggestion_log()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        async Task Feedback(Society s, string suggested, string language, bool accepted, int times)
        {
            for (var i = 0; i < times; i++)
                await Sql(@"INSERT INTO ai_feedback (id, organization_id, complaint_id, field, suggested, final_value, accepted, language)
                            VALUES (gen_random_uuid(), @o, @c, 'category', @s, @f, @a, @l)", ("o", s.Org), ("c", await ComplaintIn(s)), ("s", suggested), ("f", accepted ? suggested : "other"), ("a", accepted), ("l", language));
        }
        await Feedback(a, "water", "en", true, 3);
        await Feedback(a, "water", "en", false, 1);
        await Feedback(a, "water", "ur", true, 1);
        await Feedback(b, "roads", "en", false, 2);

        var admin = await app.LoginPlatformAdminAsync();
        var rows = await Ok<List<AiQualityRow>>(admin.Client.GetAsync($"/admin/ai/quality?organizationId={a.Org}"));
        var english = Assert.Single(rows, r => r.Language == "en");
        Assert.Equal((4, 3, 1, 0.75, 0.25), (english.Total, english.Accepted, english.Overridden, english.AcceptRate, english.OverrideRate));
        Assert.Equal(1.0, Assert.Single(rows, r => r.Language == "ur").AcceptRate);
        Assert.All(rows, r => Assert.Equal(a.Org, r.OrganizationId));

        var everyone = await Ok<List<AiQualityRow>>(admin.Client.GetAsync("/admin/ai/quality"));
        Assert.Equal(0.0, Assert.Single(everyone, r => r.OrganizationId == b.Org).AcceptRate);

        // The society's own report agrees with the platform's for that society.
        var own = await Ok<List<RoutingEndpoints.AccuracyRow>>(a.Manager.Client.GetAsync("/reports/ai-accuracy"));
        Assert.Equal(english.Total, own.Single(r => r.Language == "en").Total);
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Manager.Client.GetAsync("/admin/ai/quality")).StatusCode);
    }

    [Fact]
    public async Task AI_cost_per_complaint_and_the_cap_are_visible_per_society_and_the_cap_can_be_changed()
    {
        var s = await Society.CreateAsync(app);
        var complaint = await ComplaintIn(s);
        await Sql(@"INSERT INTO ai_usage (id, organization_id, complaint_id, task, model, input_tokens, output_tokens, cost_usd, occurred_at)
                    VALUES (gen_random_uuid(), @o, @c, 'understanding', 'm', 1000, 500, 0.0100, now()), (gen_random_uuid(), @o, @c, 'embedding', 'm', 200, 0, 0.0020, now())", ("o", s.Org), ("c", complaint));

        var admin = await app.LoginPlatformAdminAsync();
        var usage = await Ok<AiUsageDto>(admin.Client.GetAsync("/admin/ai/usage"));
        var row = Assert.Single(usage.Organizations, o => o.OrganizationId == s.Org);
        Assert.Equal((2, 1700L, 1), (row.Calls, row.InputTokens + row.OutputTokens, row.Complaints));
        Assert.Equal(0.012m, row.CostUsd);
        Assert.Equal(0.012m, row.CostPerComplaintUsd);
        Assert.Equal(2_000_000, row.MonthlyCap);
        Assert.False(row.OverCap);

        await Ok<SetAiCap>(admin.Client.PutAsJsonAsync($"/admin/organizations/{s.Org}/ai-cap", new SetAiCap(1000)));
        var capped = Assert.Single((await Ok<AiUsageDto>(admin.Client.GetAsync("/admin/ai/usage"))).Organizations, o => o.OrganizationId == s.Org);
        Assert.Equal(1000, capped.MonthlyCap);
        Assert.True(capped.OverCap);
        // The society sees the same allowance in its own policy.
        Assert.Equal(1000, (await Ok<UrbanFix.OrgSetup.OrgPolicy>(s.Manager.Client.GetAsync("/organization/policies"))).AiMonthlyTokenCap);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PutAsJsonAsync($"/admin/organizations/{s.Org}/ai-cap", new SetAiCap(-1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.Client.PutAsJsonAsync($"/admin/organizations/{s.Org}/ai-cap", new SetAiCap(5))).StatusCode);
        var trail = await Ok<List<AdminEndpoints.AuditDto>>(s.Manager.Client.GetAsync("/audit?action=ai_cap_changed"));
        Assert.Single(trail);
    }

    // PA-05 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_application_role_cannot_read_or_write_support_sessions_directly()
    {
        var s = await Society.CreateAsync(app);
        foreach (var sql in new[] { "SELECT count(*) FROM support_sessions", "DELETE FROM support_sessions", "UPDATE support_sessions SET ended_at = now()" })
        {
            await using var conn = await app.OpenAppConnectionAsync(s.Org);
            await using var cmd = new NpgsqlCommand(sql, conn);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("42501", denied.SqlState); // insufficient_privilege
        }
    }

    [Fact]
    public async Task A_support_session_needs_a_reason_and_a_sensible_time_and_only_a_platform_admin_can_open_one()
    {
        var s = await Society.CreateAsync(app);
        var admin = await app.LoginPlatformAdminAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("help", 30))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Resident cannot upload a photo", 600))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Resident cannot upload a photo", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsJsonAsync($"/admin/organizations/{Guid.NewGuid()}/support-sessions", new OpenSupport("Resident cannot upload a photo", 30))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Resident cannot upload a photo", 30))).StatusCode);
    }

    [Fact]
    public async Task During_a_support_session_the_platform_person_can_look_but_not_change_anything()
    {
        var s = await Society.CreateAsync(app);
        var created = await ComplaintIn(s);
        var admin = await app.LoginPlatformAdminAsync();
        var opened = await Ok<SupportOpened>(admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Resident cannot upload a photo", 30)));
        var support = Bearer(opened.Token);

        // Looking works, and it is that society's data.
        var list = await Ok<List<ComplaintDto>>(support.GetAsync("/complaints"));
        Assert.Equal(created, Assert.Single(list).Id);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync($"/complaints/{created}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await support.GetAsync("/dashboard/summary")).StatusCode);

        // Changing does not, whichever way it is tried.
        var post = await support.PostAsJsonAsync("/complaints", new CreateComplaint("Planted", null, s.CategoryId, s.HouseId, null, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Contains("support_read_only", await post.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/complaints/{created}/transition", new Transition(UrbanFix.Complaints.ComplaintStatus.Rejected, "x", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PutAsJsonAsync("/organization/settings", new SettingsDto("Hacked", "Asia/Karachi", "en", "society", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.DeleteAsync($"/members/{Guid.NewGuid()}")).StatusCode);
        Assert.Single(await Ok<List<ComplaintDto>>(s.Manager.Client.GetAsync("/complaints"))); // nothing was added

        // Staff-only areas stay closed, and it cannot open another support session or reach other societies.
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync("/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.GetAsync("/admin/organizations/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await support.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Opening another one", 30))).StatusCode);
        var other = await Society.CreateAsync(app);
        await ComplaintIn(other);
        Assert.Single(await Ok<List<ComplaintDto>>(support.GetAsync("/complaints"))); // still only the one society's
    }

    [Fact]
    public async Task The_support_person_appears_as_support_in_me_with_the_time_left()
    {
        var s = await Society.CreateAsync(app);
        var admin = await app.LoginPlatformAdminAsync();
        var opened = await Ok<SupportOpened>(admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Checking a stuck complaint", 15)));
        var me = await Ok<AuthEndpoints.MeResponse>(Bearer(opened.Token).GetAsync("/me"));
        Assert.Equal(s.Org, me.OrganizationId);
        Assert.Equal("Support", Assert.Single(me.Memberships).Role);
        Assert.NotNull(me.Support);
        Assert.InRange((me.Support!.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes, 13, 15.5);
        Assert.Null((await Ok<AuthEndpoints.MeResponse>(admin.Client.GetAsync("/me"))).Support); // the normal session is unchanged
    }

    [Fact]
    public async Task Every_support_session_appears_in_the_audit_log_of_the_society_and_of_the_platform()
    {
        var s = await Society.CreateAsync(app);
        await ComplaintIn(s);
        var admin = await app.LoginPlatformAdminAsync();
        var opened = await Ok<SupportOpened>(admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Resident cannot upload a photo", 30)));
        var support = Bearer(opened.Token);
        await support.GetAsync("/complaints");
        await support.PostAsJsonAsync("/complaints", new CreateComplaint("Planted", null, s.CategoryId, s.HouseId, null, null, null));
        Assert.True((await admin.Client.PostAsync($"/admin/support-sessions/{opened.SessionId}/end", null)).IsSuccessStatusCode);

        // The society's manager can read what the platform person did.
        var trail = await Ok<List<AdminEndpoints.AuditDto>>(s.Manager.Client.GetAsync($"/audit?entityType=SupportSession&entityId={opened.SessionId}&order=asc"));
        Assert.Equal(["support_session_started", "support_viewed", "support_change_refused", "support_session_ended"], trail.Select(e => e.Action).ToArray());
        Assert.Contains("Resident cannot upload a photo", trail[0].After.ToString());
        Assert.Contains("/complaints", trail[1].After.ToString());

        // And the platform's own log holds the start and the end.
        var platformTrail = await Ok<List<AdminEndpoints.AuditDto>>(admin.Client.GetAsync($"/audit?entityType=SupportSession&entityId={opened.SessionId}"));
        Assert.Contains(platformTrail, e => e.Action == "support_session_started");
        Assert.Contains(platformTrail, e => e.Action == "support_session_ended");

        var sessions = await Ok<List<SupportSessionRow>>(admin.Client.GetAsync("/admin/support-sessions"));
        var row = Assert.Single(sessions, r => r.Id == opened.SessionId);
        Assert.False(row.Active);
        Assert.Equal("Resident cannot upload a photo", row.Reason);
    }

    [Fact]
    public async Task A_support_session_stops_working_when_it_is_ended_or_runs_out()
    {
        var s = await Society.CreateAsync(app);
        var admin = await app.LoginPlatformAdminAsync();

        var ended = await Ok<SupportOpened>(admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Looking at a complaint", 30)));
        Assert.Equal(HttpStatusCode.OK, (await Bearer(ended.Token).GetAsync("/complaints")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Client.PostAsync($"/admin/support-sessions/{ended.SessionId}/end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(ended.Token).GetAsync("/complaints")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.PostAsync($"/admin/support-sessions/{ended.SessionId}/end", null)).StatusCode); // already ended

        var expiring = await Ok<SupportOpened>(admin.Client.PostAsJsonAsync($"/admin/organizations/{s.Org}/support-sessions", new OpenSupport("Looking at another one", 30)));
        Assert.Equal(HttpStatusCode.OK, (await Bearer(expiring.Token).GetAsync("/complaints")).StatusCode);
        await Sql("UPDATE sessions SET expires_at = now() - interval '1 minute' WHERE support_session_id = @id", ("id", expiring.SessionId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Bearer(expiring.Token).GetAsync("/complaints")).StatusCode);

        // The platform person's own session is not affected by any of this.
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync("/admin/organizations/overview")).StatusCode);
    }
}
