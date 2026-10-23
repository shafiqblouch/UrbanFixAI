using System.Net;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Complaints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.DirectoryEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

/// <summary>What the portal needs from the API: filtered complaint lists, job list context and a people directory.</summary>
[Collection("db")]
public class PortalSupportTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<CreatedDto> Make(ApiSession who, string text, Guid? category = null, Guid? house = null, Guid? location = null) =>
        (await (await who.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, category, house, location, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;

    private static async Task<(List<ComplaintDto> Rows, int Total)> List(ApiSession who, string query)
    {
        var res = await who.Client.GetAsync("/complaints" + query);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return ((await res.Content.ReadFromJsonAsync<List<ComplaintDto>>())!, int.Parse(res.Headers.GetValues("X-Total-Count").Single()));
    }

    [Fact]
    public async Task Complaint_list_filters_combine_and_report_the_total_for_paging()
    {
        var s = await Society.CreateAsync(app);
        var dept = await (await s.Manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Water", null, null))).Content.ReadFromJsonAsync<DepartmentDto>();
        var cats = (await s.Manager.Client.GetFromJsonAsync<List<CategoryDto>>("/categories"))!;
        var water = cats.Single(c => c.Key == "water");
        var roads = cats.Single(c => c.Key == "roads");
        await s.Manager.Client.PutAsJsonAsync($"/categories/{water.Id}", new SaveCategory("water", "Water supply", null, dept!.Id, "High", "photo", [], null, null));

        // a second house in another block, to test sector scoping
        var blockB = await (await s.Manager.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Block B", null, null, null))).Content.ReadFromJsonAsync<LocationDto>();
        var locB = await (await s.Manager.Client.PostAsJsonAsync("/locations", new CreateLocation(blockB!.Id, "house", "House 9", null, null, null))).Content.ReadFromJsonAsync<LocationDto>();
        var houseB = await (await s.Manager.Client.PostAsJsonAsync("/houses", new CreateHouse(locB!.Id, null, null, null))).Content.ReadFromJsonAsync<HouseDto>();

        var leak = await Make(s.Resident, "Pipe leaking outside gate", water.Id, s.HouseId);
        var pothole = await Make(s.Resident, "Pothole 100% deep_hole", roads.Id, houseB!.Id);
        var other = await Make(s.Resident, "Street light flickers", null, s.HouseId);
        await s.Office.Client.PostAsJsonAsync($"/complaints/{pothole.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        await Sql("UPDATE complaints SET created_at = now() - interval '40 days' WHERE id = @id", ("id", other.Id));

        Assert.Equal(3, (await List(s.Office, "")).Total);
        Assert.Equal([leak.Id], (await List(s.Office, $"?categoryId={water.Id}")).Rows.Select(r => r.Id));
        Assert.Equal([leak.Id], (await List(s.Office, $"?departmentId={dept.Id}")).Rows.Select(r => r.Id));
        Assert.Equal([pothole.Id], (await List(s.Office, "?status=UnderReview")).Rows.Select(r => r.Id));
        Assert.Equal([pothole.Id], (await List(s.Office, $"?locationId={blockB.Id}")).Rows.Select(r => r.Id)); // the whole sector
        Assert.Equal(2, (await List(s.Office, $"?locationId={s.LocationId}")).Total);
        Assert.Equal([leak.Id], (await List(s.Office, "?priority=High")).Rows.Select(r => r.Id));
        Assert.Equal(2, (await List(s.Office, $"?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-7).ToString("o"))}")).Total);
        Assert.Equal([other.Id], (await List(s.Office, $"?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-7).ToString("o"))}")).Rows.Select(r => r.Id));

        // filters combine
        Assert.Equal([leak.Id], (await List(s.Office, $"?locationId={s.LocationId}&priority=High&status=New")).Rows.Select(r => r.Id));
        Assert.Empty((await List(s.Office, $"?categoryId={water.Id}&status=UnderReview")).Rows);

        // search: reference, description (case-insensitive), and wildcard characters are literal
        Assert.Equal([leak.Id], (await List(s.Office, $"?q={leak.Reference}")).Rows.Select(r => r.Id));
        Assert.Equal([leak.Id], (await List(s.Office, "?q=PIPE%20leaking")).Rows.Select(r => r.Id));
        Assert.Equal([pothole.Id], (await List(s.Office, "?q=100%25")).Rows.Select(r => r.Id));
        Assert.Equal([pothole.Id], (await List(s.Office, "?q=deep_hole")).Rows.Select(r => r.Id));
        Assert.Empty((await List(s.Office, "?q=%25")).Rows.Where(r => !r.Description.Contains('%')));

        // paging keeps the total
        var page = await List(s.Office, "?limit=2&offset=2");
        Assert.Equal((1, 3), (page.Rows.Count, page.Total));
        Assert.Equal(other.Id, page.Rows[0].Id); // oldest last
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.GetAsync($"/complaints?locationId={Guid.NewGuid()}")).StatusCode);

        // a resident still sees only their own, whatever they ask for
        var mine = await List(s.Resident, "");
        Assert.Equal(3, mine.Total);
        Assert.Empty((await List(s.Neighbour, "")).Rows);
        Assert.Empty((await List(s.Neighbour, $"?q={leak.Reference}")).Rows);
    }

    [Fact]
    public async Task Job_list_carries_the_complaint_reference_and_priority_for_display()
    {
        var s = await Society.CreateAsync(app);
        var c = await Make(s.Resident, "Broken tap", s.CategoryId, s.HouseId);
        await s.Office.Client.PostAsJsonAsync($"/complaints/{c.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{c.Id}/jobs", new CreateJob(null, null));

        var item = Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<List<JobListItemDto>>("/jobs"))!);
        Assert.Equal((c.Reference, "High", "Broken tap"), (item.Reference, item.Priority, item.Description));
        // due = the complaint's creation time plus the limit for High (72 h), the same rule the dashboard uses for "overdue"
        var created = (await s.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{c.Id}"))!.Complaint.CreatedAt;
        Assert.Equal(created + Sla.For("High"), item.DueAt);
    }

    [Fact]
    public async Task Directory_lists_people_by_role_for_staff_and_not_for_residents()
    {
        var s = await Society.CreateAsync(app);
        var all = (await s.Supervisor.Client.GetFromJsonAsync<List<PersonDto>>("/directory"))!;
        Assert.Contains(all, p => p.UserId == s.TechnicianId && p.Role == Role.Technician);
        Assert.Contains(all, p => p.UserId == s.ResidentId && p.Role == Role.Resident);

        var techs = (await s.Supervisor.Client.GetFromJsonAsync<List<PersonDto>>("/directory?role=Technician"))!;
        Assert.Equal([s.TechnicianId], techs.Select(p => p.UserId));

        var dept = await (await s.Manager.Client.PostAsJsonAsync("/departments", new SaveDepartment("Works", null, null))).Content.ReadFromJsonAsync<DepartmentDto>();
        var team = await (await s.Manager.Client.PostAsJsonAsync("/teams", new CreateTeam(dept!.Id, "Crew"))).Content.ReadFromJsonAsync<TeamDto>();
        await s.Manager.Client.PostAsJsonAsync($"/teams/{team!.Id}/members", new AddTeamMember(s.TechnicianId, false));
        Assert.Equal([team.Id], (await s.Supervisor.Client.GetFromJsonAsync<List<PersonDto>>("/directory?role=Technician"))!.Single().TeamIds);

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/directory")).StatusCode);

        var other = await Society.CreateAsync(app);
        Assert.DoesNotContain((await other.Supervisor.Client.GetFromJsonAsync<List<PersonDto>>("/directory"))!, p => p.UserId == s.TechnicianId);
    }
}
