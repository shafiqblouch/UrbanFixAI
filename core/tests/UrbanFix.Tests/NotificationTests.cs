using System.Net;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.JobEndpoints;
using static UrbanFix.Api.Endpoints.NotificationEndpoints;

namespace UrbanFix.Tests;

/// <summary>An extra channel the tests can switch between working and failing.</summary>
public sealed class FlakyChannel : INotificationChannel
{
    public string Name => "test_sms";
    public bool Fail { get; set; }
    public List<string> Sent { get; } = [];
    public Task<string?> SendAsync(Notification n, CancellationToken ct)
    {
        if (Fail) throw new InvalidOperationException("gateway timeout");
        lock (Sent) Sent.Add(n.Title);
        return Task.FromResult<string?>(null);
    }
}

public class NotificationTemplateTests
{
    [Fact]
    public void Every_template_has_english_and_urdu_text_with_the_same_placeholders()
    {
        var holes = new System.Text.RegularExpressions.Regex(@"\{(\w+)\}");
        foreach (var key in NotificationTemplates.Keys)
        {
            var all = new Dictionary<string, string>();
            foreach (var name in new[] { "reference", "number", "reason", "priority", "level", "amount", "percent", "item", "quantity", "title", "body", "period" }) all[name] = $"<{name}>";
            var en = NotificationTemplates.Render(key, "en", all);
            var ur = NotificationTemplates.Render(key, "ur", all);
            Assert.DoesNotMatch(holes, en.Body); // every placeholder is a known value
            Assert.DoesNotMatch(holes, ur.Body);
            if (key == NotificationTemplates.Notice) continue; // the sender's own words; the Urdu text comes from the sender, not the template
            Assert.NotEqual(en.Title, ur.Title);
            Assert.Matches("[؀-ۿ]", ur.Body);
            // the same values appear in both languages
            foreach (var name in all.Keys) Assert.Equal(en.Body.Contains(all[name]), ur.Body.Contains(all[name]));
        }
    }

    [Fact]
    public void An_unknown_language_falls_back_to_english()
    {
        var text = NotificationTemplates.Render(NotificationTemplates.ComplaintReceived, "fr", new Dictionary<string, string> { ["reference"] = "UF-1" });
        Assert.Equal("Complaint received", text.Title);
        Assert.Contains("UF-1", text.Body);
    }
}

[Collection("db")]
public class NotificationTests(TestApp app)
{
    private FlakyChannel Flaky => (FlakyChannel)app.Services.GetRequiredService<FlakyChannel>();

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<InboxDto> Inbox(ApiSession who) => Ok<InboxDto>(who.Client.GetAsync("/notifications"));

