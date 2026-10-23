using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.AuthEndpoints;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Tests;

public class PhoneAndPolicyUnitTests
{
    [Theory]
    [InlineData("0300 1234567", "+923001234567")]
    [InlineData("+92 300 1234567", "+923001234567")]
    [InlineData("0092-300-1234567", "+923001234567")]
    [InlineData("923001234567", "+923001234567")]
    [InlineData("+44 7700 900123", "+447700900123")]
    [InlineData("12345", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Phone_numbers_are_stored_in_international_form(string? input, string? expected) => Assert.Equal(expected, PhoneNumber.Normalize(input));

    [Fact]
    public void Policy_survives_a_round_trip_through_the_settings_json_and_keeps_other_settings()
    {
        var policy = new OrgPolicy(UnknownSenderPolicy.Guest, false, true, 5, 2);
        var json = policy.WriteInto("""{"theme":"green"}""");
        Assert.Equal(policy, OrgPolicy.From(json));
        Assert.Contains("green", json);
        Assert.Equal(OrgPolicy.Default, OrgPolicy.From("{}"));
        Assert.Equal(OrgPolicy.Default, OrgPolicy.From("not json"));
        Assert.True(OrgPolicy.Default.ClosureNeedsResident); // closing needs the resident unless the organization changes it
        Assert.False(OrgPolicy.Default.AutoConfirm);
    }

    [Theory]
    [InlineData("guest", 3, 1, null)]
    [InlineData("nonsense", 3, 1, "unknown_sender_policy")]
    [InlineData("guest", 0, 1, "auto_confirm_days_out_of_range")]
    [InlineData("guest", 3, 3, "reminder_must_come_before_close")]
    public void Policy_validation(string sender, int days, int reminder, string? error) =>
        Assert.Equal(error, new OrgPolicy(sender, true, true, days, reminder).Validate());
}

[Collection("db")]
public class PhoneOtpTests(TestApp app)
{
    private RecordingSmsSender Sms => app.Services.GetRequiredService<RecordingSmsSender>();
    private static string NewPhone() => "+9230" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> AddResident(Society s, string phone, Role role = Role.Resident)
    {
        var email = $"{role}-{Guid.NewGuid():N}@x.test".ToLowerInvariant();
        var res = await s.Manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest(email, "Phone user", TestSecrets.UserPassword, role, null, null, phone));
        res.EnsureSuccessStatusCode();
        return email;
    }

    private Task<HttpResponseMessage> Request(string phone, HttpClient? client = null) => (client ?? app.CreateClient()).PostAsJsonAsync("/auth/otp/request", new OtpRequestBody(phone));
    private Task<HttpResponseMessage> Verify(string phone, string code, HttpClient? client = null) => (client ?? app.CreateClient()).PostAsJsonAsync("/auth/otp/verify", new OtpVerifyBody(phone, code, null));

    [Fact]
    public async Task A_resident_signs_in_with_the_code_sent_to_their_phone_and_can_use_it_once()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        await AddResident(s, phone);

        Assert.Equal(HttpStatusCode.Accepted, (await Request(phone)).StatusCode);
        var code = Sms.LastCode(phone)!;
        Assert.Matches(@"^\d{6}$", code);

        var ok = await Verify(phone, code);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var login = (await ok.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.Equal((s.Org, false), (login.OrganizationId, login.TotpEnrollmentRequired));

        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/complaints")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(phone, code)).StatusCode); // single use
    }

    [Fact]
    public async Task Local_number_formats_reach_the_same_resident()
    {
        var s = await Society.CreateAsync(app);
        var digits = Random.Shared.NextInt64(10_000_000, 99_999_999);
        await AddResident(s, $"+9230{digits}");
        Assert.Equal(HttpStatusCode.Accepted, (await Request($"030{digits}")).StatusCode);
        Assert.NotNull(Sms.LastCode($"+9230{digits}"));
    }

    [Fact]
    public async Task Wrong_codes_expire_and_run_out_of_attempts()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        await AddResident(s, phone);
        await Request(phone);
        var code = Sms.LastCode(phone)!;
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(phone, wrong)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(phone, code)).StatusCode); // too many tries kills the code

        var other = NewPhone();
        await AddResident(s, other);
        await Request(other);
        await Sql("UPDATE otp_challenges SET expires_at = now() - interval '1 minute' WHERE phone = @p", ("p", other));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(other, Sms.LastCode(other)!)).StatusCode);
    }

    [Fact]
    public async Task Unknown_numbers_look_the_same_as_known_ones_and_nothing_is_sent()
    {
        var phone = NewPhone();
        Assert.Equal(HttpStatusCode.Accepted, (await Request(phone)).StatusCode);
        Assert.Equal(0, Sms.CountFor(phone));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(phone, "123456")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request("not a phone")).StatusCode);
    }

    [Fact]
    public async Task Staff_cannot_skip_their_second_factor_by_using_a_phone_code()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        await AddResident(s, phone, Role.Supervisor);
        Assert.Equal(HttpStatusCode.Accepted, (await Request(phone)).StatusCode);
        Assert.Equal(0, Sms.CountFor(phone));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Verify(phone, "123456")).StatusCode);
    }

    [Fact]
    public async Task Codes_are_limited_per_phone()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        await AddResident(s, phone);
        Assert.Equal(HttpStatusCode.Accepted, (await Request(phone)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Request(phone)).StatusCode); // 60-second cooldown
        Assert.Equal(1, Sms.CountFor(phone));

        // The limit is the same for a number nobody owns, so it cannot be used to tell them apart.
        var stranger = NewPhone();
        Assert.Equal(HttpStatusCode.Accepted, (await Request(stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Request(stranger)).StatusCode);
    }

    [Fact]
    public async Task A_phone_number_belongs_to_one_person()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        await AddResident(s, phone);
        var res = await s.Manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest($"dup-{Guid.NewGuid():N}@x.test", "Dup", TestSecrets.UserPassword, Role.Resident, null, null, phone));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest($"bad-{Guid.NewGuid():N}@x.test", "Bad", TestSecrets.UserPassword, Role.Resident, null, null, "12"))).StatusCode);
    }

    [Fact]
    public async Task Public_endpoints_throttle_a_noisy_caller_by_address()
    {
        await using var limited = app.WithWebHostBuilder(b => b.UseSetting("RateLimits:PublicPerMinute", "3"));
        var client = limited.CreateClient();
        var phone = NewPhone();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await Request(phone, client)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.Contains("rate_limited", await (await Request(phone, client)).Content.ReadAsStringAsync());
        // The normal application is unaffected.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await Request(NewPhone())).StatusCode);
    }
}

