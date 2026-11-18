using System.Net;
using System.Net.Http.Json;
using UrbanFix.Infrastructure.Platform;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.GateEndpoints;

namespace UrbanFix.Tests;

public class GateRulesTests
{
    private static GatePass Pass(DateTimeOffset from, DateTimeOffset to, int? max = null, int used = 0, string status = "active") =>
        new() { ValidFrom = from, ValidTo = to, MaxUses = max, UsedCount = used, Status = status };

    [Fact]
    public void A_pass_is_valid_only_inside_its_window_with_uses_left_and_not_cancelled()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(GateRules.PassState.Valid, GateRules.Check(Pass(now.AddHours(-1), now.AddHours(1)), now));
        Assert.Equal(GateRules.PassState.NotYetValid, GateRules.Check(Pass(now.AddHours(1), now.AddHours(2)), now));
        Assert.Equal(GateRules.PassState.Expired, GateRules.Check(Pass(now.AddHours(-2), now.AddHours(-1)), now));
        Assert.Equal(GateRules.PassState.UsedUp, GateRules.Check(Pass(now.AddHours(-1), now.AddHours(1), max: 1, used: 1), now));
        Assert.Equal(GateRules.PassState.Cancelled, GateRules.Check(Pass(now.AddHours(-1), now.AddHours(1), status: "cancelled"), now));
        Assert.True(GateRules.IsUsable(Pass(now.AddHours(-1), now.AddHours(1), max: 2, used: 1), now));
    }

    [Theory]
    [InlineData("px7-qk2", "PX7QK2")]
    [InlineData(" a b c 1 ", "ABC1")]
    [InlineData(null, "")]
    public void A_typed_code_is_tidied_to_the_stored_form(string? input, string expected) => Assert.Equal(expected, GateRules.NormalizeCode(input));

    [Theory]
    [InlineData(" e2:00-1234 ", "E2001234")]
    [InlineData("rf-tag-001", "RFTAG001")]
    [InlineData(null, "")]
    public void An_rfid_tag_is_tidied_so_small_formatting_differences_still_match(string? input, string expected) =>
        Assert.Equal(expected, GateRules.NormalizeTag(input));

    [Fact]
    public void A_generated_code_is_six_characters_from_the_no_lookalike_alphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = GateRules.NewPassCode();
            Assert.Equal(6, code.Length);
            Assert.DoesNotContain(code, c => "OISZB012".Contains(c));
            Assert.Equal(code, GateRules.NormalizeCode(code)); // already in stored form
        }
    }

    [Fact]
    public void Inside_keeps_a_subject_only_when_its_latest_crossing_is_an_allowed_entry()
    {
        var t0 = DateTimeOffset.UtcNow;
        var car = Guid.NewGuid();
        var help = Guid.NewGuid();
        var events = new List<GateEvent>
        {
            new() { VehicleId = car, Direction = "in", Decision = "allowed", RecordedAt = t0 },
            new() { VehicleId = car, Direction = "out", Decision = "allowed", RecordedAt = t0.AddMinutes(30) }, // left again
            new() { HelpId = help, Direction = "in", Decision = "allowed", RecordedAt = t0.AddMinutes(10) },    // still in
            new() { SubjectName = "Gatecrasher", Direction = "in", Decision = "denied", RecordedAt = t0 },       // turned away
        };

        var inside = GateRules.Inside(events).ToList();
        Assert.Single(inside);
        Assert.Equal(help, inside[0].HelpId);
    }
}

[Collection("db")]
public class GateFlowTests(TestApp app)
{
    private static string Plate() => "G" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

    /// <summary>A society with the gate feature on and a guard on staff.</summary>
    private async Task<(Society S, ApiSession Guard)> GatedAsync()
    {
        var s = await Society.CreateAsync(app);
        var guard = await app.AddMemberAsync(s.Manager, s.Org, Role.Guard);
        var admin = await app.LoginPlatformAdminAsync();
        (await admin.Client.PutAsJsonAsync($"/admin/organizations/{s.Org}/features/{Features.Gate}",
            new UrbanFix.Api.Endpoints.PlatformEndpoints.SetFeature(true))).EnsureSuccessStatusCode();
        return (s, guard);
    }

