using System.Diagnostics;
using System.Net;
using System.Text;
using Npgsql;
using UrbanFix.Infrastructure.Registry;
using UrbanFix.OrgSetup;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.RegistryEndpoints;

namespace UrbanFix.Tests;

public class RegistryRulesTests
{
    [Theory]
    [InlineData("ABC-123", "ABC123")]
    [InlineData("abc 123", "ABC123")]
    [InlineData(" lea/09 - 4521 ", "LEA094521")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Plates_that_differ_only_in_spacing_dashes_or_case_are_the_same_vehicle(string? plate, string key) => Assert.Equal(key, RegistryRules.PlateKey(plate));

    [Theory]
    [InlineData("35202-1234567-1", "•••••••••5671")]
    [InlineData("1234", "1234")]
    [InlineData("12", "12")]
    [InlineData("AB 12345", "•••2345")]
    public void Only_the_last_four_characters_of_an_id_number_are_kept(string id, string masked) => Assert.Equal(masked, RegistryRules.MaskId(id));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData(null)]
    public void Nothing_to_mask_gives_nothing(string? id) => Assert.Null(RegistryRules.MaskId(id));

    [Theory]
    [InlineData("mon,tue", new[] { "mon", "tue" })]
    [InlineData("Mon; Wed Fri", new[] { "mon", "wed", "fri" })]
    [InlineData("weekdays", new[] { "mon", "tue", "wed", "thu", "fri" })]
    [InlineData("daily", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("sat,sat,mon", new[] { "mon", "sat" })]
    public void Days_are_read_in_the_ways_people_write_them(string text, string[] expected) => Assert.Equal(expected, RegistryRules.ParseDays(text));

    [Theory]
    [InlineData("funday")]
    [InlineData("mon,xyz")]
    public void Something_that_is_not_a_day_is_refused(string text) => Assert.Null(RegistryRules.ParseDays(text));

    [Fact]
    public void A_visit_is_allowed_only_on_the_agreed_days_and_hours_of_an_active_record()
    {
        var help = new DailyHelp { AllowedDays = ["mon", "wed"], AllowedFrom = new TimeOnly(8, 0), AllowedTo = new TimeOnly(17, 0) };
        Assert.True(RegistryRules.IsAllowed(help, DayOfWeek.Monday, new TimeOnly(9, 30)));
        Assert.False(RegistryRules.IsAllowed(help, DayOfWeek.Tuesday, new TimeOnly(9, 30)));
        Assert.False(RegistryRules.IsAllowed(help, DayOfWeek.Monday, new TimeOnly(7, 59)));
        Assert.False(RegistryRules.IsAllowed(help, DayOfWeek.Monday, new TimeOnly(17, 1)));
        Assert.True(RegistryRules.IsAllowed(help, DayOfWeek.Wednesday, new TimeOnly(17, 0)));
        Assert.True(RegistryRules.IsAllowed(new DailyHelp(), DayOfWeek.Sunday, new TimeOnly(23, 0))); // no limits set
        help.Status = "inactive";
        Assert.False(RegistryRules.IsAllowed(help, DayOfWeek.Monday, new TimeOnly(9, 30)));
        Assert.True(RegistryRules.IsAllowed(new DailyHelp { AllowedDays = ["sun"] }, DayOfWeek.Sunday, new TimeOnly(9, 0)));
    }
}

public class CsvTests
{
    [Fact]
    public void Quotes_commas_line_breaks_and_urdu_inside_values_are_read_whole()
    {
        var t = Csv.Parse("name,note\r\n\"Khan, Ali\",\"said \"\"hello\"\"\"\r\n\"Sara\",\"line one\nline two\"\r\nاحمد,پانی\r\n");
        Assert.Equal(["name", "note"], t.Header);
        Assert.Equal(["Khan, Ali", "Sara", "احمد"], t.Rows.Select(r => r.Get("name")));
        Assert.Equal("said \"hello\"", t.Rows[0].Get("note"));
        Assert.Equal("line one\nline two", t.Rows[1].Get("note"));
        Assert.Equal("پانی", t.Rows[2].Get("note"));
    }

    [Fact]
    public void Line_numbers_point_at_the_line_in_the_file_even_after_multi_line_values()
    {
        var t = Csv.Parse("a,b\n1,\"x\ny\"\n\n3,4\n");
        Assert.Equal([2, 5], t.Rows.Select(r => r.Line));
    }

