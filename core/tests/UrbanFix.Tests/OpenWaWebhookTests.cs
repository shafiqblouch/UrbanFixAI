using System.Text;
using UrbanFix.Infrastructure.WhatsApp;

namespace UrbanFix.Tests;

/// <summary>Contract tests for the OpenWA webhook parser and signature. No database (WA-08, QA-06).</summary>
public class OpenWaWebhookTests
{
    private const string Session = "sess-1";

    private static string Envelope(string data) =>
        $$"""{"event":"message.received","timestamp":1719312000,"sessionId":"{{Session}}","data":{{data}}}""";

    [Fact]
    public void A_text_message_becomes_one_inbound_message_for_the_session()
    {
        var hook = OpenWaWebhook.Parse(Envelope("""{"id":"m1","from":"923001234567@c.us","chatId":"923001234567@c.us","type":"text","body":"The street light is broken","timestamp":1719312000}"""));
        var m = Assert.Single(hook.Messages);
        Assert.Equal(Session, m.PhoneNumberId);     // the channel is keyed by the session id
        Assert.Equal("923001234567", m.From);       // digits only, no @c.us and no plus
        Assert.Equal("m1", m.MessageId);
        Assert.Equal("text", m.Type);
        Assert.Equal("The street light is broken", m.Text);
        Assert.Null(m.MediaId);
    }

    [Fact]
    public void An_image_with_inline_media_is_an_image_message_whose_media_id_is_the_message_id()
    {
        var hook = OpenWaWebhook.Parse(Envelope("""{"id":"m2","from":"923001234567@c.us","type":"image","body":"pothole","media":{"mimetype":"image/jpeg","data":"/9j/4AAQ","sizeBytes":5}}"""));
        var m = Assert.Single(hook.Messages);
        Assert.Equal("image", m.Type);
        Assert.Equal("m2", m.MediaId);              // the client looks the bytes up by message id
        Assert.Equal("pothole", m.Caption);
    }

    [Fact]
    public void Media_that_was_omitted_or_has_no_bytes_is_not_treated_as_media()
    {
        var omitted = OpenWaWebhook.Parse(Envelope("""{"id":"m3","from":"923001234567@c.us","type":"image","media":{"mimetype":"image/jpeg","omitted":true}}"""));
        Assert.Null(Assert.Single(omitted.Messages).MediaId);
    }

    [Fact]
    public void A_lid_sender_is_reported_by_its_resolved_phone()
    {
        var hook = OpenWaWebhook.Parse(Envelope("""{"id":"m4","from":"12345@lid","type":"text","body":"hello there please","contact":{"senderPhone":"923009999999"}}"""));
        Assert.Equal("923009999999", Assert.Single(hook.Messages).From);
    }

    [Fact]
    public void A_tapped_button_is_read_as_its_text_so_word_commands_still_work()
    {
        var hook = OpenWaWebhook.Parse(Envelope("""{"id":"m5","from":"923001234567@c.us","type":"button","button":{"id":"done","text":"Done"}}"""));
        var m = Assert.Single(hook.Messages);
        Assert.Equal("text", m.Type);
        Assert.Equal("Done", m.Text);
    }

    [Fact]
    public void Our_own_echoes_and_group_messages_are_skipped()
    {
        Assert.Empty(OpenWaWebhook.Parse(Envelope("""{"id":"m6","from":"923001234567@c.us","type":"text","body":"echo","fromMe":true}""")).Messages);
        Assert.Empty(OpenWaWebhook.Parse(Envelope("""{"id":"m7","from":"120363@g.us","type":"text","body":"group","isGroup":true}""")).Messages);
    }

    [Fact]
    public void Events_other_than_message_received_yield_nothing()
    {
        var statusEvent = """{"event":"session.status","timestamp":1,"sessionId":"sess-1","data":{"status":"connected"}}""";
        Assert.Empty(OpenWaWebhook.Parse(statusEvent).Messages);
    }

    [Fact]
    public void A_body_that_is_not_json_is_refused()
    {
        Assert.Throws<FormatException>(() => OpenWaWebhook.Parse("not json"));
    }

    [Fact]
    public void A_signature_is_valid_only_for_the_exact_body_and_secret()
    {
        var body = Encoding.UTF8.GetBytes(Envelope("""{"id":"m8","from":"923001234567@c.us","type":"text","body":"hi"}"""));
        var header = OpenWaSignature.Sign("top-secret", body);
        Assert.True(OpenWaSignature.Verify("top-secret", body, header));
        Assert.False(OpenWaSignature.Verify("wrong-secret", body, header));
        Assert.False(OpenWaSignature.Verify("top-secret", Encoding.UTF8.GetBytes("tampered"), header));
        Assert.False(OpenWaSignature.Verify("", body, header));   // fail closed when no secret is set
        Assert.False(OpenWaSignature.Verify("top-secret", body, null));
    }
}
