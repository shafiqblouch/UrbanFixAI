using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.WhatsApp;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.NotificationEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class WhatsAppNotificationTests(TestApp app)
{
    private FakeWhatsAppClient Client => app.Services.GetRequiredService<FakeWhatsAppClient>();
    private RecordingSmsSender Sms => app.Services.GetRequiredService<RecordingSmsSender>();
    private RecordingEmailSender Email => app.Services.GetRequiredService<RecordingEmailSender>();

    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<(string Channel, string Status, string? Provider, string? Error)>> Deliveries(Guid user)
    {
        var rows = new List<(string, string, string?, string?)>();
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT d.channel, d.status, d.provider_message_id, d.last_error FROM notification_deliveries d JOIN notifications n ON n.id = d.notification_id AND n.organization_id = d.organization_id WHERE n.user_id = @u ORDER BY d.created_at", conn);
        cmd.Parameters.AddWithValue("u", user);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return rows;
    }

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private sealed record Resident(string WaId, Guid UserId, ApiSession Session);

    /// <summary>A resident with a phone number who chose to be told over WhatsApp (and, if asked, other ways).</summary>
    private async Task<Resident> ResidentAsync(WaChat chat, string[] channels, bool withPhone = true)
    {
        var waId = WaChat.NewWaId();
        var email = $"notify-{Guid.NewGuid():N}@x.test";
        var res = await chat.S.Manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest(email, "Notified", TestSecrets.UserPassword, Role.Resident, null, null, withPhone ? "+" + waId : null));
        res.EnsureSuccessStatusCode();
        var userId = (await res.Content.ReadFromJsonAsync<AdminEndpoints.MemberDto>())!.UserId;
        (await chat.S.Manager.Client.PostAsJsonAsync($"/houses/{chat.S.HouseId}/residents", new SetupEndpoints.CreateResidentLink(userId, "family", null))).EnsureSuccessStatusCode();
        await Sql("UPDATE users SET notify_channels = @c WHERE id = @u", ("c", channels), ("u", userId));
        return new Resident(waId, userId, await app.LoginAsync(email, TestSecrets.UserPassword, chat.S.Org)) with { };
    }

    private static async Task<CreatedDto> Complain(Resident r, string text = "A pipe is leaking in the lane") =>
        (await (await r.Session.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, null, null, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;

    private static async Task Status(WaChat chat, string fixture, string waId, string wamid) =>
        (await chat.PostAsync(fixture, waId, ("WAMID", wamid))).EnsureSuccessStatusCode();

    // NT-02 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inside_the_24_hour_window_the_notification_goes_as_a_normal_message_and_receipts_are_followed()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"]);
        await chat.Text(r.WaId, "hello"); // they wrote to us just now
        var created = await Complain(r);
        await Drain();

        var sent = chat.Client.To(r.WaId).Last(m => m.Text.Contains("Complaint received"));
        Assert.Equal("text", sent.Kind);
        Assert.Contains(created.Reference, sent.Text);

        var delivery = (await Deliveries(r.UserId)).Single(d => d.Channel == "whatsapp");
        Assert.Equal(("Sent", sent.Id), (delivery.Status, delivery.Provider));

        await Status(chat, "status_delivered", r.WaId, sent.Id);
        Assert.Equal("Delivered", (await Deliveries(r.UserId)).Single(d => d.Channel == "whatsapp").Status);
        await Status(chat, "status_read", r.WaId, sent.Id);
        Assert.Equal("Read", (await Deliveries(r.UserId)).Single(d => d.Channel == "whatsapp").Status);
        await Status(chat, "status_sent", r.WaId, sent.Id); // a late "sent" receipt must not undo "read"
        Assert.Equal("Read", (await Deliveries(r.UserId)).Single(d => d.Channel == "whatsapp").Status);
        Assert.Equal("read", await ScalarString("SELECT status FROM whatsapp_messages WHERE organization_id = @o AND wa_message_id = @w", chat.S.Org, sent.Id));
    }

    private async Task<string?> ScalarString(string sql, Guid org, string wamid)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("o", org);
        cmd.Parameters.AddWithValue("w", wamid);
        return await cmd.ExecuteScalarAsync() as string;
    }

    [Fact]
    public async Task Outside_the_window_the_approved_template_goes_instead_filled_from_the_notification()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"]);
        await chat.Text(r.WaId, "hello");
        await chat.Sql("UPDATE whatsapp_conversations SET last_inbound_at = now() - interval '3 days' WHERE organization_id = @o");

        var created = await Complain(r);
        await Drain();

        var sent = chat.Client.To(r.WaId).Last(m => m.Kind == "template");
        Assert.Equal(("uf_complaint_received", "en"), (sent.Template, sent.Language));
        Assert.Equal([created.Reference], sent.Parameters);
        Assert.DoesNotContain(chat.Client.To(r.WaId), m => m.Kind == "text" && m.Text.Contains("Complaint received"));
    }

    [Fact]
    public async Task If_whatsapp_refuses_a_free_form_message_the_template_is_used()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"]);
        await chat.Text(r.WaId, "hello");
        Client.RefuseFreeForm = true;
        try
        {
            await Complain(r);
            await Drain();
        }
        finally { Client.RefuseFreeForm = false; }
        Assert.Contains(chat.Client.To(r.WaId), m => m.Kind == "template" && m.Template == "uf_complaint_received");
    }

    [Fact]
    public async Task With_no_template_for_the_message_outside_the_window_it_is_not_sent_by_whatsapp()
    {
        var chat = await WaChat.CreateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.ChangeTenantAsync(chat.S.Org, null);
        var sender = scope.ServiceProvider.GetRequiredService<WhatsAppSender>();

        var phone = "+" + WaChat.NewWaId();
        var result = await sender.NotifyAsync(phone, "free text", "no_such_template", new Dictionary<string, string>(), "en", default);
        Assert.Equal(SendOutcome.NeedsTemplate, result.Outcome);
        Assert.Empty(chat.Client.To(phone.TrimStart('+')));
    }

    // NT-03: text-message fallback -----------------------------------------------------------------------------------

    [Fact]
    public async Task When_whatsapp_cannot_send_the_same_message_goes_as_a_text()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"]);
        await chat.Text(r.WaId, "hello");
        await chat.Sql("UPDATE whatsapp_conversations SET last_inbound_at = now() - interval '3 days' WHERE organization_id = @o");

        // A danger word makes the safety prompt, which has no WhatsApp template, so outside the window it cannot go that way.
        await Complain(r, "There is a live wire hanging in our street");
        await Drain();

        var deliveries = (await Deliveries(r.UserId)).Where(d => d.Channel is "whatsapp" or "sms").ToList();
        Assert.Contains(deliveries, d => d.Channel == "whatsapp" && d.Status == "Failed" && d.Error == "outside_24h_and_no_template");
        Assert.Contains(deliveries, d => d.Channel == "sms" && d.Status == "Sent");
        Assert.Contains(Sms.Sent, s => s.Phone == "+" + r.WaId && s.Message.Contains("1122"));
    }

    [Fact]
    public async Task A_failure_report_from_whatsapp_after_sending_also_triggers_the_text_message()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"]);
        await chat.Text(r.WaId, "hello");
        var created = await Complain(r);
        await Drain();
        var sent = chat.Client.To(r.WaId).Last(m => m.Text.Contains("Complaint received"));

        await Status(chat, "status_failed", r.WaId, sent.Id); // WhatsApp: the receiver cannot get this message
        var deliveries = await Deliveries(r.UserId);
        Assert.Equal(("Failed", "Message undeliverable"), (deliveries.Single(d => d.Channel == "whatsapp").Status, deliveries.Single(d => d.Channel == "whatsapp").Error));
        Assert.Equal("Sent", deliveries.Single(d => d.Channel == "sms").Status);
        Assert.Contains(Sms.Sent, s => s.Phone == "+" + r.WaId && s.Message.Contains(created.Reference));

        await Status(chat, "status_failed", r.WaId, sent.Id); // reported twice: still one text
        Assert.Single((await Deliveries(r.UserId)).Where(d => d.Channel == "sms"));
    }

    [Fact]
    public async Task Someone_who_sent_stop_gets_nothing_not_even_a_text()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"]);
        await chat.Text(r.WaId, "STOP");
        var before = chat.Client.To(r.WaId).Count;
        var textsBefore = Sms.CountFor("+" + r.WaId);

        await Complain(r);
        await Drain();

        Assert.Equal(before, chat.Client.To(r.WaId).Count);
        Assert.Equal(textsBefore, Sms.CountFor("+" + r.WaId));
        var deliveries = await Deliveries(r.UserId);
        Assert.Equal("opted_out", deliveries.Single(d => d.Channel == "whatsapp").Error);
        Assert.DoesNotContain(deliveries, d => d.Channel == "sms");
    }

    [Fact]
    public async Task Someone_without_a_phone_number_cannot_be_reached_that_way_and_the_failure_is_recorded()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["whatsapp"], withPhone: false);
        await Complain(r);
        await Drain();

        var deliveries = await Deliveries(r.UserId);
        Assert.Equal(("Failed", "no_phone_number"), (deliveries.Single(d => d.Channel == "whatsapp").Status, deliveries.Single(d => d.Channel == "whatsapp").Error));
        Assert.Equal("Failed", deliveries.Single(d => d.Channel == "sms").Status);
        Assert.Equal("Sent", deliveries.Single(d => d.Channel == "in_app").Status); // the inbox still got it
    }

    [Fact]
    public async Task People_who_did_not_choose_whatsapp_are_not_sent_anything_on_it()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, []);
        await chat.Text(r.WaId, "hello");
        var before = chat.Client.To(r.WaId).Count;
        await Complain(r);
        await Drain();
        Assert.Equal(before, chat.Client.To(r.WaId).Count);
        Assert.DoesNotContain(await Deliveries(r.UserId), d => d.Channel is "whatsapp" or "sms");
    }

    // NT-04 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task People_who_chose_email_get_the_message_by_email()
    {
        var chat = await WaChat.CreateAsync(app);
        var r = await ResidentAsync(chat, ["email"]);
        var created = await Complain(r);
        await Drain();
        var mail = Email.Sent.Single(m => m.Body.Contains(created.Reference));
        Assert.StartsWith("notify-", mail.To);
        Assert.Equal("Complaint received", mail.Subject);
        Assert.Equal("Sent", (await Deliveries(r.UserId)).Single(d => d.Channel == "email").Status);
    }

    // NT-07 -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_delivery_report_shows_each_channels_counts_and_failure_rate()
    {
        var chat = await WaChat.CreateAsync(app);
        var good = await ResidentAsync(chat, ["whatsapp"]);
        var noPhone = await ResidentAsync(chat, ["whatsapp"], withPhone: false);
        await chat.Text(good.WaId, "hello");
        var created = await Complain(good);
        await Complain(noPhone);
        await Drain();
        var sent = chat.Client.To(good.WaId).Last(m => m.Text.Contains("Complaint received"));
        await Status(chat, "status_delivered", good.WaId, sent.Id);

        var report = (await chat.S.Manager.Client.GetFromJsonAsync<List<ChannelStats>>("/reports/delivery"))!;
        var whatsapp = report.Single(c => c.Channel == "whatsapp");
        Assert.Equal((2, 1, 1), (whatsapp.Total, whatsapp.Delivered, whatsapp.Failed));
        Assert.Equal(0.5, whatsapp.FailureRate);
        Assert.Equal(0, report.Single(c => c.Channel == "in_app").Failed);
        Assert.Equal(HttpStatusCode.Forbidden, (await good.Session.Client.GetAsync("/reports/delivery")).StatusCode);
        Assert.NotEmpty(created.Reference);
    }
}

