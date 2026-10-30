using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.WhatsApp;
using UrbanFix.OrgSetup;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

/// <summary>A society with a WhatsApp number, and a way to type into the chat as someone and read what comes back.</summary>
internal sealed class WaChat
{
    private readonly TestApp _app;
    public Society S { get; }
    public string Number { get; private set; } = "";
    public FakeWhatsAppClient Client => _app.Services.GetRequiredService<FakeWhatsAppClient>();

    private WaChat(TestApp app, Society s) { _app = app; S = s; }

    public static async Task<WaChat> CreateAsync(TestApp app, bool linkResident = true)
    {
        var s = await Society.CreateAsync(app, linkResident);
        return new WaChat(app, s) { Number = await WaTest.AddChannelAsync(app, s.Org) };
    }

    public static string NewWaId() => "9230" + Random.Shared.NextInt64(10_000_000, 99_999_999);

    /// <summary>A resident who has this phone number in the directory and lives in the society's house.</summary>
    public async Task<(string WaId, Guid UserId)> ResidentWithPhoneAsync()
    {
        var waId = NewWaId();
        var email = $"wa-{Guid.NewGuid():N}@x.test";
        var res = await S.Manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest(email, "Phone Resident", TestSecrets.UserPassword, Role.Resident, null, null, "+" + waId));
        res.EnsureSuccessStatusCode();
        var userId = (await res.Content.ReadFromJsonAsync<AdminEndpoints.MemberDto>())!.UserId;
        (await S.Manager.Client.PostAsJsonAsync($"/houses/{S.HouseId}/residents", new CreateResidentLink(userId, "family", null))).EnsureSuccessStatusCode();
        return (waId, userId);
    }

    public async Task<HttpResponseMessage> PostAsync(string fixture, string from, params (string, string)[] values)
    {
        // Values passed in win over the defaults (a test can choose the message id to replay it).
        var all = new Dictionary<string, string> { ["PHONE_NUMBER_ID"] = Number, ["FROM"] = from, ["WAMID"] = WaTest.Wamid() };
        foreach (var (key, value) in values) all[key] = value;
        return await _app.CreateClient().SendAsync(WaTest.Webhook(WaTest.Fixture(fixture, [.. all.Select(kv => (kv.Key, kv.Value))])));
    }

    /// <summary>Receives a message and handles it straight away, returning what the chat said back.</summary>
    public async Task<IReadOnlyList<FakeWhatsAppClient.Sent>> SendAsync(string fixture, string from, params (string, string)[] values)
    {
        var before = Client.To(from).Count;
        (await PostAsync(fixture, from, values)).EnsureSuccessStatusCode();
        await ProcessPendingAsync();
        return Client.To(from).Skip(before).ToList();
    }

    public Task<IReadOnlyList<FakeWhatsAppClient.Sent>> Text(string from, string body) => SendAsync("text", from, ("BODY", body));
    public Task<IReadOnlyList<FakeWhatsAppClient.Sent>> Tap(string from, string buttonId) => SendAsync("button_reply", from, ("BUTTON_ID", buttonId));
    public Task<IReadOnlyList<FakeWhatsAppClient.Sent>> Photo(string from, string mediaId) => SendAsync("image", from, ("MEDIA_ID", mediaId));
    public Task<IReadOnlyList<FakeWhatsAppClient.Sent>> Voice(string from, string mediaId) => SendAsync("audio", from, ("MEDIA_ID", mediaId));
    public Task<IReadOnlyList<FakeWhatsAppClient.Sent>> Pin(string from) => SendAsync("location", from);

    public async Task ProcessPendingAsync()
    {
        var ids = new List<Guid>();
        await using (var conn = new NpgsqlConnection(_app.OwnerConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT id FROM whatsapp_messages WHERE organization_id = @o AND direction = 'in' AND status = 'received' ORDER BY created_at, id", conn);
            cmd.Parameters.AddWithValue("o", S.Org);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        }
        foreach (var id in ids)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<WhatsAppConversationService>().ProcessAsync(S.Org, id, default);
        }
    }

    public async Task<T?> Scalar<T>(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(_app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("o", S.Org);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        var value = await cmd.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    public async Task Sql(string sql, params (string, object)[] args) => await Scalar<object>(sql, args);

    public static string ReferenceIn(IEnumerable<FakeWhatsAppClient.Sent> replies) =>
        Regex.Match(string.Join("\n", replies.Select(r => r.Text)), @"UF-\d{4}-\d{5}").Value;

    public async Task<ComplaintDetailDto> DetailAsync(string reference)
    {
        var c = await S.Supervisor.Client.GetFromJsonAsync<ComplaintDto>($"/complaints/by-reference/{reference}");
        return (await S.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{c!.Id}"))!;
    }

    public Task<string?> State(string waId) => Scalar<string>("SELECT state FROM whatsapp_conversations WHERE organization_id = @o AND phone = @p", ("p", "+" + waId));

    public static byte[] Jpeg(int seed = 1)
    {
        var rng = new Random(seed);
        using var img = new Image<Rgb24>(320, 240);
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var p in rows.GetRowSpan(y)) p = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        });
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }

    public static byte[] VoiceNote(byte salt = 1) => [.. "OggS"u8.ToArray(), 0, 2, .. Enumerable.Repeat(salt, 300)];

    public void Serve(string mediaId, byte[] bytes, string mime) => Client.Media[mediaId] = new MediaFile(bytes, mime);
}