[Collection("db")]
public class ReporterAndConfirmationTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static string NewPhone() => "+9231" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    private static Task<HttpResponseMessage> PhoneIn(Society s, string? phone, string? asset = null, string text = "Tap broken") =>
        s.Office.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, "phone", s.CategoryId, s.HouseId, null, null, null, phone, asset));

    private static async Task<ComplaintDto> Detail(Society s, Guid id) => (await s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!.Complaint;

    private static Task SetPolicy(Society s, OrgPolicy p) => s.Manager.Client.PutAsJsonAsync("/organization/policies", p).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    private static async Task<Guid> ResidentWithPhone(Society s, string phone)
    {
        var email = $"res-{Guid.NewGuid():N}@x.test";
        var res = await s.Manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest(email, "Phone resident", TestSecrets.UserPassword, Role.Resident, null, null, phone));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<AdminEndpoints.MemberDto>())!.UserId;
    }

    // CP-08 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_known_number_is_matched_to_the_resident_and_marked_verified()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        var user = await ResidentWithPhone(s, phone);
        var created = await (await PhoneIn(s, phone)).Content.ReadFromJsonAsync<CreatedDto>();
        var c = await Detail(s, created!.Id);
        Assert.Equal((user, true, phone), (c.ReportedByUserId, c.ReporterVerified, c.ReporterPhone));
        Assert.Equal(ComplaintStatus.New, c.Status);
    }

    [Fact]
    public async Task Unknown_numbers_follow_the_organizations_policy()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();

        // default: held in an unverified queue
        var queued = await Detail(s, (await (await PhoneIn(s, phone)).Content.ReadFromJsonAsync<CreatedDto>())!.Id);
        Assert.Equal((ComplaintStatus.OnHold, "sender_unverified", false, null), (queued.Status, queued.HoldReason, queued.ReporterVerified, queued.ReportedByUserId));

        // staff check the caller and link them: the complaint can then be released
        var known = await ResidentWithPhone(s, NewPhone());
        var verified = await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{queued.Id}/verify-reporter", new VerifyReporter(known));
        var after = (await verified.Content.ReadFromJsonAsync<ComplaintDto>())!;
        Assert.Equal((true, known), (after.ReporterVerified, after.ReportedByUserId));
        Assert.Equal(HttpStatusCode.OK, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{queued.Id}/transition", new Transition(ComplaintStatus.New, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{queued.Id}/verify-reporter", new VerifyReporter(Guid.NewGuid()))).StatusCode);

        await SetPolicy(s, OrgPolicy.Default with { UnknownSender = UnknownSenderPolicy.Guest });
        var guest = await Detail(s, (await (await PhoneIn(s, phone)).Content.ReadFromJsonAsync<CreatedDto>())!.Id);
        Assert.Equal((ComplaintStatus.New, false), (guest.Status, guest.ReporterVerified));

        await SetPolicy(s, OrgPolicy.Default with { UnknownSender = UnknownSenderPolicy.OtpRegistration });
        var blocked = await PhoneIn(s, phone);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Contains("registration_required", await blocked.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Phone_numbers_are_checked_and_only_for_staff_entry()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await PhoneIn(s, "123")).StatusCode);
        var fromResident = await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("x", null, null, s.HouseId, null, null, null, NewPhone(), null));
        Assert.Equal(HttpStatusCode.BadRequest, fromResident.StatusCode);
    }

    [Fact]
    public async Task Policy_changes_are_for_managers_and_validated()
    {
        var s = await Society.CreateAsync(app);
        Assert.Equal(OrgPolicy.Default, await s.Supervisor.Client.GetFromJsonAsync<OrgPolicy>("/organization/policies"));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { UnknownSender = "bogus" })).StatusCode);
    }

    // CP-09 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resident_finds_their_own_complaint_by_reference_and_nobody_elses()
    {
        var s = await Society.CreateAsync(app);
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;

        var mine = await s.Resident.Client.GetAsync($"/complaints/by-reference/{created.Reference.ToLowerInvariant()}");
        Assert.Equal(created.Id, (await mine.Content.ReadFromJsonAsync<ComplaintDto>())!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Neighbour.Client.GetAsync($"/complaints/by-reference/{created.Reference}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Resident.Client.GetAsync("/complaints/by-reference/UF-1999-00000")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Supervisor.Client.GetAsync($"/complaints/by-reference/{created.Reference}")).StatusCode);
    }

    [Fact]
    public async Task Status_by_verified_phone_needs_the_reporters_own_number()
    {
        var s = await Society.CreateAsync(app);
        var phone = NewPhone();
        await ResidentWithPhone(s, phone);
        var created = (await (await PhoneIn(s, phone)).Content.ReadFromJsonAsync<CreatedDto>())!;
        var guestPhone = NewPhone();
        await SetPolicy(s, OrgPolicy.Default with { UnknownSender = UnknownSenderPolicy.Guest });
        var guest = (await (await PhoneIn(s, guestPhone)).Content.ReadFromJsonAsync<CreatedDto>())!;

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ChangeTenantAsync(s.Org, null);
        Assert.Equal(ComplaintStatus.New, (await ComplaintLookup.StatusByPhoneAsync(db, created.Reference, phone, default))!.Status);
        Assert.Null(await ComplaintLookup.StatusByPhoneAsync(db, created.Reference, NewPhone(), default));
        Assert.Null(await ComplaintLookup.StatusByPhoneAsync(db, created.Reference, guestPhone, default)); // someone else's complaint
        Assert.NotNull(await ComplaintLookup.StatusByPhoneAsync(db, guest.Reference, guestPhone, default)); // a guest sees their own
    }

    // CP-10 -----------------------------------------------------------------------------------------------------------

    private static async Task<Guid> ResolvedComplaint(Society s, bool byResident = true)
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Fix", null))).Content.ReadFromJsonAsync<JobDto>())!;
        await s.FinishWorkAsync(job);
        return created.Id;
    }

    [Fact]
    public async Task By_default_only_the_resident_closes_a_resolved_complaint()
    {
        var s = await Society.CreateAsync(app);
        var id = await ResolvedComplaint(s);

        var staff = await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{id}/transition", new Transition(ComplaintStatus.Closed, null, null));
        Assert.Equal(HttpStatusCode.Forbidden, staff.StatusCode);
        Assert.Contains("resident_confirmation_required", await staff.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await s.Resident.Client.PostAsJsonAsync($"/complaints/{id}/transition", new Transition(ComplaintStatus.Closed, null, null))).StatusCode);

        // With the rule switched off, staff may close for the resident.
        await SetPolicy(s, OrgPolicy.Default with { ClosureNeedsResident = false });
        var second = await ResolvedComplaint(s);
        Assert.Equal(HttpStatusCode.OK, (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{second}/transition", new Transition(ComplaintStatus.Closed, null, null))).StatusCode);
    }

    [Fact]
    public async Task Auto_confirm_is_off_by_default_and_when_on_reminds_then_closes_and_marks_it()
    {
        var s = await Society.CreateAsync(app);
        var sweeper = app.Services.GetRequiredService<ConfirmationSweeper>();
        var id = await ResolvedComplaint(s);

        await Sql("UPDATE complaints SET resolved_at = now() - interval '10 days' WHERE id = @id", ("id", id));
        await sweeper.RunAsync(default);
        Assert.Equal(ComplaintStatus.Resolved, (await Detail(s, id)).Status); // nothing without the switch

        await SetPolicy(s, OrgPolicy.Default with { AutoConfirm = true, AutoConfirmDays = 3, ReminderDays = 1 });
        await Sql("UPDATE complaints SET resolved_at = now() - interval '2 days' WHERE id = @id", ("id", id));
        await sweeper.RunAsync(default);
        Assert.Equal(ComplaintStatus.Resolved, (await Detail(s, id)).Status); // reminded, not closed
        await sweeper.RunAsync(default);
        await using (var conn = new NpgsqlConnection(app.OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE type LIKE 'UrbanFix.Complaints.ConfirmationReminder,%' AND payload::text LIKE '%' || @id || '%'", conn);
            cmd.Parameters.AddWithValue("id", id.ToString());
            Assert.Equal(1L, await cmd.ExecuteScalarAsync()); // one reminder only
        }

        await Sql("UPDATE complaints SET resolved_at = now() - interval '4 days' WHERE id = @id", ("id", id));
        await sweeper.RunAsync(default);
        var closed = await Detail(s, id);
        Assert.Equal((ComplaintStatus.Closed, true), (closed.Status, closed.AutoConfirmed));
        var history = (await s.Manager.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!.History.Last();
        Assert.Equal((ComplaintStatus.Closed, null), (history.To, history.ActorUserId));
        Assert.Contains("Auto-confirmed", history.Reason);
    }

    // CP-11 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_asset_reference_is_stored_and_searchable()
    {
        var s = await Society.CreateAsync(app);
        var tag = "POLE-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var res = await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Light is out", null, s.CategoryId, s.HouseId, null, null, null, null, $"  {tag}  "));
        var id = (await res.Content.ReadFromJsonAsync<CreatedDto>())!.Id;
        Assert.Equal(tag, (await Detail(s, id)).AssetRef);

        var found = await s.Office.Client.GetFromJsonAsync<List<ComplaintDto>>($"/complaints?q={tag.ToLowerInvariant()}");
        Assert.Equal([id], found!.Select(c => c.Id));

        var tooLong = await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("x", null, null, s.HouseId, null, null, null, null, new string('a', 101)));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }
}