public class HttpSmsGatewayTests
{
    private sealed class Capture(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    [Fact]
    public async Task The_request_is_built_from_the_template_with_values_escaped_and_the_credential_sent()
    {
        var capture = new Capture();
        var options = new SmsGatewayOptions { Url = "https://gateway.test/api/send", BodyTemplate = "{\"to\":\"{to}\",\"text\":\"{message}\",\"from\":\"UrbanFix\"}", AuthHeader = "X-Api-Key", AuthValue = "secret-1" };
        await new HttpSmsSender(new HttpClient(capture), options).SendAsync("+923001234567", "Your code is \"123456\"\nشکریہ", default);

        Assert.Equal("https://gateway.test/api/send", capture.Request!.RequestUri!.ToString());
        Assert.Equal("secret-1", capture.Request.Headers.GetValues("X-Api-Key").Single());
        using var doc = JsonDocument.Parse(capture.Body!); // valid JSON even with quotes, a newline and Urdu in the message
        Assert.Equal(("+923001234567", "Your code is \"123456\"\nشکریہ", "UrbanFix"), (doc.RootElement.GetProperty("to").GetString(), doc.RootElement.GetProperty("text").GetString(), doc.RootElement.GetProperty("from").GetString()));
    }

    [Fact]
    public async Task A_gateway_that_refuses_is_an_error_so_the_caller_can_retry_or_fall_back()
    {
        var sender = new HttpSmsSender(new HttpClient(new Capture(HttpStatusCode.BadGateway)), new SmsGatewayOptions { Url = "https://gateway.test/send" });
        await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync("+923001234567", "hi", default));
    }
}
