using System.Net;
using System.Text;
using Npgsql;
using UrbanFix.Infrastructure.WhatsApp;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

/// <summary>Contract tests against webhook bodies in the shapes the Cloud API documents (WA-08, QA-06). No database.</summary>
public class WhatsAppPayloadTests
{
    private static ParsedWebhook Parse(string fixture, params (string, string)[] values) =>
        WhatsAppPayload.Parse(WaTest.Fixture(fixture, [("PHONE_NUMBER_ID", "pn-1"), ("FROM", "923001234567"), ("WAMID", "wamid.A"), .. values]));

    [Fact]
    public void A_text_message_is_read_with_its_sender_name_and_number_id()
    {
        var m = Assert.Single(Parse("text", ("BODY", "No water in block A")).Messages);
        Assert.Equal(("pn-1", "923001234567", "Ayesha", "wamid.A", "text", "No water in block A"), (m.PhoneNumberId, m.From, m.ProfileName, m.MessageId, m.Type, m.Text));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1760000000), m.SentAt);
        Assert.Contains("\"type\":\"text\"", m.Raw.Replace(" ", ""));
    }

    [Fact]
    public void A_photo_has_its_media_id_type_and_caption_and_a_voice_note_its_media_id()
    {
        var image = Assert.Single(Parse("image", ("MEDIA_ID", "media-1")).Messages);
        Assert.Equal(("image", "media-1", "image/jpeg", "Look at this"), (image.Type, image.MediaId, image.MediaMime, image.Caption));

        var audio = Assert.Single(Parse("audio", ("MEDIA_ID", "media-2")).Messages);
        Assert.Equal(("audio", "media-2"), (audio.Type, audio.MediaId));
        Assert.StartsWith("audio/ogg", audio.MediaMime);
    }

    [Fact]
    public void A_location_pin_has_coordinates_and_a_place_name()
    {
        var m = Assert.Single(Parse("location").Messages);
        Assert.Equal((24.8607, 67.0011, "Near the mosque"), (m.Latitude, m.Longitude, m.PlaceName));
    }

    [Theory]
    [InlineData("button_reply", "report", "Report a problem")]
    [InlineData("list_reply", "row_2", "Street lights")]
    [InlineData("template_button", "confirm_fixed", "It is fixed")]
    public void Button_taps_from_every_kind_of_message_give_the_button_id(string fixture, string id, string title)
    {
        var m = Assert.Single(Parse(fixture, ("BUTTON_ID", "report")).Messages);
        Assert.Equal((id, title), (m.ButtonId, m.ButtonTitle));
    }

    [Fact]
    public void Delivery_receipts_are_read_with_their_error_when_they_failed()
    {
        var delivered = Assert.Single(Parse("status_delivered").Statuses);
        Assert.Equal(("wamid.A", "delivered", "923001234567", null), (delivered.MessageId, delivered.Status, delivered.Recipient, delivered.ErrorCode));
        Assert.Empty(Parse("status_delivered").Messages);

        var failed = Assert.Single(Parse("status_failed").Statuses);
        Assert.Equal(("failed", "131026", "Message undeliverable"), (failed.Status, failed.ErrorCode, failed.ErrorTitle));
        Assert.Equal(["sent"], Parse("status_sent").Statuses.Select(s => s.Status));
        Assert.Equal(["read"], Parse("status_read").Statuses.Select(s => s.Status));
    }

    [Fact]
    public void Types_we_do_not_handle_and_fields_we_do_not_know_are_read_without_trouble()
    {
        var sticker = Assert.Single(Parse("sticker").Messages);
        Assert.Equal("sticker", sticker.Type);
        var unknown = Assert.Single(Parse("unknown_field").Messages);
        Assert.Equal("reaction", unknown.Type);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"object\":\"page\"}")]
    [InlineData("[]")]
    public void A_body_that_is_not_a_whatsapp_webhook_is_refused(string body) => Assert.Throws<FormatException>(() => WhatsAppPayload.Parse(body));

    [Fact]
    public void Entries_without_the_parts_we_need_are_skipped_not_fatal()
    {
        Assert.Empty(WhatsAppPayload.Parse("{\"object\":\"whatsapp_business_account\"}").Messages);
        Assert.Empty(WhatsAppPayload.Parse("{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"account_update\",\"value\":{}}]}]}").Messages);
        var noId = "{\"object\":\"whatsapp_business_account\",\"entry\":[{\"changes\":[{\"field\":\"messages\",\"value\":{\"metadata\":{\"phone_number_id\":\"p\"},\"messages\":[{\"from\":\"1\",\"type\":\"text\"}]}}]}]}";
        Assert.Empty(WhatsAppPayload.Parse(noId).Messages);
    }

    [Fact]
    public void A_stored_message_can_be_read_back_from_its_raw_json()
    {
        var m = Assert.Single(Parse("location").Messages);
        var again = WhatsAppPayload.ParseSingle(m.Raw)!;
        Assert.Equal((m.Latitude, m.Longitude, m.MessageId), (again.Latitude, again.Longitude, again.MessageId));
        Assert.Null(WhatsAppPayload.ParseSingle("nope"));
    }
}