    [Fact]
    public void A_byte_order_mark_blank_lines_headers_in_any_case_and_short_rows_are_tolerated()
    {
        var t = Csv.Parse("﻿Plate, Type \n\nabc,car\nxyz\n");
        Assert.Equal(["plate", "type"], t.Header);
        Assert.Equal(("ABC".ToLowerInvariant(), "car"), (t.Rows[0].Get("plate"), t.Rows[0].Get("type")));
        Assert.Equal(("xyz", ""), (t.Rows[1].Get("plate"), t.Rows[1].Get("type")));
        Assert.Equal("", t.Rows[1].Get("nonexistent"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n \n")]
    [InlineData("a,b\n\"never closed,1")]
    public void An_empty_file_or_an_unclosed_quote_is_refused(string text) => Assert.Throws<FormatException>(() => Csv.Parse(text));
}

[Collection("db")]
public class RegistryFlowTests(TestApp app)
{
    private static string WaId() => "+9233" + Random.Shared.NextInt64(10_000_000, 99_999_999);
    private static string Plate() => "T" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

    private static async Task<ImportReport> Import(ApiSession who, string kind, string csv, bool dryRun = false)
    {
        var res = await who.Client.PostAsync($"/directory/import?kind={kind}&dryRun={dryRun.ToString().ToLowerInvariant()}", new StringContent(csv, Encoding.UTF8, "text/csv"));
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<ImportReport>())!;
    }

    private const string Houses = "sector,street,block,house,plot_size,category,status\n,,Block B,House 7,200,A,occupied\nSector Z,Street 3,Block C,House 8,120.5,B,vacant\n";

    // OR-08 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_vehicle_is_recorded_for_a_house_and_the_same_plate_cannot_be_entered_twice()
    {
        var s = await Society.CreateAsync(app);
        var plate = Plate();
        var res = await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(plate.ToLowerInvariant(), "car", s.HouseId, "S-100", null, "Grey Corolla"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var v = (await res.Content.ReadFromJsonAsync<VehicleDto>())!;
        Assert.Equal((plate, "car", s.HouseId, "active"), (v.Plate, v.Type, v.HouseId, v.Status));
        Assert.Contains("House 1", v.HouseLabel);

        // same car, written differently
        var dup = await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle($"{plate[..2]}-{plate[2..]}", "car", null, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("duplicate_plate", await dup.Content.ReadAsStringAsync());

        // another society may have the same plate
        var other = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.Created, (await other.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(plate, "car", null, null, null, null))).StatusCode);
    }

    [Fact]
    public async Task Vehicles_are_searched_by_plate_in_any_format_or_sticker_and_edited_and_checked()
    {
        var s = await Society.CreateAsync(app);
        var plate = Plate();
        var v = (await (await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(plate, "motorbike", s.HouseId, "ST-" + plate, null, null))).Content.ReadFromJsonAsync<VehicleDto>())!;

        Assert.Equal([v.Id], (await s.Supervisor.Client.GetFromJsonAsync<List<VehicleDto>>($"/vehicles?q={plate[..2].ToLowerInvariant()}-{plate[2..].ToLowerInvariant()}"))!.Select(x => x.Id));
        Assert.Equal([v.Id], (await s.Supervisor.Client.GetFromJsonAsync<List<VehicleDto>>($"/vehicles?q=st-{plate.ToLowerInvariant()}"))!.Select(x => x.Id));
        Assert.Equal([v.Id], (await s.Supervisor.Client.GetFromJsonAsync<List<VehicleDto>>($"/vehicles?houseId={s.HouseId}"))!.Select(x => x.Id));

        var edited = await s.Office.Client.PutAsJsonAsync($"/vehicles/{v.Id}", new SaveVehicle(plate, "car", s.HouseId, null, "inactive", null));
        var after = (await edited.Content.ReadFromJsonAsync<VehicleDto>())!;
        Assert.Equal(("car", "inactive", null), (after.Type, after.Status, after.StickerCode));

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle("-", "car", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(Plate(), "spaceship", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(Plate(), "car", Guid.NewGuid(), null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Office.Client.PutAsJsonAsync($"/vehicles/{Guid.NewGuid()}", new SaveVehicle(Plate(), null, null, null, null, null))).StatusCode);
    }

    [Fact]
    public async Task A_sticker_belongs_to_one_vehicle()
    {
        var s = await Society.CreateAsync(app);
        await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(Plate(), "car", null, "GATE-9", null, null));
        var second = await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(Plate(), "car", null, "gate-9", null, null));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("duplicate_sticker", await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_staff_who_manage_houses_change_the_directory()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Technician.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(Plate(), "car", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsJsonAsync("/daily-help", new SaveHelp("X", null, null, null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/vehicles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.PostAsync("/directory/import?kind=houses", new StringContent(Houses, Encoding.UTF8, "text/csv"))).StatusCode);
    }

    // OR-09 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Daily_help_is_recorded_with_a_masked_id_allowed_times_and_several_houses()
    {
        var s = await Society.CreateAsync(app);
        var other = await Import(s.Manager, "houses", Houses);
        Assert.Equal(2, other.Created);
        var houses = (await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.SetupEndpoints.HouseDto>>("/houses"))!.Select(h => h.Id).ToList();

        var res = await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Rukhsana", "0300 5551234", "35202-1234567-1", "maid", null, ["mon", "wed", "fri"], "08:00", "14:30", houses));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var h = (await res.Content.ReadFromJsonAsync<HelpDto>())!;
        Assert.Equal(("Rukhsana", "+923005551234", "maid"), (h.Name, h.Phone, h.Role));
        Assert.Equal(["mon", "wed", "fri"], h.AllowedDays);
        Assert.Equal(("08:00", "14:30"), (h.AllowedFrom, h.AllowedTo));
        Assert.Equivalent(houses, h.HouseIds);
        Assert.EndsWith("5671", h.IdNumberMasked);
        Assert.DoesNotContain("35202", h.IdNumberMasked);
        Assert.DoesNotContain("1234567", h.IdNumberMasked);
        Assert.Contains("•", h.IdNumberMasked);

        var stored = await Scalar<string>("SELECT id_number_masked FROM daily_help WHERE id = @id", ("id", h.Id));
        Assert.DoesNotContain("1234567", stored!); // not even the database has the full number
    }

    private async Task<T?> Scalar<T>(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    [Fact]
    public async Task Daily_help_input_is_checked_and_the_houses_can_be_changed_later()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp(" ", null, null, null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("A", "12", null, null, null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("A", null, null, "wizard", null, null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("A", null, null, null, null, ["funday"], null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("A", null, null, null, null, null, "17:00", "08:00", null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("A", null, null, null, null, null, "late", null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("A", null, null, null, null, null, null, null, [Guid.NewGuid()]))).StatusCode);

        var h = (await (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Bashir", null, "4210112345671", "driver", null, null, null, null, [s.HouseId]))).Content.ReadFromJsonAsync<HelpDto>())!;
        var moved = (await (await s.Office.Client.PutAsJsonAsync($"/daily-help/{h.Id}", new SaveHelp("Bashir", null, null, "driver", "inactive", null, null, null, []))).Content.ReadFromJsonAsync<HelpDto>())!;
        Assert.Empty(moved.HouseIds);
        Assert.Equal(("inactive", h.IdNumberMasked), (moved.Status, moved.IdNumberMasked)); // an edit with no new ID keeps the old mask
        Assert.Equal(HttpStatusCode.NotFound, (await s.Office.Client.PutAsJsonAsync($"/daily-help/{Guid.NewGuid()}", new SaveHelp("Z", null, null, null, null, null, null, null, null))).StatusCode);
    }

    [Fact]
    public async Task A_photo_can_be_added_to_a_daily_help_record_and_only_an_image_is_accepted()
    {
        var s = await Society.CreateAsync(app);
        var h = (await (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Nasreen", null, null, "cook", null, null, null, null, null))).Content.ReadFromJsonAsync<HelpDto>())!;
        var ok = await s.Office.Client.PostAsync($"/daily-help/{h.Id}/photo", new MultipartFormDataContent { { new ByteArrayContent(WaChat.Jpeg(4)), "file", "p.jpg" } });
        Assert.StartsWith("http", (await ok.Content.ReadFromJsonAsync<HelpDto>())!.PhotoUrl);
        var bad = await s.Office.Client.PostAsync($"/daily-help/{h.Id}/photo", new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("not an image at all, just text")), "file", "p.jpg" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsync($"/daily-help/{h.Id}/photo", new MultipartFormDataContent { { new ByteArrayContent([]), "file", "p.jpg" } })).StatusCode);
    }

    // OR-11 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Search_finds_houses_people_vehicles_and_help_and_answers_quickly_with_thousands_of_records()
    {
        var s = await Society.CreateAsync(app);
        var phone = WaId();
        await Import(s.Manager, "residents", $"house,name,phone,relation\nBlock A/House 1,Zubair Hashmi,{phone},family\n");
        var plate = Plate();
        await s.Office.Client.PostAsJsonAsync("/vehicles", new SaveVehicle(plate, "car", s.HouseId, null, null, null));
        await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Zainab Bibi", null, null, "maid", null, null, null, null, [s.HouseId]));

        var byName = (await s.Supervisor.Client.GetFromJsonAsync<SearchResult>("/directory/search?q=zub"))!;
        Assert.Equal("Zubair Hashmi", Assert.Single(byName.People).DisplayName);
        Assert.Equal([s.HouseId], Assert.Single(byName.People).HouseIds);
        Assert.Equal(plate, Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<SearchResult>($"/directory/search?q={plate.ToLowerInvariant()}"))!.Vehicles).Plate);
        Assert.Equal("Zainab Bibi", Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<SearchResult>("/directory/search?q=zainab"))!.Help).Name);
        Assert.Contains("House 1", Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<SearchResult>("/directory/search?q=house 1"))!.Houses).Label);
        Assert.Equal(phone, Assert.Single((await s.Supervisor.Client.GetFromJsonAsync<SearchResult>($"/directory/search?q={phone[^7..]}"))!.People).Phone);
        var nothing = (await s.Supervisor.Client.GetFromJsonAsync<SearchResult>("/directory/search?q=x"))!; // too short to search
        Assert.Empty(nothing.People.Concat<object>(nothing.Vehicles).Concat(nothing.Help));
        Assert.Empty((await s.Supervisor.Client.GetFromJsonAsync<SearchResult>("/directory/search?q=%25%25"))!.People); // a percent sign is just a character

        await Scalar<object>("""
            INSERT INTO vehicles (id, organization_id, plate, plate_key, type, status)
            SELECT gen_random_uuid(), @o, 'LHR-' || g, 'LHR' || g, 'car', 'active' FROM generate_series(1, 4000) g
            """, ("o", s.Org));
        await s.Supervisor.Client.GetAsync("/directory/search?q=lhr-39"); // warm up
        var watch = Stopwatch.StartNew();
        var found = (await s.Supervisor.Client.GetFromJsonAsync<SearchResult>("/directory/search?q=lhr-39"))!;
        watch.Stop();
        Assert.NotEmpty(found.Vehicles);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Possible_duplicates_are_flagged_for_staff_and_can_be_dismissed()
    {
        var s = await Society.CreateAsync(app);
        var phone = WaId();
        await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Kausar", phone, null, "maid", null, null, null, null, null));
        var second = (await (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Kausar B", phone, null, "maid", null, null, null, null, null))).Content.ReadFromJsonAsync<HelpDto>())!;
        Assert.Contains("phone_used_by_other_help", second.Warnings);

        await Import(s.Manager, "residents", $"house,name,phone,relation\nBlock A/House 1,Hamid,{phone},family\n");
        var third = (await (await s.Office.Client.PostAsJsonAsync("/daily-help", new SaveHelp("Kausar C", phone, null, "maid", null, null, null, null, null))).Content.ReadFromJsonAsync<HelpDto>())!;
        Assert.Contains("phone_belongs_to_a_resident", third.Warnings);

        await Import(s.Manager, "houses", Houses);
        var owner = WaId();
        await Import(s.Manager, "residents", $"house,name,phone,relation\nBlock B/House 7,Moving Man,{owner},owner\nSector Z/Street 3/Block C/House 8,Moving Man,{owner},tenant\n");

        var groups = (await s.Office.Client.GetFromJsonAsync<List<DuplicateGroup>>("/directory/duplicates"))!;
        Assert.Contains(groups, g => g.Kind == "help_same_phone" && g.Key == phone && g.Items.Count == 3);
        Assert.Contains(groups, g => g.Kind == "help_resident_phone" && g.Key == phone);
        var two = Assert.Single(groups, g => g.Kind == "resident_two_houses");
        Assert.Equal(2, two.Items.Count);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Office.Client.PostAsJsonAsync("/directory/duplicates/dismiss", new Dismiss(two.Kind, two.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Office.Client.PostAsJsonAsync("/directory/duplicates/dismiss", new Dismiss(two.Kind, two.Key))).StatusCode); // twice is fine
        Assert.DoesNotContain(await s.Office.Client.GetFromJsonAsync<List<DuplicateGroup>>("/directory/duplicates") ?? [], g => g.Kind == "resident_two_houses");
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Office.Client.PostAsJsonAsync("/directory/duplicates/dismiss", new Dismiss("", ""))).StatusCode);
    }

    // OR-10 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Houses_are_imported_with_their_streets_and_blocks_and_a_second_import_changes_nothing()
    {
        var s = await Society.CreateAsync(app);
        var first = await Import(s.Manager, "houses", Houses);
        Assert.True(first.Issues.Count == 0, string.Join(" | ", first.Issues.Select(i => $"{i.Line} {i.Field} {i.Message}")));
        Assert.Equal((2, 2, 0, 0), (first.Rows, first.Created, first.Updated, first.Issues.Count));

        var again = await Import(s.Manager, "houses", Houses);
        Assert.Equal((0, 0, 2), (again.Created, again.Updated, again.Unchanged));

        var changed = await Import(s.Manager, "houses", Houses.Replace(",200,A,", ",250,A,"));
        Assert.Equal((0, 1, 1), (changed.Created, changed.Updated, changed.Unchanged));
        var all = (await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.SetupEndpoints.HouseDto>>("/houses"))!;
        Assert.Equal(250m, all.Single(h => h.LocationPath.Length > 0 && h.PlotSizeSqYd == 250m).PlotSizeSqYd);
        Assert.Equal(3, all.Count); // the society's own house plus the two imported
    }

    [Fact]
    public async Task Bad_rows_are_reported_by_line_and_the_good_rows_still_go_in()
    {
        var s = await Society.CreateAsync(app);
        var csv = "sector,street,block,house,plot_size,category,status\n" +
                  ",,Block D,House 1,100,A,occupied\n" +       // line 2: fine
                  ",,Block D,,100,A,occupied\n" +              // line 3: no house
                  ",,Block D,House 2,big,A,occupied\n" +       // line 4: plot size not a number
                  ",,Block D,House 3,100,A,haunted\n" +        // line 5: status
                  ",,Block D,House 4,100,A,vacant\n";          // line 6: fine
        var report = await Import(s.Manager, "houses", csv);
        Assert.Equal((5, 2), (report.Rows, report.Created));
        Assert.Equal([(3, "house"), (4, "plot_size"), (5, "status")], report.Issues.Select(i => (i.Line, i.Field)));
    }

    [Fact]
    public async Task A_dry_run_reports_what_would_happen_and_saves_nothing()
    {
        var s = await Society.CreateAsync(app);
        var dry = await Import(s.Manager, "houses", Houses, dryRun: true);
        Assert.Equal((true, 2), (dry.DryRun, dry.Created));
        Assert.Single((await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.SetupEndpoints.HouseDto>>("/houses"))!); // only the society's own
        var real = await Import(s.Manager, "houses", Houses);
        Assert.Equal(2, real.Created);
    }

    [Fact]
    public async Task Residents_are_imported_with_phones_made_residents_linked_to_houses_and_not_duplicated()
    {
        var s = await Society.CreateAsync(app);
        await Import(s.Manager, "houses", Houses);
        var owner = WaId();
        var tenant = WaId();
        var csv = "house,name,phone,email,relation,from_date\n" +
                  $"Block B/House 7,\"Khan, Ali\",{owner},,owner,2025-01-15\n" +
                  $"Block B/House 7,Sara Khan,{WaId()},sara.k@example.test,family,\n" +
                  $"Block B/House 7,Second Owner,{WaId()},,owner,\n" +        // line 4: the house has an owner
                  $"Block B/House 7,Tenant,{tenant},,tenant,\n" +
                  $"Block B/House 99,Nobody,{WaId()},,owner,\n" +             // line 6: no such house
                  $"Block B/House 7,Bad Phone,12,,family,\n" +                // line 7
                  $"Block B/House 7,Bad Relation,{WaId()},,cousin,\n";        // line 8
        var report = await Import(s.Manager, "residents", csv);
        Assert.Equal((7, 3), (report.Rows, report.Created));
        Assert.Equal([(4, "relation"), (6, "house"), (7, "phone"), (8, "relation")], report.Issues.Select(i => (i.Line, i.Field)));

        var again = await Import(s.Manager, "residents", csv);
        Assert.Equal((0, 0), (again.Created, again.Updated));
        Assert.Equal(3, again.Unchanged);

        var members = (await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.MemberDto>>("/members"))!;
        var ali = Assert.Single(members, m => m.Phone == owner);
        Assert.Equal("Khan, Ali", ali.DisplayName);
        Assert.Equal(UrbanFix.SharedKernel.Role.Resident, ali.Role);
        Assert.EndsWith(".invalid", ali.Email); // no email given: a placeholder, sign-in is by phone code
        Assert.Contains(members, m => m.Email == "sara.k@example.test");
    }

    [Fact]
    public async Task A_resident_already_in_the_system_by_phone_is_linked_not_duplicated()
    {
        var s = await Society.CreateAsync(app);
        await Import(s.Manager, "houses", Houses);
        var phone = WaId();
        await Import(s.Manager, "residents", $"house,name,phone,relation\nBlock B/House 7,Pat,{phone},family\n");
        var moved = await Import(s.Manager, "residents", $"house,name,phone,relation\nSector Z/Street 3/Block C/House 8,Pat,{phone},tenant\n");
        Assert.Equal((0, 1), (moved.Created, moved.Updated));
        Assert.Equal(1, (await s.Manager.Client.GetFromJsonAsync<List<UrbanFix.Api.Endpoints.AdminEndpoints.MemberDto>>("/members"))!.Count(m => m.Phone == phone));
    }

    [Fact]
    public async Task Vehicles_and_daily_help_are_imported_matched_by_plate_and_phone_and_stay_idempotent()
    {
        var s = await Society.CreateAsync(app);
        await Import(s.Manager, "houses", Houses);
        var p1 = Plate();
        var p2 = Plate();
        var vehicles = "plate,type,house,sticker_code,status\n" +
                       $"{p1},car,Block B/House 7,ST-{p1},active\n" +
                       $"{p2.ToLowerInvariant()},motorbike,,,\n" +
                       $"{p1.Insert(2, "-")},car,Block B/House 7,ST-{p1},active\n" +         // line 4: same car as line 2: unchanged
                       $"{Plate()},boat,,,\n" +                                              // line 5: type
                       $"{Plate()},car,,ST-{p1},\n";                                         // line 6: sticker taken
        var v = await Import(s.Manager, "vehicles", vehicles);
        Assert.Equal((2, 1), (v.Created, v.Unchanged));
        Assert.Equal([(5, "type"), (6, "sticker_code")], v.Issues.Select(i => (i.Line, i.Field)));
        Assert.Equal(2, (await s.Manager.Client.GetFromJsonAsync<List<VehicleDto>>("/vehicles"))!.Count(x => x.Plate == p1 || x.Plate == p2));

        var phone = WaId();
        var help = "name,phone,id_number,role,days,from,to,houses\n" +
                   $"Rukhsana,{phone},35202-1234567-1,maid,\"mon,wed,fri\",08:00,14:00,Block B/House 7;Sector Z/Street 3/Block C/House 8\n" +
                   "Bashir,,,driver,weekdays,,,\n" +
                   $"Bad Days,,,maid,someday,,,\n" +                                          // line 4
                   $"Bad House,,,maid,,,,Block Q/House 1\n";                                  // line 5
        var h = await Import(s.Manager, "help", help);
        Assert.Equal((2, 0), (h.Created, h.Updated));
        Assert.Equal([(4, "days"), (5, "houses")], h.Issues.Select(i => (i.Line, i.Field)));
        var again = await Import(s.Manager, "help", help);
        Assert.Equal((0, 0, 2), (again.Created, again.Updated, again.Unchanged));

        var found = (await s.Manager.Client.GetFromJsonAsync<List<HelpDto>>($"/daily-help?q=rukh"))!.Single();
        Assert.Equal(2, found.HouseIds.Count);
        Assert.DoesNotContain("1234567", found.IdNumberMasked);
    }

    [Fact]
    public async Task A_file_with_missing_columns_unknown_kind_or_broken_quotes_is_refused_clearly()
    {
        var s = await Society.CreateAsync(app);
        var missing = await Import(s.Manager, "residents", "name,phone\nA,+923001234567\n");
        Assert.Equal(["house", "relation"], missing.Issues.Select(i => i.Field).Order());
        Assert.Equal(0, missing.Created);

        var unknown = await s.Manager.Client.PostAsync("/directory/import?kind=cars", new StringContent("a\n1", Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("unknown_kind", await unknown.Content.ReadAsStringAsync());
        var broken = await s.Manager.Client.PostAsync("/directory/import?kind=vehicles", new StringContent("plate\n\"oops", Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);
        Assert.Contains("invalid_csv", await broken.Content.ReadAsStringAsync());
    }
}
