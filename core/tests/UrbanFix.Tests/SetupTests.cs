using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class SetupTests(TestApp app)
{
    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<LocationDto> AddLocation(ApiSession s, Guid? parent, string kind, string name)
        => await Ok<LocationDto>(await s.Client.PostAsJsonAsync("/locations", new CreateLocation(parent, kind, name, null, null, null)));

    // OR-01 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_persist_and_are_tenant_scoped()
    {
        var (_, a) = await app.CreateOrganizationAsync();
        var (_, b) = await app.CreateOrganizationAsync();

        var put = await a.Client.PutAsJsonAsync("/organization/settings", new SettingsDto("Green Valley", "Asia/Karachi", "ur", "society", "#4338CA", null));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var readA = await a.Client.GetFromJsonAsync<SettingsDto>("/organization/settings");
        var readB = await b.Client.GetFromJsonAsync<SettingsDto>("/organization/settings");
        Assert.Equal(("Green Valley", "ur", "#4338CA"), (readA!.DisplayName, readA.Locale, readA.PrimaryColor));
        Assert.Null(readB!.DisplayName);
        Assert.Equal("en", readB.Locale);
    }

    [Theory]
    [InlineData("Mars/Phobos", "en", "society", null, "unknown_timezone")]
    [InlineData("Asia/Karachi", "fr", "society", null, "unsupported_locale")]
    [InlineData("Asia/Karachi", "en", "castle", null, "unknown_location_template")]
    [InlineData("Asia/Karachi", "en", "society", "red", "invalid_color")]
    public async Task Invalid_settings_are_rejected(string tz, string locale, string template, string? color, string error)
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var res = await m.Client.PutAsJsonAsync("/organization/settings", new SettingsDto(null, tz, locale, template, color, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(error, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_managers_edit_settings_but_every_member_can_read_them()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var resident = await app.AddMemberAsync(m, org, Role.Resident);
        Assert.Equal(HttpStatusCode.OK, (await resident.Client.GetAsync("/organization/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.PutAsJsonAsync("/organization/settings", new SettingsDto(null, "Asia/Karachi", "en", "society", null, null))).StatusCode);
    }

    // OR-02 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Society_template_builds_sector_street_block_house_and_levels_can_be_skipped()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var sector = await AddLocation(m, null, "sector", "F-7");
        var street = await AddLocation(m, sector.Id, "street", "Street 4");
        var block = await AddLocation(m, street.Id, "block", "Block B");
        var house = await AddLocation(m, block.Id, "house", "House 12");
        Assert.Equal(3, house.Depth);
        Assert.Equal($"/{sector.Id}/{street.Id}/{block.Id}/{house.Id}/", house.Path);

        var skipping = await AddLocation(m, null, "street", "Main Boulevard"); // no sector level
        Assert.Equal(0, skipping.Depth);
    }

    [Fact]
    public async Task Building_template_builds_building_floor_unit_and_rejects_society_levels()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        (await m.Client.PutAsJsonAsync("/organization/settings", new SettingsDto(null, "Asia/Karachi", "en", "building", null, null))).EnsureSuccessStatusCode();

        var tower = await AddLocation(m, null, "building", "Tower A");
        var floor = await AddLocation(m, tower.Id, "floor", "Floor 3");
        var unit = await AddLocation(m, floor.Id, "unit", "Unit 302");
        Assert.Equal(2, unit.Depth);

        var wrong = await m.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "sector", "S", null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task Levels_must_go_deeper_and_siblings_need_unique_names()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var block = await AddLocation(m, null, "block", "Block A");
        var upside = await m.Client.PostAsJsonAsync("/locations", new CreateLocation(block.Id, "street", "Street 1", null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, upside.StatusCode);

        var dup = await m.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "block a", null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task Template_cannot_change_once_locations_exist()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        await AddLocation(m, null, "block", "Block A");
        var res = await m.Client.PutAsJsonAsync("/organization/settings", new SettingsDto(null, "Asia/Karachi", "en", "building", null, null));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Descendant_query_returns_only_the_subtree_and_is_fast_on_a_large_tree()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var sector = await AddLocation(m, null, "sector", "Big Sector");
        var other = await AddLocation(m, null, "sector", "Other Sector");
        var street = await AddLocation(m, sector.Id, "street", "Street 1");

        // 6,000 blocks under the street plus 1,000 elsewhere, inserted directly for speed (owner connection).
        await using (var owner = new NpgsqlConnection(app.OwnerConnectionString))
        {
            await owner.OpenAsync();
            const string sql = """
                INSERT INTO locations (id, organization_id, parent_id, kind, name, path, depth)
                SELECT gen_random_uuid(), @o, @p, 'block', 'B' || g, @pp || gen_random_uuid() || '/', @d FROM generate_series(1, @n) g
                """;
            foreach (var (parent, parentPath, depth, n) in new[] { (street.Id, street.Path, 2, 6000), (other.Id, other.Path, 1, 1000) })
            {
                await using var cmd = new NpgsqlCommand(sql, owner);
                cmd.Parameters.AddWithValue("o", org); cmd.Parameters.AddWithValue("p", parent);
                cmd.Parameters.AddWithValue("pp", parentPath); cmd.Parameters.AddWithValue("d", depth); cmd.Parameters.AddWithValue("n", n);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        await m.Client.GetAsync($"/locations/{sector.Id}/descendants"); // warm up
        var clock = Stopwatch.StartNew();
        var rows = await m.Client.GetFromJsonAsync<List<LocationDto>>($"/locations/{sector.Id}/descendants");
        clock.Stop();

        Assert.Equal(1 + 6000, rows!.Count);
        Assert.All(rows, r => Assert.StartsWith(sector.Path, r.Path));
        Assert.True(clock.ElapsedMilliseconds < 1500, $"took {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_location_can_never_point_at_another_organizations_parent()
    {
        var (orgA, a) = await app.CreateOrganizationAsync();
        var (_, b) = await app.CreateOrganizationAsync();
        var foreign = await AddLocation(b, null, "block", "B's block");

        await using var owner = new NpgsqlConnection(app.OwnerConnectionString);
        await owner.OpenAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO locations (id, organization_id, parent_id, kind, name, path, depth) VALUES (gen_random_uuid(), @o, @p, 'house', 'x', '/x/', 1)", owner);
        cmd.Parameters.AddWithValue("o", orgA); cmd.Parameters.AddWithValue("p", foreign.Id);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);

        var viaApi = await a.Client.PostAsJsonAsync("/locations", new CreateLocation(foreign.Id, "house", "sneaky", null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, viaApi.StatusCode);
    }

    // OR-04 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Teams_belong_to_one_department_and_membership_changes_are_audited()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var dept = await Ok<DepartmentDto>(await m.Client.PostAsJsonAsync("/departments", new SaveDepartment("Water & Sanitation", null, null)));
        var team = await Ok<TeamDto>(await m.Client.PostAsJsonAsync("/teams", new CreateTeam(dept.Id, "Night shift")));
        Assert.Equal(dept.Id, team.DepartmentId);

        var tech = await app.AddMemberAsync(m, org, Role.Technician);
        var techId = (await tech.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
        var member = await Ok<TeamMemberDto>(await m.Client.PostAsJsonAsync($"/teams/{team.Id}/members", new AddTeamMember(techId, false)));
        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync($"/teams/{team.Id}/members", new AddTeamMember(techId, true))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await m.Client.DeleteAsync($"/teams/{team.Id}/members/{member.Id}")).StatusCode);

        var trail = await m.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>($"/audit?entityType=TeamMember&entityId={member.Id}&order=asc");
        Assert.Equal(["created", "deleted"], trail!.Select(t => t.Action).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync("/teams", new CreateTeam(Guid.NewGuid(), "orphan"))).StatusCode);
    }

    [Fact]
    public async Task Team_members_must_belong_to_the_organization()
    {
        var (_, a) = await app.CreateOrganizationAsync();
        var (orgB, b) = await app.CreateOrganizationAsync();
        var outsider = await app.AddMemberAsync(b, orgB, Role.Technician);
        var outsiderId = (await outsider.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

        var dept = await Ok<DepartmentDto>(await a.Client.PostAsJsonAsync("/departments", new SaveDepartment("Roads", null, null)));
        var team = await Ok<TeamDto>(await a.Client.PostAsJsonAsync("/teams", new CreateTeam(dept.Id, "Crew")));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.Client.PostAsJsonAsync($"/teams/{team.Id}/members", new AddTeamMember(outsiderId, false))).StatusCode);
    }

    // OR-05 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task New_organizations_get_the_eight_seeded_categories_and_admins_add_more_without_code()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var cats = await m.Client.GetFromJsonAsync<List<CategoryDto>>("/categories");
        Assert.Equal(8, cats!.Count);
        Assert.Superset(new HashSet<string> { "water", "roads", "streetlights", "sewerage", "sanitation", "parks", "building-maintenance", "parking" }, cats.Select(c => c.Key).ToHashSet());
        Assert.All(cats, c => Assert.False(string.IsNullOrEmpty(c.NameUr)));

        var created = await Ok<CategoryDto>(await m.Client.PostAsJsonAsync("/categories",
            new SaveCategory("lift-repair", "Lift repair", "لفٹ کی مرمت", null, "Urgent", "checklist", ["Check cables", "Check doors"], null)));
        Assert.Equal("Urgent", created.DefaultPriority);
        Assert.Equal(2, created.Checklist.GetArrayLength());
        Assert.Equal(9, (await m.Client.GetFromJsonAsync<List<CategoryDto>>("/categories"))!.Count);

        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync("/categories", new SaveCategory("lift-repair", "Dup", null, null, null, null, null, null))).StatusCode);

        // Seeding again adds nothing; deactivating hides a category from residents.
        var seed = await m.Client.PostAsync("/categories/seed-templates", null);
        Assert.Contains("\"added\":0", await seed.Content.ReadAsStringAsync());
        await m.Client.PutAsJsonAsync($"/categories/{created.Id}", new SaveCategory("lift-repair", "Lift repair", null, null, null, null, null, false));
        var resident = await app.AddMemberAsync(m, org, Role.Resident);
        Assert.DoesNotContain(await resident.Client.GetFromJsonAsync<List<CategoryDto>>("/categories") ?? [], c => c.Key == "lift-repair");
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.PostAsJsonAsync("/categories", new SaveCategory("x", "X", null, null, null, null, null, null))).StatusCode);
    }

    [Theory]
    [InlineData("Bad Key", "X", null, null, "invalid_key")]
    [InlineData("ok", "", null, null, "name_required")]
    [InlineData("ok", "X", "Critical", null, "invalid_priority")]
    [InlineData("ok", "X", null, "video", "invalid_evidence_rule")]
    public async Task Invalid_categories_are_rejected(string key, string name, string? priority, string? evidence, string error)
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var res = await m.Client.PostAsJsonAsync("/categories", new SaveCategory(key, name, null, null, priority, evidence, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(error, await res.Content.ReadAsStringAsync());
    }

    // OR-06 / OR-07 -------------------------------------------------------------------------------------------

    private async Task<(Guid Org, ApiSession Manager, HouseDto House)> OrgWithHouse()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var block = await AddLocation(m, null, "block", "Block A");
        var loc = await AddLocation(m, block.Id, "house", "House 7");
        var house = await Ok<HouseDto>(await m.Client.PostAsJsonAsync("/houses", new CreateHouse(loc.Id, 250m, "A", null)));
        return (org, m, house);
    }

    [Fact]
    public async Task Every_house_has_a_unique_location_of_the_leaf_level()
    {
        var (_, m, house) = await OrgWithHouse();
        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync("/houses", new CreateHouse(house.LocationId, null, null, null))).StatusCode);

        var block = await AddLocation(m, null, "block", "Block Z");
        var notLeaf = await m.Client.PostAsJsonAsync("/houses", new CreateHouse(block.Id, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, notLeaf.StatusCode);
        Assert.Contains("not_a_house_level", await notLeaf.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync("/houses", new CreateHouse(Guid.NewGuid(), null, null, null))).StatusCode);
        var bad = await m.Client.PostAsJsonAsync("/houses", new CreateHouse((await AddLocation(m, block.Id, "house", "H2")).Id, -5m, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Houses_list_filters_by_subtree()
    {
        var (_, m, house) = await OrgWithHouse();
        var all = await m.Client.GetFromJsonAsync<List<HouseDto>>("/houses");
        Assert.Contains(all!, h => h.Id == house.Id);
        var blockId = Guid.Parse(house.LocationPath.Split('/', StringSplitOptions.RemoveEmptyEntries)[0]);
        Assert.Single(await m.Client.GetFromJsonAsync<List<HouseDto>>($"/houses?underLocationId={blockId}") ?? []);
        var elsewhere = await AddLocation(m, null, "block", "Empty Block");
        Assert.Empty(await m.Client.GetFromJsonAsync<List<HouseDto>>($"/houses?underLocationId={elsewhere.Id}") ?? []);
    }

    [Fact]
    public async Task A_house_can_have_an_owner_and_a_tenant_and_moving_out_keeps_history()
    {
        var (org, m, house) = await OrgWithHouse();
        var owner = await app.AddMemberAsync(m, org, Role.Resident);
        var tenant = await app.AddMemberAsync(m, org, Role.Resident);
        var tenant2 = await app.AddMemberAsync(m, org, Role.Resident);
        var ownerId = (await owner.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
        var tenantId = (await tenant.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
        var tenant2Id = (await tenant2.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

        await Ok<ResidentLinkDto>(await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(ownerId, "owner", new DateOnly(2020, 1, 1))));
        var tenantLink = await Ok<ResidentLinkDto>(await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(tenantId, "tenant", new DateOnly(2024, 3, 1))));

        // A second current tenant is refused...
        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(tenant2Id, "tenant", null))).StatusCode);

        // ...until the first moves out; the old link stays as history.
        var ended = await Ok<ResidentLinkDto>(await m.Client.PostAsJsonAsync($"/resident-links/{tenantLink.Id}/end", new EndResidentLink(new DateOnly(2025, 6, 30))));
        Assert.Equal(new DateOnly(2025, 6, 30), ended.ToDate);
        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync($"/resident-links/{tenantLink.Id}/end", new EndResidentLink(null))).StatusCode);
        await Ok<ResidentLinkDto>(await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(tenant2Id, "tenant", new DateOnly(2025, 7, 1))));

        var history = await m.Client.GetFromJsonAsync<List<ResidentLinkDto>>($"/houses/{house.Id}/residents");
        Assert.Equal(3, history!.Count);
        Assert.Single(history, h => h.Relation == "tenant" && h.ToDate is not null);
        Assert.Single(history, h => h.Relation == "tenant" && h.ToDate is null);
    }

    [Fact]
    public async Task Family_members_are_unlimited_and_unknown_relations_or_users_are_refused()
    {
        var (org, m, house) = await OrgWithHouse();
        for (var i = 0; i < 3; i++)
        {
            var member = await app.AddMemberAsync(m, org, Role.Resident);
            var id = (await member.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
            Assert.Equal(HttpStatusCode.Created, (await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(id, "family", null))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(Guid.NewGuid(), "owner", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(Guid.NewGuid(), "landlord", null))).StatusCode);
    }

    [Fact]
    public async Task Users_from_other_organizations_are_invisible_and_a_deactivated_member_is_reactivated_as_resident_on_linking()
    {
        var (_, a, house) = await OrgWithHouse();
        var (orgB, b) = await app.CreateOrganizationAsync();
        var outsider = await app.AddMemberAsync(b, orgB, Role.Resident);
        var outsiderId = (await outsider.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

        // Row-level security hides other organizations' users: they cannot be linked by id.
        var res = await a.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(outsiderId, "owner", null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("user_not_found", await res.Content.ReadAsStringAsync());

        // A former member (membership deactivated) can be linked again and gets an active Resident role.
        var orgA = (await a.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.OrganizationId!.Value;
        var former = await app.AddMemberAsync(a, orgA, Role.Resident);
        var formerId = (await former.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;
        var members = await a.Client.GetFromJsonAsync<List<AdminEndpoints.MemberDto>>("/members");
        await a.Client.DeleteAsync($"/members/{members!.Single(x => x.UserId == formerId).MembershipId}");

        Assert.Equal(HttpStatusCode.Created, (await a.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(formerId, "owner", null))).StatusCode);
        members = await a.Client.GetFromJsonAsync<List<AdminEndpoints.MemberDto>>("/members");
        Assert.Contains(members!, x => x.UserId == formerId && x.Role == Role.Resident && x.IsActive);
    }
    [Fact]
    public async Task Office_staff_can_link_residents_but_residents_cannot()
    {
        var (org, m, house) = await OrgWithHouse();
        var office = await app.AddMemberAsync(m, org, Role.Office);
        var resident = await app.AddMemberAsync(m, org, Role.Resident);
        var id = (await resident.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(id, "owner", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await office.Client.PostAsJsonAsync($"/houses/{house.Id}/residents", new CreateResidentLink(id, "owner", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await office.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Nope", null, null, null))).StatusCode);
    }
}

[Collection("db")]
public class LandmarkTests(TestApp app)
{
    private static async Task<LandmarkDto> Add(ApiSession s, string name, string[]? aliases = null, Guid? loc = null, string? ur = null)
    {
        var res = await s.Client.PostAsJsonAsync("/landmarks", new SaveLandmark(name, ur, aliases, loc, null, null, null));
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<LandmarkDto>())!;
    }

    [Fact]
    public async Task Landmarks_are_searchable_by_name_urdu_name_and_alias_and_scoped_to_the_organization()
    {
        var (_, a) = await app.CreateOrganizationAsync();
        var (_, b) = await app.CreateOrganizationAsync();
        var mosque = await Add(a, "Madni Masjid", ["Green Mosque", "hari masjid"], ur: "مدنی مسجد");
        await Add(a, "Park Gate");
        await Add(b, "Madni Masjid"); // same name in another organization is fine

        async Task<List<LandmarkDto>> Search(string q) => (await a.Client.GetFromJsonAsync<List<LandmarkDto>>($"/landmarks?q={Uri.EscapeDataString(q)}"))!;
        Assert.Equal([mosque.Id], (await Search("madni")).Select(l => l.Id).ToArray());
        Assert.Equal([mosque.Id], (await Search("GREEN mosque")).Select(l => l.Id).ToArray());
        Assert.Equal([mosque.Id], (await Search("مسجد")).Select(l => l.Id).ToArray());
        Assert.Empty(await Search("airport"));
        Assert.Equal(2, (await a.Client.GetFromJsonAsync<List<LandmarkDto>>("/landmarks"))!.Count);
    }

    [Fact]
    public async Task Landmarks_can_be_tied_to_a_location_and_names_are_unique()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        var block = await (await m.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Block A", null, null, null))).Content.ReadFromJsonAsync<LocationDto>();
        var inBlock = await Add(m, "Block A Park", loc: block!.Id);
        Assert.Equal(block.Id, inBlock.LocationId);
        Assert.Single((await m.Client.GetFromJsonAsync<List<LandmarkDto>>($"/landmarks?locationId={block.Id}"))!);

        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync("/landmarks", new SaveLandmark("block a park", null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync("/landmarks", new SaveLandmark("Ghost", null, null, Guid.NewGuid(), null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync("/landmarks", new SaveLandmark(" ", null, null, null, null, null, null))).StatusCode);

        var resident = await app.AddMemberAsync(m, org, Role.Resident);
        Assert.Equal(HttpStatusCode.OK, (await resident.Client.GetAsync("/landmarks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.PostAsJsonAsync("/landmarks", new SaveLandmark("X", null, null, null, null, null, null))).StatusCode);
    }

    [Fact]
    public async Task Editing_and_deactivating_a_landmark_works_and_inactive_ones_leave_the_search()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var l = await Add(m, "Old Name", ["  alias one ", "alias one", ""]);
        Assert.Equal(["alias one"], l.Aliases); // trimmed, de-duplicated, blanks dropped

        var renamed = await (await m.Client.PutAsJsonAsync($"/landmarks/{l.Id}", new SaveLandmark("New Name", null, ["x"], null, 24.8, 67.0, null))).Content.ReadFromJsonAsync<LandmarkDto>();
        Assert.Equal(("New Name", 24.8), (renamed!.Name, renamed.Latitude));
        await m.Client.PutAsJsonAsync($"/landmarks/{l.Id}", new SaveLandmark("New Name", null, null, null, null, null, false));
        Assert.Empty((await m.Client.GetFromJsonAsync<List<LandmarkDto>>("/landmarks"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await m.Client.PutAsJsonAsync($"/landmarks/{Guid.NewGuid()}", new SaveLandmark("Nope", null, null, null, null, null, null))).StatusCode);
    }

    [Fact]
    public async Task Landmarks_and_their_aliases_are_sent_to_the_AI_with_every_enrichment()
    {
        var s = await Society.CreateAsync(app);
        await Add(s.Manager, "Madni Masjid", ["Green Mosque"]);
        await Add(s.Manager, "Park Gate");
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var created = await (await s.Resident.Client.PostAsJsonAsync("/complaints", new ComplaintEndpoints.CreateComplaint("sewer near the mosque", null, null, null, null, null, null)))
            .Content.ReadFromJsonAsync<ComplaintEndpoints.CreatedDto>();

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<UrbanFix.Infrastructure.Complaints.ComplaintEnrichmentJob>().RunCoreAsync(s.Org, created!.Id, 0, default);

        var call = ai.Calls.Last(c => c.Text == "sewer near the mosque");
        Assert.Equal(["Madni Masjid (also: Green Mosque)", "Park Gate"], call.Landmarks.ToArray());
    }
}