public class WhatsAppSignatureTests
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"object\":\"whatsapp_business_account\"}");

    [Fact]
    public void A_correct_signature_passes_and_any_change_to_the_body_or_secret_fails()
    {
        var header = WhatsAppSignature.Sign("secret", Body);
        Assert.StartsWith("sha256=", header);
        Assert.True(WhatsAppSignature.Verify("secret", Body, header));
        Assert.True(WhatsAppSignature.Verify("secret", Body, header.ToUpperInvariant().Replace("SHA256=", "sha256=")));
        Assert.False(WhatsAppSignature.Verify("secret", Encoding.UTF8.GetBytes("{\"object\":\"other\"}"), header));
        Assert.False(WhatsAppSignature.Verify("other-secret", Body, header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256=")]
    [InlineData("sha1=abcdef")]
    [InlineData("garbage")]
    public void Missing_or_malformed_headers_fail(string? header) => Assert.False(WhatsAppSignature.Verify("secret", Body, header));

    [Fact]
    public void With_no_secret_configured_nothing_is_accepted() =>
        Assert.False(WhatsAppSignature.Verify("", Body, WhatsAppSignature.Sign("", Body)));
}

[Collection("db")]
public class WhatsAppWebhookEndpointTests(TestApp app)
{
    private async Task<long> InboundCount(Guid org, string? waMessageId = null)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM whatsapp_messages WHERE organization_id = @o AND direction = 'in' AND (@w::text IS NULL OR wa_message_id = @w)", conn);
        cmd.Parameters.AddWithValue("o", org);
        cmd.Parameters.AddWithValue("w", (object?)waMessageId ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Registering_the_webhook_echoes_the_challenge_only_for_the_right_token()
    {
        var client = app.CreateClient();
        var good = await client.GetAsync($"/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token={WaTest.VerifyToken}&hub.challenge=12345");
        Assert.Equal(("12345", HttpStatusCode.OK), (await good.Content.ReadAsStringAsync(), good.StatusCode));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/webhooks/whatsapp?hub.mode=subscribe&hub.verify_token=wrong&hub.challenge=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/webhooks/whatsapp?hub.mode=unsubscribe&hub.verify_token={WaTest.VerifyToken}&hub.challenge=1")).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_unsigned_message_is_refused_and_stores_nothing()
    {
        var s = await Society.CreateAsync(app);
        var number = await WaTest.AddChannelAsync(app, s.Org);
        var id = WaTest.Wamid();
        var body = WaTest.Fixture("text", ("PHONE_NUMBER_ID", number), ("FROM", "923001230001"), ("WAMID", id), ("BODY", "No water here"));

        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WaTest.Webhook(body, "sha256=" + new string('0', 64)))).StatusCode);
        var unsigned = new HttpRequestMessage(HttpMethod.Post, "/webhooks/whatsapp") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unsigned)).StatusCode);
        // Signed by someone who does not know the secret, or altered after signing.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WaTest.Webhook(body, WhatsAppSignature.Sign("guessed", Encoding.UTF8.GetBytes(body))))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(WaTest.Webhook(body + " ", WhatsAppSignature.Sign(WaTest.AppSecret, Encoding.UTF8.GetBytes(body))))).StatusCode);
        Assert.Equal(0, await InboundCount(s.Org));
    }

    [Fact]
    public async Task A_signed_message_is_stored_once_and_a_replay_changes_nothing()
    {
        var s = await Society.CreateAsync(app);
        var number = await WaTest.AddChannelAsync(app, s.Org);
        var id = WaTest.Wamid();
        var body = WaTest.Fixture("text", ("PHONE_NUMBER_ID", number), ("FROM", "923001230002"), ("WAMID", id), ("BODY", "No water here"));
        var client = app.CreateClient();

        var first = await client.SendAsync(WaTest.Webhook(body));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"stored\":1", await first.Content.ReadAsStringAsync());
        Assert.Equal(1, await InboundCount(s.Org, id));

        var replay = await client.SendAsync(WaTest.Webhook(body));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); // Meta must not be told to retry
        Assert.Contains("\"duplicates\":1", await replay.Content.ReadAsStringAsync());
        Assert.Equal(1, await InboundCount(s.Org, id));
    }

    [Fact]
    public async Task A_message_for_a_number_that_belongs_to_nobody_is_acknowledged_and_ignored()
    {
        var body = WaTest.Fixture("text", ("PHONE_NUMBER_ID", "pn-nobody"), ("FROM", "923001230003"), ("WAMID", WaTest.Wamid()), ("BODY", "hello"));
        var res = await app.CreateClient().SendAsync(WaTest.Webhook(body));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"unknownNumbers\":1", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Each_society_only_receives_messages_for_its_own_number()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        var numberA = await WaTest.AddChannelAsync(app, a.Org);
        await WaTest.AddChannelAsync(app, b.Org);
        var id = WaTest.Wamid();
        await app.CreateClient().SendAsync(WaTest.Webhook(WaTest.Fixture("text", ("PHONE_NUMBER_ID", numberA), ("FROM", "923001230004"), ("WAMID", id), ("BODY", "hi there"))));
        Assert.Equal(1, await InboundCount(a.Org, id));
        Assert.Equal(0, await InboundCount(b.Org, id));
    }

    [Fact]
    public async Task A_body_that_is_not_a_webhook_is_a_bad_request_when_signed_correctly()
    {
        var body = "{\"object\":\"page\"}";
        var res = await app.CreateClient().SendAsync(WaTest.Webhook(body));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
