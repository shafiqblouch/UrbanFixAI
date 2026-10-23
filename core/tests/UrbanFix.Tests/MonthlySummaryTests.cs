using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Api.Reports;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.NotificationEndpoints;
using static UrbanFix.Api.Reports.MonthlySummaryEndpoints;

namespace UrbanFix.Tests;

public class MonthlySummaryRuleTests
{
    [Theory]
    [InlineData("2026-10-23", "2026-09-01", "2026-09-30")]
    [InlineData("2026-01-05", "2025-12-01", "2025-12-31")]
    [InlineData("2026-03-31", "2026-02-01", "2026-02-28")]
    public void The_summary_covers_the_whole_month_before_today(string today, string start, string end) =>
        Assert.Equal((DateOnly.Parse(start), DateOnly.Parse(end)), MonthlySummaryService.PreviousMonth(DateOnly.Parse(today)));
}

[Collection("db")]
public class MonthlySummaryTests(TestApp app)
{
    private static string LastMonth() => DateTime.UtcNow.AddMonths(-1).ToString("yyyy-MM");

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private static async Task<Guid> UserId(ApiSession s) => (await s.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static Task<ScheduleDto> Schedule(ApiSession who, bool enabled, params Guid[] recipients) =>
        Ok<ScheduleDto>(who.Client.PutAsJsonAsync("/reports/summary-schedule", new ScheduleDto(enabled, 1, "en", recipients)));

    [Fact]
    public async Task A_schedule_needs_a_valid_day_and_recipients_who_can_read_the_dashboards()
    {
        var s = await Society.CreateAsync(app);
        var manager = await UserId(s.Manager);
        async Task<string> Refused(ScheduleDto req)
        {
            var res = await s.Manager.Client.PutAsJsonAsync("/reports/summary-schedule", req);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }
        Assert.Contains("day_out_of_range", await Refused(new ScheduleDto(true, 30, null, [manager])));
        Assert.Contains("recipients_required", await Refused(new ScheduleDto(true, 1, null, [])));
        Assert.Contains("unsupported_locale", await Refused(new ScheduleDto(true, 1, "fr", [manager])));
        Assert.Contains("recipient_cannot_read_dashboards", await Refused(new ScheduleDto(true, 1, null, [await UserId(s.Technician)])));

        var saved = await Schedule(s.Manager, true, manager);
        Assert.True(saved.Enabled);
        Assert.Equal([manager], saved.RecipientUserIds);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Supervisor.Client.PutAsJsonAsync("/reports/summary-schedule", saved)).StatusCode);
    }

    [Fact]
    public async Task A_summary_is_made_once_per_month_kept_as_a_pdf_and_announced_to_the_recipients()
    {
        var s = await Society.CreateAsync(app);
        var supervisor = await UserId(s.Supervisor);
        await Schedule(s.Manager, true, await UserId(s.Manager), supervisor);

        var made = await s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(LastMonth()));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var summary = (await made.Content.ReadFromJsonAsync<SummaryDto>())!;
        Assert.Equal(LastMonth(), summary.Period);
        Assert.Equal(2, summary.Recipients);
        Assert.True(summary.SizeBytes > 5_000);

        // Asking again returns the same one and sends nothing new.
        var again = await Ok<SummaryDto>(s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(LastMonth())));
        Assert.Equal(summary.Id, again.Id);
        Assert.Single(await Ok<List<SummaryDto>>(s.Manager.Client.GetAsync("/reports/summaries")));

        var file = await s.Manager.Client.GetAsync($"/reports/summaries/{summary.Id}/download");
        Assert.Equal("application/pdf", file.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString((await file.Content.ReadAsByteArrayAsync())[..5]));

        await Drain();
        foreach (var who in new[] { s.Manager, s.Supervisor })
        {
            var inbox = await Ok<InboxDto>(who.Client.GetAsync("/notifications"));
            var note = Assert.Single(inbox.Items, n => n.Template == NotificationTemplates.MonthlySummary);
            Assert.Contains(LastMonth(), note.Body);
            Assert.Equal($"/reports/summaries/{summary.Id}", note.Link);
        }
    }

    [Fact]
    public async Task The_scheduled_run_makes_last_months_summary_when_it_is_due_and_only_once()
    {
        var s = await Society.CreateAsync(app);
        await Schedule(s.Manager, true, await UserId(s.Manager));

        await using var scope = app.Services.CreateAsyncScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(s.Org, null);
        var service = scope.ServiceProvider.GetRequiredService<MonthlySummaryService>();
        Assert.True(await service.RunDueAsync(DateTimeOffset.UtcNow, default));
        Assert.False(await service.RunDueAsync(DateTimeOffset.UtcNow, default));

        var list = await Ok<List<SummaryDto>>(s.Manager.Client.GetAsync("/reports/summaries"));
        var only = Assert.Single(list);
        Assert.True(only.Scheduled);
        Assert.Equal(LastMonth(), only.Period);
    }

    [Fact]
    public async Task A_disabled_schedule_or_one_not_yet_due_makes_nothing()
    {
        var s = await Society.CreateAsync(app);
        await using var scope = app.Services.CreateAsyncScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(s.Org, null);
        var service = scope.ServiceProvider.GetRequiredService<MonthlySummaryService>();

        Assert.False(await service.RunDueAsync(DateTimeOffset.UtcNow, default)); // no schedule at all
        await Schedule(s.Manager, false, await UserId(s.Manager));
        Assert.False(await service.RunDueAsync(DateTimeOffset.UtcNow, default)); // switched off
        await s.Manager.Client.PutAsJsonAsync("/reports/summary-schedule", new ScheduleDto(true, 28, "en", [await UserId(s.Manager)]));
        // On the 3rd of a month the 28th has not come.
        Assert.False(await service.RunDueAsync(new DateTimeOffset(2026, 10, 3, 6, 0, 0, TimeSpan.Zero), default));
    }

    [Fact]
    public async Task Costs_are_in_the_file_only_when_every_recipient_may_read_them()
    {
        var s = await Society.CreateAsync(app);
        await Schedule(s.Manager, true, await UserId(s.Manager), await UserId(s.Supervisor));
        var withCosts = await Ok<SummaryDto>(s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(LastMonth())));
        Assert.True(withCosts.IncludesCosts);

        var t = await Society.CreateAsync(app);
        await Schedule(t.Manager, true, await UserId(t.Manager), await UserId(t.Office));
        var without = await Ok<SummaryDto>(t.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(LastMonth())));
        Assert.False(without.IncludesCosts);
        Assert.Equal(HttpStatusCode.OK, (await t.Office.Client.GetAsync($"/reports/summaries/{without.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Only_finished_months_can_be_asked_for_and_other_societies_never_see_the_file()
    {
        var s = await Society.CreateAsync(app);
        await Schedule(s.Manager, true, await UserId(s.Manager));
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(DateTime.UtcNow.ToString("yyyy-MM")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary("last month"))).StatusCode);

        var made = await Ok<SummaryDto>(s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(LastMonth())));
        var other = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.Client.GetAsync($"/reports/summaries/{made.Id}/download")).StatusCode);
        Assert.Empty(await Ok<List<SummaryDto>>(other.Manager.Client.GetAsync("/reports/summaries")));
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync($"/reports/summaries/{made.Id}/download")).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_made_without_recipients()
    {
        var s = await Society.CreateAsync(app);
        var res = await s.Manager.Client.PostAsJsonAsync("/reports/summaries", new MakeSummary(LastMonth()));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("recipients_required", await res.Content.ReadAsStringAsync());
    }
}