    private static async Task<CreatedDto> Complain(Society s, string text = "No water")
        => await Ok<CreatedDto>(s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, s.CategoryId, s.HouseId, null, null, null)));

    private static async Task<JobDto> JobFor(Society s, Guid complaintId)
    {
        await s.Office.Client.PostAsJsonAsync($"/complaints/{complaintId}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        return await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/complaints/{complaintId}/jobs", new CreateJob("Fix", null)));
    }

    // NT-06 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Resident_gets_a_receipt_in_their_own_language_and_can_read_it()
    {
        var s = await Society.CreateAsync(app);
        var first = await Complain(s);
        await Drain();

        var inbox = await Inbox(s.Resident);
        var receipt = Assert.Single(inbox.Items);
        Assert.Equal(1, inbox.Unread);
        Assert.Equal(("complaint_received", "en"), (receipt.Template, receipt.Locale));
        Assert.Contains(first.Reference, receipt.Body);
        Assert.Equal($"/complaints/{first.Id}", receipt.Link);

        // Urdu preference applies to later messages
        Assert.Equal(HttpStatusCode.OK, (await s.Resident.Client.PutAsJsonAsync("/me/preferences", new SavePreferences("ur", []))).StatusCode);
        var second = await Complain(s);
        await Drain();
        var urdu = (await Inbox(s.Resident)).Items.Single(n => n.Link == $"/complaints/{second.Id}");
        Assert.Equal("ur", urdu.Locale);
        Assert.Contains(second.Reference, urdu.Body);
        Assert.Matches("[؀-ۿ]", urdu.Title);

        Assert.Equal(HttpStatusCode.NotFound, (await s.Neighbour.Client.PostAsync($"/notifications/{receipt.Id}/read", null)).StatusCode);
        Assert.NotNull((await Ok<NotificationDto>(s.Resident.Client.PostAsync($"/notifications/{receipt.Id}/read", null))).ReadAt);
        Assert.Equal(1, (await Inbox(s.Resident)).Unread);
        Assert.Single((await Ok<InboxDto>(s.Resident.Client.GetAsync("/notifications?unreadOnly=true"))).Items);
        await s.Resident.Client.PostAsync("/notifications/read-all", null);
        Assert.Equal(0, (await Inbox(s.Resident)).Unread);
        Assert.Empty((await Inbox(s.Neighbour)).Items);
    }

    [Fact]
    public async Task Organization_default_language_applies_when_the_user_has_no_preference()
    {
        var s = await Society.CreateAsync(app);
        var settings = await Ok<SetupEndpoints.SettingsDto>(s.Manager.Client.GetAsync("/organization/settings"));
        (await s.Manager.Client.PutAsJsonAsync("/organization/settings", settings with { Locale = "ur" })).EnsureSuccessStatusCode();
        await Complain(s);
        await Drain();
        Assert.Equal("ur", (await Inbox(s.Resident)).Items.Single().Locale);
    }

    [Fact]
    public async Task Each_lifecycle_event_notifies_the_right_people()
    {
        var s = await Society.CreateAsync(app);
        await s.SetEvidenceAsync("none");
        var c = await Complain(s);
        var job = await JobFor(s, c.Id);
        await Drain();
        Assert.Contains((await Inbox(s.Resident)).Items, n => n.Template == "complaint_assigned" && n.Body.Contains(c.Reference));

        await Ok<JobDto>(s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId)));
        await Drain();
        var assigned = Assert.Single((await Inbox(s.Technician)).Items);
        Assert.Equal(("job_assigned", $"/jobs/{job.Id}"), (assigned.Template, assigned.Link));
        Assert.Contains(job.Number, assigned.Body);

        await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null);
        await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/start", new StartWork(null, null, null));
        await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/complete", new CompleteJob("done"));
        await Drain();
        foreach (var boss in new[] { s.Supervisor, s.Manager })
            Assert.Contains((await Inbox(boss)).Items, n => n.Template == "job_awaiting_signoff" && n.Body.Contains(job.Number));
        Assert.DoesNotContain((await Inbox(s.Technician)).Items, n => n.Template == "job_awaiting_signoff");
        Assert.DoesNotContain((await Inbox(s.Resident)).Items, n => n.Template == "job_awaiting_signoff");

        await s.Supervisor.Client.PostAsync($"/jobs/{job.Id}/sign-off", null);
        await Drain();
        Assert.Contains((await Inbox(s.Resident)).Items, n => n.Template == "confirmation_request" && n.Body.Contains(c.Reference));
    }

    [Fact]
    public async Task Rejection_and_cannot_complete_carry_their_reasons()
    {
        var s = await Society.CreateAsync(app);
        var rejected = await Complain(s, "Duplicate of an old one");
        Assert.Equal(HttpStatusCode.OK, (await s.Office.Client.PostAsJsonAsync($"/complaints/{rejected.Id}/transition", new Transition(ComplaintStatus.Rejected, "Already being handled", null))).StatusCode);

        var c = await Complain(s);
        var job = await JobFor(s, c.Id);
        await s.Supervisor.Client.PostAsJsonAsync($"/jobs/{job.Id}/assign", new AssignJob(null, s.TechnicianId));
        await s.Technician.Client.PostAsync($"/jobs/{job.Id}/accept", null);
        await s.Technician.Client.PostAsJsonAsync($"/jobs/{job.Id}/cannot-complete", new ReasonRequest("Needs a crane"));
        await Drain();

        Assert.Contains((await Inbox(s.Resident)).Items, n => n.Template == "complaint_rejected" && n.Body.Contains("Already being handled"));
        Assert.Contains((await Inbox(s.Supervisor)).Items, n => n.Template == "job_returned" && n.Body.Contains("Needs a crane"));
    }

    [Fact]
    public async Task Replaying_an_event_never_duplicates_messages()
    {
        var s = await Society.CreateAsync(app);
        await Complain(s);
        await Drain();
        Assert.Single((await Inbox(s.Resident)).Items);

        await Sql("UPDATE outbox_messages SET processed_at = NULL WHERE organization_id = @o", ("o", s.Org));
        await Drain();
        Assert.Single((await Inbox(s.Resident)).Items);
        Assert.All(await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log")), d => Assert.Equal(1, d.Attempts));
    }

    // NT-01 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_send_is_logged_retried_and_never_duplicates_the_inbox_message()
    {
        var s = await Society.CreateAsync(app);
        Assert.Contains("test_sms", (await Ok<PreferencesDto>(s.Resident.Client.GetAsync("/me/preferences"))).AvailableChannels);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PutAsJsonAsync("/me/preferences", new SavePreferences(null, ["carrier_pigeon"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PutAsJsonAsync("/me/preferences", new SavePreferences("fr", []))).StatusCode);
        (await s.Resident.Client.PutAsJsonAsync("/me/preferences", new SavePreferences(null, ["test_sms"]))).EnsureSuccessStatusCode();

        Flaky.Fail = true;
        try
        {
            await Complain(s);
            await Drain();

            var failed = Assert.Single(await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log?status=Failed")));
            Assert.Equal(("test_sms", "Failed", 1, "gateway timeout"), (failed.Channel, failed.Status, failed.Attempts, failed.LastError));
            Assert.Single((await Inbox(s.Resident)).Items); // the in-app copy was delivered regardless
            Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync("/notifications/log")).StatusCode);

            // still failing on the retry: attempts climb, one inbox message
            await Sql("UPDATE outbox_messages SET next_attempt_at = now() WHERE organization_id = @o AND processed_at IS NULL", ("o", s.Org));
            await Drain();
            Assert.Equal(2, Assert.Single(await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log?status=Failed"))).Attempts);

            Flaky.Fail = false;
            await Sql("UPDATE outbox_messages SET next_attempt_at = now() WHERE organization_id = @o AND processed_at IS NULL", ("o", s.Org));
            await Drain();
        }
        finally { Flaky.Fail = false; }

        Assert.Empty(await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log?status=Failed")));
        var sms = Assert.Single((await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log"))).Where(d => d.Channel == "test_sms"));
        Assert.Equal(("Sent", 3), (sms.Status, sms.Attempts));
        Assert.Single((await Inbox(s.Resident)).Items);
        Assert.Contains("Complaint received", Flaky.Sent);
    }

    // Escalation -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Overdue_jobs_escalate_once_per_level_to_supervisors_then_managers()
    {
        var s = await Society.CreateAsync(app);
        var c = await Complain(s);
        await JobFor(s, c.Id);
        await Sql("UPDATE complaints SET due_at = now() - interval '2 days' WHERE id = @id", ("id", c.Id));

        var scanner = app.Services.GetRequiredService<EscalationScanner>();
        Assert.True(await scanner.RunAsync(default) >= 1);
        await Drain();
        foreach (var boss in new[] { s.Supervisor, s.Manager })
        {
            var alert = Assert.Single((await Inbox(boss)).Items.Where(n => n.Template == "escalation_target_passed"));
            Assert.Contains(c.Reference, alert.Body);
        }
        Assert.DoesNotContain((await Inbox(s.Resident)).Items, n => n.Template == "escalation_target_passed");

        await scanner.RunAsync(default);
        await Drain();
        Assert.Single((await Inbox(s.Supervisor)).Items.Where(n => n.Template == "escalation_target_passed"));
    }

    // NT-05 -----------------------------------------------------------------------------------------------------------

    private HubConnection Connect(ApiSession who) => new HubConnectionBuilder()
        .WithUrl(new Uri(app.Server.BaseAddress, NotificationHub.Path), o =>
        {
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(who.Token);
        }).Build();

    [Fact]
    public async Task Notifications_arrive_in_an_open_session_without_a_refresh_and_only_for_that_user()
    {
        var s = await Society.CreateAsync(app);
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var neighbourGot = new List<NotificationDto>();

        await using var mine = Connect(s.Resident);
        mine.On<NotificationDto>("notification", n => received.TrySetResult(n));
        await using var theirs = Connect(s.Neighbour);
        theirs.On<NotificationDto>("notification", n => { lock (neighbourGot) neighbourGot.Add(n); });
        await mine.StartAsync();
        await theirs.StartAsync();

        var c = await Complain(s);
        await Drain();

        var pushed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("complaint_received", pushed.Template);
        Assert.Contains(c.Reference, pushed.Body);
        await Task.Delay(300);
        lock (neighbourGot) Assert.Empty(neighbourGot);
    }

    [Fact]
    public async Task The_hub_refuses_anonymous_and_unverified_connections()
    {
        var anonymous = new HubConnectionBuilder().WithUrl(new Uri(app.Server.BaseAddress, NotificationHub.Path), o =>
        {
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
        }).Build();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());

        var s = await Society.CreateAsync(app);
        var bogus = new HubConnectionBuilder().WithUrl(new Uri(app.Server.BaseAddress, NotificationHub.Path), o =>
        {
            o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>("not-a-real-token");
        }).Build();
        await Assert.ThrowsAnyAsync<Exception>(() => bogus.StartAsync());
        _ = s;
    }
}