[Collection("db")]
public class WhatsAppFlowTests(TestApp app)
{
    private static readonly string[] Menu = ["report", "status", "lang"];

    // WA-04: a whole complaint by chat -------------------------------------------------------------------------------

    [Fact]
    public async Task A_resident_reports_a_problem_adds_a_photo_and_a_voice_note_and_finishes_all_in_the_chat()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, userId) = await chat.ResidentWithPhoneAsync();

        var created = await chat.Text(from, "There is no water in block A since this morning");
        var reply = created[0];
        Assert.Equal(["done"], reply.Buttons.Select(b => b.Id));
        var reference = WaChat.ReferenceIn(created);
        Assert.Matches(@"^UF-\d{4}-\d{5}$", reference);

        var detail = await chat.DetailAsync(reference);
        Assert.Equal(("whatsapp", userId, true, "+" + from, ComplaintStatus.New), (detail.Complaint.Channel, detail.Complaint.ReportedByUserId, detail.Complaint.ReporterVerified, detail.Complaint.ReporterPhone, detail.Complaint.Status));
        Assert.Equal("There is no water in block A since this morning", detail.Complaint.Description);
        Assert.NotNull(detail.Complaint.DueAt);

        chat.Serve("media-photo-1", WaChat.Jpeg(1), "image/jpeg");
        var photo = await chat.Photo(from, "media-photo-1");
        Assert.Contains(reference, photo[0].Text);
        chat.Serve("media-voice-1", WaChat.VoiceNote(), "audio/ogg; codecs=opus");
        await chat.Voice(from, "media-voice-1");

        var more = await chat.Text(from, "It is getting worse every hour");
        Assert.Contains(reference, more[0].Text);

        var finished = await chat.Text(from, "done");
        Assert.Contains(reference, finished[0].Text);
        Assert.Equal(ChatStates.Idle, await chat.State(from));

        detail = await chat.DetailAsync(reference);
        Assert.Equal(["audio", "image"], detail.Media.Select(m => m.Kind).Order());
        Assert.Contains("It is getting worse every hour", detail.Complaint.Description);
    }

    [Fact]
    public async Task The_same_chat_works_in_urdu_and_replies_in_urdu()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var created = await chat.Text(from, "بلاک اے میں صبح سے پانی نہیں آ رہا ہے");
        Assert.Matches("[؀-ۿ]", created[0].Text);
        Assert.Contains("شکایت کا نمبر", created[0].Text);
        Assert.Equal("مکمل", created[0].Buttons.Single().Title);

        var finished = await chat.Text(from, "مکمل");
        Assert.Contains("سب محفوظ", finished[0].Text);
    }

    [Fact]
    public async Task The_menu_report_button_works_and_a_photo_first_waits_for_the_words_then_goes_with_the_complaint()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();

        var hello = await chat.Text(from, "Hello");
        Assert.Equal(Menu, hello[0].Buttons.Select(b => b.Id));
        Assert.Contains(chat.S.Org.ToString().Length > 0 ? "Welcome" : "", hello[0].Text);

        var ask = await chat.Tap(from, "report");
        Assert.Contains("describe", ask[0].Text.Replace("tell me what the problem is", "describe"), StringComparison.OrdinalIgnoreCase);

        chat.Serve("m-first", WaChat.Jpeg(2), "image/jpeg");
        var waiting = await chat.Photo(from, "m-first");
        Assert.Equal(["sendnow", "cancel"], waiting[0].Buttons.Select(b => b.Id));

        var created = await chat.Text(from, "Street light broken outside house 9");
        var detail = await chat.DetailAsync(WaChat.ReferenceIn(created));
        Assert.Single(detail.Media);
        Assert.Equal("image", detail.Media[0].Kind);
    }

    [Fact]
    public async Task A_photo_can_be_sent_as_it_is_and_a_voice_only_complaint_gets_its_words_from_the_transcript()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        chat.Serve("m-voice", WaChat.VoiceNote(3), "audio/ogg");

        await chat.Voice(from, "m-voice");
        var created = await chat.Tap(from, "sendnow");
        var reference = WaChat.ReferenceIn(created);
        var detail = await chat.DetailAsync(reference);
        Assert.Equal(IntakeText.MediaOnly, detail.Complaint.Description);

        var mediaId = detail.Media.Single().Id;
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<VoiceTranscriptionJob>().RunCoreAsync(chat.S.Org, mediaId, 0, default);

        detail = await chat.DetailAsync(reference);
        Assert.Equal("no water since morning", detail.Complaint.Description); // the fake speech model's transcript
        Assert.Equal("pending", detail.Complaint.AiStatus); // read again by the AI now that it has words
    }

    [Fact]
    public async Task Cancelling_before_sending_creates_nothing_and_a_saved_complaint_cannot_be_cancelled()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        await chat.Tap(from, "report");
        var cancelled = await chat.Tap(from, "cancel");
        Assert.Contains("Cancelled", cancelled[0].Text);
        Assert.Equal(ChatStates.Idle, await chat.State(from));

        var created = await chat.Text(from, "Garbage has not been collected for days");
        var again = await chat.Text(from, "cancel");
        Assert.Contains(WaChat.ReferenceIn(created), again[0].Text);
        Assert.Contains("already saved", again[0].Text);
    }

    // CP-05 / CP-08 through the chat ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_number_is_held_for_checking_and_told_so()
    {
        var chat = await WaChat.CreateAsync(app, linkResident: false);
        var from = WaChat.NewWaId();
        var created = await chat.Text(from, "There is sewage overflowing on our street");
        var detail = await chat.DetailAsync(WaChat.ReferenceIn(created));

        Assert.Equal((ComplaintStatus.OnHold, false, null), (detail.Complaint.Status, detail.Complaint.ReporterVerified, detail.Complaint.ReportedByUserId));
        Assert.Contains(created, r => r.Text.Contains("location pin"));
        Assert.Contains(created, r => r.Text.Contains("check your number"));
        Assert.True(detail.Complaint.SafetyFlag); // "sewage overflow" is a danger word
        Assert.Contains(created, r => r.Text.Contains("1122"));
    }

    [Fact]
    public async Task A_location_pin_near_a_known_place_finds_it_and_releases_the_complaint()
    {
        var chat = await WaChat.CreateAsync(app, linkResident: false);
        var place = await (await chat.S.Manager.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Pin Block", null, 24.8607, 67.0011))).Content.ReadFromJsonAsync<LocationDto>();
        var from = WaChat.NewWaId();
        var created = await chat.Text(from, "The tap near the mosque is leaking badly");
        var reference = WaChat.ReferenceIn(created);
        Assert.Equal(ComplaintStatus.OnHold, (await chat.DetailAsync(reference)).Complaint.Status);

        var pinned = await chat.Pin(from); // the fixture's pin is within a few metres of the place
        Assert.Contains("Location noted", pinned[0].Text);
        var detail = (await chat.DetailAsync(reference)).Complaint;
        Assert.Equal((ComplaintStatus.New, place!.Id, null), (detail.Status, detail.LocationId, detail.HoldReason));
        Assert.Equal((24.8607, 67.0011), (detail.Latitude!.Value, detail.Longitude!.Value), new PinComparer());
    }

    [Fact]
    public async Task A_pin_that_matches_nothing_keeps_the_complaint_waiting_and_the_next_line_typed_is_the_address()
    {
        var chat = await WaChat.CreateAsync(app, linkResident: false);
        var from = WaChat.NewWaId();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "The tap near the mosque is leaking badly"));

        var pinned = await chat.Pin(from);
        Assert.Contains(pinned, r => r.Text.Contains("type your street"));
        var detail = (await chat.DetailAsync(reference)).Complaint;
        Assert.Equal(ComplaintStatus.OnHold, detail.Status);
        Assert.Contains("WhatsApp pin", detail.LocationText);
        Assert.NotNull(detail.Latitude); // kept for the map even though no known place matched

        var address = await chat.Text(from, "Street 4, house 12");
        Assert.Contains("confirm the place", address[0].Text);
        Assert.Equal("Street 4, house 12", (await chat.DetailAsync(reference)).Complaint.LocationText);
    }

    [Fact]
    public async Task The_societys_policy_decides_what_happens_to_numbers_it_does_not_know()
    {
        var chat = await WaChat.CreateAsync(app, linkResident: false);

        await chat.S.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { UnknownSender = UnknownSenderPolicy.Guest });
        var guest = WaChat.NewWaId();
        var guestRef = WaChat.ReferenceIn(await chat.Text(guest, "Street light has been out for a week"));
        var guestDetail = (await chat.DetailAsync(guestRef)).Complaint;
        Assert.Equal(false, guestDetail.ReporterVerified);

        await chat.S.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { UnknownSender = UnknownSenderPolicy.OtpRegistration });
        var stranger = WaChat.NewWaId();
        var refused = await chat.Text(stranger, "Street light has been out for a week");
        Assert.Contains("not registered", refused[0].Text);
        Assert.Equal("", WaChat.ReferenceIn(refused));
        Assert.Equal(ChatStates.Idle, await chat.State(stranger));
    }

    // WA-03: media ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_photo_that_cannot_be_fetched_is_reported_and_the_complaint_is_kept()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "A pipe is leaking in the lane"));
        chat.Client.BrokenMedia.Add("gone-1");

        var reply = await chat.Photo(from, "gone-1");
        Assert.Contains("could not read that file", reply[0].Text);
        Assert.Empty((await chat.DetailAsync(reference)).Media);
        Assert.Equal(ChatStates.Collecting, await chat.State(from));

        chat.Serve("ok-1", WaChat.Jpeg(7), "image/jpeg");
        await chat.Photo(from, "ok-1");
        Assert.Single((await chat.DetailAsync(reference)).Media);
    }

    [Fact]
    public async Task Something_that_is_not_an_image_or_sound_is_refused_not_stored()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "A pipe is leaking in the lane"));
        chat.Serve("bad-1", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13], "image/jpeg");
        var reply = await chat.Photo(from, "bad-1");
        Assert.Contains("could not read", reply[0].Text);
        Assert.Empty((await chat.DetailAsync(reference)).Media);
    }

    [Fact]
    public async Task Types_the_chat_cannot_read_get_a_polite_answer()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reply = await chat.SendAsync("sticker", from);
        Assert.Contains("only read text", reply[0].Text);
        Assert.Equal(Menu, reply[1].Buttons.Select(b => b.Id));
    }

    // WA-05: status ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_reporter_asks_for_the_status_by_number_and_nobody_else_learns_it()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "A pipe is leaking in the lane"));
        await chat.Text(from, "done");

        var asked = await chat.Tap(from, "status");
        Assert.Contains("complaint number", asked[0].Text);
        var answer = await chat.Text(from, reference);
        Assert.Contains($"{reference}: received", answer[0].Text);

        var direct = await chat.Text(from, $"what happened to {reference.ToLowerInvariant()} please");
        Assert.Contains("received", direct[0].Text);

        var other = (await chat.ResidentWithPhoneAsync()).WaId;
        var denied = await chat.Text(other, reference);
        Assert.Contains("could not find", denied[0].Text);
        Assert.DoesNotContain("received", denied[0].Text);

        var nobody = await chat.Text(from, "UF-2026-99999");
        Assert.Contains("could not find", nobody[0].Text);
    }

    [Fact]
    public async Task The_status_comes_back_in_urdu_with_the_reason_when_there_is_one()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "پائپ سے پانی بہہ رہا ہے"));
        var c = (await chat.DetailAsync(reference)).Complaint;
        await chat.S.Office.Client.PostAsJsonAsync($"/complaints/{c.Id}/transition", new Transition(ComplaintStatus.Rejected, "Duplicate of another report", null));

        var answer = await chat.Text(from, reference);
        Assert.Contains("قبول نہیں کی گئی", answer[0].Text);
        Assert.Contains("Duplicate of another report", answer[0].Text);
    }

    // WA-04: a long pause ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task After_a_long_pause_the_chat_asks_whether_to_carry_on_and_carries_on_when_told_to()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "A pipe is leaking in the lane"));
        await chat.Sql("UPDATE whatsapp_conversations SET last_inbound_at = now() - interval '7 hours' WHERE organization_id = @o");

        var ask = await chat.Text(from, "hello again");
        Assert.Equal(["continue", "startover"], ask[0].Buttons.Select(b => b.Id));
        Assert.Equal(ChatStates.ConfirmResume, await chat.State(from));

        var back = await chat.Tap(from, "continue");
        Assert.Contains(reference, back[0].Text);
        Assert.Equal(ChatStates.Collecting, await chat.State(from));
        Assert.Single(await chat.S.Supervisor.Client.GetFromJsonAsync<List<ComplaintDto>>($"/complaints?q={reference}") ?? []); // still one complaint
    }

    // WA-06: consent, rate limit, replays ---------------------------------------------------------------------------

    [Fact]
    public async Task Stop_switches_messages_off_and_start_switches_them_on()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var off = await chat.Text(from, "STOP");
        Assert.Contains("will not get messages", off[0].Text);
        Assert.NotNull(await chat.Scalar<DateTime?>("SELECT opted_out_at FROM whatsapp_conversations WHERE organization_id = @o AND phone = @p", ("p", "+" + from)));

        // They can still write to us and be answered.
        Assert.NotEmpty(await chat.Text(from, "hello"));

        var on = await chat.Text(from, "start");
        Assert.Contains("back on", on[0].Text);
        Assert.Null(await chat.Scalar<DateTime?>("SELECT opted_out_at FROM whatsapp_conversations WHERE organization_id = @o AND phone = @p", ("p", "+" + from)));
    }

    [Fact]
    public async Task A_phone_sending_too_fast_is_ignored_and_told_once()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        await chat.Text(from, "hello"); // makes the conversation
        await chat.Sql("""
            INSERT INTO whatsapp_messages (id, organization_id, direction, wa_message_id, wa_id, type, body, status)
            SELECT gen_random_uuid(), @o, 'in', 'flood-' || g, @w, 'text', 'x', 'processed' FROM generate_series(1, 30) g
            """, ("w", from));

        var first = await chat.Text(from, "one more message");
        Assert.Single(first);
        Assert.Contains("very quickly", first[0].Text);
        var second = await chat.Text(from, "and another");
        Assert.Empty(second); // told once, not every time

        var status = await chat.Scalar<string>("SELECT status FROM whatsapp_messages WHERE organization_id = @o AND body = 'and another'");
        Assert.Equal("ignored", status);
    }

    [Fact]
    public async Task Handling_the_same_message_twice_does_not_make_a_second_complaint_or_a_second_reply()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var id = WaTest.Wamid();
        (await chat.PostAsync("text", from, ("BODY", "Garbage pile outside the park"), ("WAMID", id))).EnsureSuccessStatusCode();
        (await chat.PostAsync("text", from, ("BODY", "Garbage pile outside the park"), ("WAMID", id))).EnsureSuccessStatusCode(); // Meta retries
        await chat.ProcessPendingAsync();
        await chat.ProcessPendingAsync();

        Assert.Single(chat.Client.To(from));
        Assert.Equal(1L, await chat.Scalar<long>("SELECT count(*) FROM complaints WHERE organization_id = @o AND reporter_phone = @p", ("p", "+" + from)));
    }

    [Fact]
    public async Task Danger_words_added_later_flag_the_complaint_and_the_reply_says_to_call_rescue()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var reference = WaChat.ReferenceIn(await chat.Text(from, "The street light near the school is flickering"));
        Assert.False((await chat.DetailAsync(reference)).Complaint.SafetyFlag);

        var more = await chat.Text(from, "now there is a live wire hanging down");
        Assert.Contains(more, r => r.Text.Contains("1122"));
        var detail = (await chat.DetailAsync(reference)).Complaint;
        Assert.Equal((true, "Safety"), (detail.SafetyFlag, detail.Priority));
    }

    [Fact]
    public async Task Replies_are_kept_as_outbound_messages_with_their_whatsapp_ids()
    {
        var chat = await WaChat.CreateAsync(app);
        var (from, _) = await chat.ResidentWithPhoneAsync();
        var replies = await chat.Text(from, "A pipe is leaking in the lane");
        var stored = await chat.Scalar<long>("SELECT count(*) FROM whatsapp_messages WHERE organization_id = @o AND direction = 'out' AND wa_id = @w AND status = 'sent' AND wa_message_id IS NOT NULL", ("w", from));
        Assert.Equal(replies.Count, stored);
    }
}

/// <summary>A pin is drawn on a map, so a metre or two does not matter.</summary>
internal sealed class PinComparer : IEqualityComparer<(double, double)>
{
    public bool Equals((double, double) a, (double, double) b) => Math.Abs(a.Item1 - b.Item1) < 0.0005 && Math.Abs(a.Item2 - b.Item2) < 0.0005;
    public int GetHashCode((double, double) x) => 0;
}