    [Fact]
    public async Task Gate_endpoints_are_closed_until_the_feature_is_switched_on()
    {
        var s = await Society.CreateAsync(app);
        var guard = await app.AddMemberAsync(s.Manager, s.Org, Role.Guard);
        Assert.Equal(HttpStatusCode.Forbidden, (await guard.Client.GetAsync("/gate/lookup?q=abc")).StatusCode);

        var admin = await app.LoginPlatformAdminAsync();
        (await admin.Client.PutAsJsonAsync($"/admin/organizations/{s.Org}/features/{Features.Gate}",
            new UrbanFix.Api.Endpoints.PlatformEndpoints.SetFeature(true))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await guard.Client.GetAsync("/gate/lookup?q=abc")).StatusCode);
    }

    [Fact]
    public async Task A_manager_can_switch_gate_security_on_and_off_for_their_own_society()
    {
        var s = await Society.CreateAsync(app);
        var guard = await app.AddMemberAsync(s.Manager, s.Org, Role.Guard);

        // Off by default: closed to the guard until someone turns it on.
        Assert.Equal(HttpStatusCode.Forbidden, (await guard.Client.GetAsync("/gate/lookup?q=abc")).StatusCode);

        // The manager turns it on themselves — no platform admin needed.
        Assert.Equal(HttpStatusCode.OK, (await s.Manager.Client.PutAsJsonAsync("/my/features/gate",
            new UrbanFix.Api.Endpoints.PlatformEndpoints.SetFeature(true))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guard.Client.GetAsync("/gate/lookup?q=abc")).StatusCode);

        // And off again.
        (await s.Manager.Client.PutAsJsonAsync("/my/features/gate",
            new UrbanFix.Api.Endpoints.PlatformEndpoints.SetFeature(false))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await guard.Client.GetAsync("/gate/lookup?q=abc")).StatusCode);
    }

    [Fact]
    public async Task Self_service_feature_toggle_is_limited_to_gate_and_to_managers()
    {
        var s = await Society.CreateAsync(app);

        // A manager may self-provision gate, but nothing else (billing stays platform-only).
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/my/features/billing",
            new UrbanFix.Api.Endpoints.PlatformEndpoints.SetFeature(true))).StatusCode);

        // A guard holds gate.log but not gate.manage, so cannot flip the feature at all.
        var guard = await app.AddMemberAsync(s.Manager, s.Org, Role.Guard);
        Assert.Equal(HttpStatusCode.Forbidden, (await guard.Client.PutAsJsonAsync("/my/features/gate",
            new UrbanFix.Api.Endpoints.PlatformEndpoints.SetFeature(true))).StatusCode);
    }

    // RFID / UHF sticker + boom barrier (GT-05) ---------------------------------------------------------------
    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    /// <summary>A reader POSTs a tag read, authenticating with its own key (no user token), as the hardware will.</summary>
    private async Task<HttpResponseMessage> ScanAsync(string key, string? tag)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/gate/scan") { Content = JsonContent.Create(new ScanRequest(tag)) };
        req.Headers.Add("X-Gate-Key", key);
        return await app.CreateClient().SendAsync(req);
    }

    private async Task<HttpResponseMessage> CommandsAsync(string key)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/gate/commands");
        req.Headers.Add("X-Gate-Key", key);
        return await app.CreateClient().SendAsync(req);
    }

    /// <summary>A gated society with a gate, an entry reader and a guard.</summary>
    private async Task<(Society S, ApiSession Guard, GateDto Gate, GateReaderKeyDto Reader)> RfidGateAsync(string direction = "in")
    {
        var (s, guard) = await GatedAsync();
        var gate = await Ok<GateDto>(s.Manager.Client.PostAsJsonAsync("/gates", new SaveGate("Main", true)));
        var reader = await Ok<GateReaderKeyDto>(s.Manager.Client.PostAsJsonAsync("/gate/readers",
            new SaveReader(gate.Id, direction == "in" ? "Entry" : "Exit", direction)));
        return (s, guard, gate, reader);
    }

    [Fact]
    public async Task A_registered_resident_car_opens_the_barrier_automatically_and_is_logged_once()
    {
        var (s, guard, _, reader) = await RfidGateAsync();
        var plate = Plate();
        (await s.Office.Client.PostAsJsonAsync("/vehicles",
            new UrbanFix.Api.Endpoints.RegistryEndpoints.SaveVehicle(plate, "car", s.HouseId, "RF-TAG-001", "active", null))).EnsureSuccessStatusCode();

        var scan = await Ok<ScanResult>(ScanAsync(reader.ApiKey, "rf-tag-001")); // matched case-insensitively
        Assert.Equal("open", scan.Action);
        Assert.Equal(plate, scan.Subject);

        var events = await Ok<List<EventDto>>(guard.Client.GetAsync("/gate/events?limit=20"));
        var mine = events.Where(e => e.VehiclePlate == plate).ToList();
        Assert.Single(mine);
        Assert.Equal(("in", "allowed", "rfid"), (mine[0].Direction, mine[0].Decision, mine[0].Source));

        // A repeat read moments later still opens, but does not double-log the same crossing.
        Assert.Equal("open", (await Ok<ScanResult>(ScanAsync(reader.ApiKey, "RF-TAG-001"))).Action);
        var after = await Ok<List<EventDto>>(guard.Client.GetAsync("/gate/events?limit=20"));
        Assert.Single(after.Where(e => e.VehiclePlate == plate));
    }

    [Fact]
    public async Task An_unknown_tag_is_held_for_a_guard_and_opens_once_approved()
    {
        var (s, guard, _, reader) = await RfidGateAsync();

        var scan = await Ok<ScanResult>(ScanAsync(reader.ApiKey, "STRANGER-9"));
        Assert.Equal("hold", scan.Action);
        Assert.NotNull(scan.ApprovalId);
        Assert.Empty(await Ok<List<OpenCommand>>(CommandsAsync(reader.ApiKey))); // barrier stays shut

        var waiting = await Ok<List<GateApprovalDto>>(guard.Client.GetAsync("/gate/waiting"));
        Assert.Contains(waiting, a => a.Id == scan.ApprovalId);

        var decided = await Ok<GateApprovalDto>(guard.Client.PostAsJsonAsync($"/gate/waiting/{scan.ApprovalId}/decide", new Decide(true, null)));
        Assert.Equal("approved", decided.Status);

        // The reader now gets exactly one open.
        Assert.Contains(await Ok<List<OpenCommand>>(CommandsAsync(reader.ApiKey)), c => c.ApprovalId == scan.ApprovalId);
        Assert.Empty(await Ok<List<OpenCommand>>(CommandsAsync(reader.ApiKey)));

        Assert.Contains(await Ok<List<EventDto>>(guard.Client.GetAsync("/gate/events?limit=20")), e => e.Decision == "allowed" && e.Source == "rfid");
    }

    [Fact]
    public async Task A_denied_car_never_opens_the_barrier_and_cannot_be_decided_twice()
    {
        var (s, guard, _, reader) = await RfidGateAsync();
        var scan = await Ok<ScanResult>(ScanAsync(reader.ApiKey, "STRANGER-X"));

        var decided = await Ok<GateApprovalDto>(guard.Client.PostAsJsonAsync($"/gate/waiting/{scan.ApprovalId}/decide", new Decide(false, null)));
        Assert.Equal("denied", decided.Status);
        Assert.Empty(await Ok<List<OpenCommand>>(CommandsAsync(reader.ApiKey)));
        Assert.Equal(HttpStatusCode.Conflict,
            (await guard.Client.PostAsJsonAsync($"/gate/waiting/{scan.ApprovalId}/decide", new Decide(true, null))).StatusCode);
    }

    [Fact]
    public async Task A_reader_key_must_be_valid_and_rotating_it_revokes_the_old_one()
    {
        var (s, _, _, reader) = await RfidGateAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await ScanAsync("grk_not-a-real-key", "x")).StatusCode);

        var rotated = await Ok<GateReaderKeyDto>(s.Manager.Client.PostAsync($"/gate/readers/{reader.Reader.Id}/rotate-key", null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ScanAsync(reader.ApiKey, "x")).StatusCode); // the old key is dead
        Assert.Equal(HttpStatusCode.OK, (await ScanAsync(rotated.ApiKey, "x")).StatusCode);           // the new key works
    }

    [Fact]
    public async Task Only_a_manager_can_set_up_readers()
    {
        var (_, guard, gate, _) = await RfidGateAsync();
        Assert.Equal(HttpStatusCode.Forbidden,
            (await guard.Client.PostAsJsonAsync("/gate/readers", new SaveReader(gate.Id, "Sneaky", "in"))).StatusCode);
    }

    [Fact]
    public async Task A_generated_sticker_code_is_unique_per_car_and_opens_the_barrier_for_it()
    {
        var (s, _, _, reader) = await RfidGateAsync();
        var plate = Plate();
        var vehicle = await Ok<UrbanFix.Api.Endpoints.RegistryEndpoints.VehicleDto>(s.Office.Client.PostAsJsonAsync("/vehicles",
            new UrbanFix.Api.Endpoints.RegistryEndpoints.SaveVehicle(plate, "car", s.HouseId, null, "active", null)));

        var tagged = await Ok<UrbanFix.Api.Endpoints.RegistryEndpoints.VehicleDto>(s.Office.Client.PostAsync($"/vehicles/{vehicle.Id}/sticker", null));
        Assert.StartsWith("UF", tagged.StickerCode);

        var scan = await Ok<ScanResult>(ScanAsync(reader.ApiKey, tagged.StickerCode!.ToLowerInvariant())); // reader may send any case
        Assert.Equal("open", scan.Action);
        Assert.Equal(plate, scan.Subject);
    }

    [Fact]
    public async Task A_resident_cannot_touch_the_gate_and_a_guard_cannot_configure_checkpoints()
    {
        var (s, guard) = await GatedAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/gate/lookup?q=abc")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsJsonAsync("/gate/events", Record("in"))).StatusCode);
        // gate.manage is the manager's, not the guard's
        Assert.Equal(HttpStatusCode.Forbidden, (await guard.Client.PostAsJsonAsync("/gates", new SaveGate("North Gate", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Manager.Client.PostAsJsonAsync("/gates", new SaveGate("North Gate", null))).StatusCode);
    }

    [Fact]
    public async Task A_registered_car_is_found_by_plate_in_any_format_with_its_house()
    {
        var (s, guard) = await GatedAsync();
        var plate = Plate();
        (await s.Office.Client.PostAsJsonAsync("/vehicles",
            new UrbanFix.Api.Endpoints.RegistryEndpoints.SaveVehicle(plate, "car", s.HouseId, null, null, null))).EnsureSuccessStatusCode();

        var hit = await guard.Client.GetFromJsonAsync<LookupResult>($"/gate/lookup?q={plate[..2].ToLowerInvariant()}-{plate[2..].ToLowerInvariant()}");
        Assert.Single(hit!.Vehicles);
        Assert.Equal(s.HouseId, hit.Vehicles[0].HouseId);
        Assert.Contains("House 1", hit.Vehicles[0].HouseLabel);
    }

    [Fact]
    public async Task An_entry_then_an_exit_is_logged_and_who_is_inside_updates()
    {
        var (s, guard) = await GatedAsync();
        var plate = Plate();

        var entry = await guard.Client.PostAsJsonAsync("/gate/events", Record("in", name: "Courier", plate: plate));
        Assert.Equal(HttpStatusCode.Created, entry.StatusCode);
        var ev = (await entry.Content.ReadFromJsonAsync<EventDto>())!;
        Assert.Equal(("in", "allowed"), (ev.Direction, ev.Decision));
        Assert.NotNull(ev.RecordedBy);

        Assert.Single((await guard.Client.GetFromJsonAsync<List<EventDto>>("/gate/inside"))!, e => e.SubjectName == "Courier");

        // Same car leaving (same plate key pairs the exit with the entry).
        (await guard.Client.PostAsJsonAsync("/gate/events", Record("out", name: "Courier", plate: plate))).EnsureSuccessStatusCode();
        Assert.DoesNotContain((await guard.Client.GetFromJsonAsync<List<EventDto>>("/gate/inside"))!, e => e.SubjectName == "Courier");

        Assert.True((await guard.Client.GetFromJsonAsync<List<EventDto>>("/gate/events"))!.Count >= 2);
    }

    [Fact]
    public async Task A_pass_authorises_a_guest_and_an_allowed_entry_spends_one_use()
    {
        var (s, guard) = await GatedAsync();
        var created = await guard.Client.PostAsJsonAsync("/gate/passes",
            new SavePass("Uncle Ali", null, Plate(), s.HouseId, "guest", "Dinner", null, null, 1));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pass = (await created.Content.ReadFromJsonAsync<PassDto>())!;
        Assert.Equal(6, pass.Code.Length);
        Assert.Equal("Valid", pass.State);

        // Guard finds it by code
        var lookup = await guard.Client.GetFromJsonAsync<LookupResult>($"/gate/lookup?q={pass.Code.ToLowerInvariant()}");
        Assert.Single(lookup!.Passes, p => p.Id == pass.Id && p.State == "Valid");

        // Enters on the pass → the single use is spent, next check is UsedUp
        (await guard.Client.PostAsJsonAsync("/gate/events",
            Record("in", name: "Uncle Ali", passId: pass.Id, subjectType: "visitor", houseId: s.HouseId))).EnsureSuccessStatusCode();
        var after = (await guard.Client.GetFromJsonAsync<List<PassDto>>($"/gate/passes?q={pass.Code}"))!.Single();
        Assert.Equal((1, "UsedUp"), (after.UsedCount, after.State));
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        var (_, guard) = await GatedAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await guard.Client.PostAsJsonAsync("/gate/events", Record("sideways"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guard.Client.PostAsJsonAsync("/gate/events",
            new RecordEvent("in", "visitor", null, null, Guid.NewGuid(), null, null, null, "x", null, null, null, null))).StatusCode); // unknown house
        Assert.Equal(HttpStatusCode.BadRequest, (await guard.Client.PostAsJsonAsync("/gate/passes",
            new SavePass("", null, null, Guid.NewGuid(), null, null, null, null, null))).StatusCode); // no name, unknown house
    }

    [Fact]
    public async Task A_resident_pre_authorises_a_visitor_for_their_own_house_but_not_another()
    {
        var (s, guard) = await GatedAsync();
        var created = await s.Resident.Client.PostAsJsonAsync("/my/gate-passes",
            new SavePass("Cousin Sara", null, Plate(), s.HouseId, "guest", null, null, null, null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var pass = (await created.Content.ReadFromJsonAsync<PassDto>())!;
        Assert.Equal(("active", "Valid"), (pass.Status, pass.State));

        Assert.Contains((await s.Resident.Client.GetFromJsonAsync<List<PassDto>>("/my/gate-passes"))!, p => p.Id == pass.Id);
        // The guard at the gate finds the resident's pass by its code.
        Assert.Single((await guard.Client.GetFromJsonAsync<LookupResult>($"/gate/lookup?q={pass.Code}"))!.Passes, p => p.Id == pass.Id);

        // Not for a house that is not theirs.
        var other = await s.Resident.Client.PostAsJsonAsync("/my/gate-passes", new SavePass("X", null, null, Guid.NewGuid(), null, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Fact]
    public async Task A_guard_asks_the_resident_and_an_approval_lets_the_visitor_in()
    {
        var (s, guard) = await GatedAsync();
        var req = await guard.Client.PostAsJsonAsync("/gate/visit-requests", new VisitRequest(s.HouseId, "Delivery Rider", null, null, "delivery", "Food order"));
        Assert.Equal(HttpStatusCode.Created, req.StatusCode);
        var pass = (await req.Content.ReadFromJsonAsync<PassDto>())!;
        Assert.Equal(("pending", "Pending"), (pass.Status, pass.State));

        Assert.Single((await s.Resident.Client.GetFromJsonAsync<List<PassDto>>("/my/gate-visit-requests"))!, p => p.Id == pass.Id);

        var decided = await s.Resident.Client.PostAsJsonAsync($"/my/gate-visit-requests/{pass.Id}/decide", new Decide(true, null));
        Assert.Equal(HttpStatusCode.OK, decided.StatusCode);
        var after = (await decided.Content.ReadFromJsonAsync<PassDto>())!;
        Assert.Equal(("active", "Valid"), (after.Status, after.State));

        // The guard now sees it as a valid pass.
        Assert.Single((await guard.Client.GetFromJsonAsync<LookupResult>($"/gate/lookup?q={pass.Code}"))!.Passes, p => p.Id == pass.Id && p.State == "Valid");

        // It cannot be decided twice.
        Assert.Equal(HttpStatusCode.Conflict, (await s.Resident.Client.PostAsJsonAsync($"/my/gate-visit-requests/{pass.Id}/decide", new Decide(false, "x"))).StatusCode);
    }

    [Fact]
    public async Task A_neighbour_cannot_see_or_decide_another_houses_request_and_a_denial_is_not_usable()
    {
        var (s, guard) = await GatedAsync();
        var pass = (await (await guard.Client.PostAsJsonAsync("/gate/visit-requests", new VisitRequest(s.HouseId, "Stranger", null, null, null, null))).Content.ReadFromJsonAsync<PassDto>())!;

        // The neighbour is a resident of no house here: nothing to see, nothing to decide.
        Assert.Empty((await s.Neighbour.Client.GetFromJsonAsync<List<PassDto>>("/my/gate-visit-requests"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Neighbour.Client.PostAsJsonAsync($"/my/gate-visit-requests/{pass.Id}/decide", new Decide(true, null))).StatusCode);

        var denied = (await (await s.Resident.Client.PostAsJsonAsync($"/my/gate-visit-requests/{pass.Id}/decide", new Decide(false, "not expecting anyone"))).Content.ReadFromJsonAsync<PassDto>())!;
        Assert.Equal(("denied", "Denied"), (denied.Status, denied.State));
    }

    private static RecordEvent Record(string direction, string subjectType = "visitor", string? name = null, string? plate = null, Guid? passId = null, Guid? houseId = null) =>
        new(direction, subjectType, null, houseId, null, null, passId, null, name, null, plate, null, null);
}
