using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.NoticeEndpoints;
using static UrbanFix.Api.Endpoints.NotificationEndpoints;
using static UrbanFix.Api.Endpoints.SetupEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class NoticeTests(TestApp app)
{
    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private static async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static async Task<Guid> UserId(ApiSession s) => (await s.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.UserId;

    /// <summary>Two blocks with a house each. Block A: an owner and a tenant. Block B: an owner. Plus a resident tied to no house.</summary>
    private sealed record Estate(Guid Org, ApiSession Manager, Guid BlockA, Guid BlockB, ApiSession OwnerA, ApiSession TenantA, ApiSession OwnerB, ApiSession Loose);

    private async Task<Estate> BuildEstate()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        async Task<LocationDto> Loc(Guid? parent, string kind, string name) =>
            await Ok<LocationDto>(await m.Client.PostAsJsonAsync("/locations", new CreateLocation(parent, kind, name, null, null, null)));
        var blockA = await Loc(null, "block", "Block A");
        var blockB = await Loc(null, "block", "Block B");
        var houseA = await Ok<HouseDto>(await m.Client.PostAsJsonAsync("/houses", new CreateHouse((await Loc(blockA.Id, "house", "A1")).Id, null, null, null)));
        var houseB = await Ok<HouseDto>(await m.Client.PostAsJsonAsync("/houses", new CreateHouse((await Loc(blockB.Id, "house", "B1")).Id, null, null, null)));

        var ownerA = await app.AddMemberAsync(m, org, Role.Resident);
        var tenantA = await app.AddMemberAsync(m, org, Role.Resident);
        var ownerB = await app.AddMemberAsync(m, org, Role.Resident);
        var loose = await app.AddMemberAsync(m, org, Role.Resident);
        async Task Link(Guid house, ApiSession who, string relation) =>
            await Ok<ResidentLinkDto>(await m.Client.PostAsJsonAsync($"/houses/{house}/residents", new CreateResidentLink(await UserId(who), relation, new DateOnly(2020, 1, 1))));
        await Link(houseA.Id, ownerA, "owner");
        await Link(houseA.Id, tenantA, "tenant");
        await Link(houseB.Id, ownerB, "owner");
        return new Estate(org, m, blockA.Id, blockB.Id, ownerA, tenantA, ownerB, loose);
    }

    private static CreateNotice Notice(string kind, Guid? location = null, DateTimeOffset? at = null, string? ur = null) =>
        new("Water tank cleaning", "The tank is cleaned on Friday.", ur is null ? null : "ٹینکی کی صفائی", ur, kind, location, at);

    private async Task<NoticeDto> Send(Estate e, CreateNotice req)
    {
        var notice = await Ok<NoticeDto>(await e.Manager.Client.PostAsJsonAsync("/notices", req));
        await Drain();
        return notice;
    }

    private static async Task<List<MyNoticeDto>> Mine(ApiSession s) => (await s.Client.GetFromJsonAsync<List<MyNoticeDto>>("/me/notices"))!;

    [Fact]
    public async Task A_notice_to_everyone_reaches_every_resident_and_nobody_else()
    {
        var e = await BuildEstate();
        await Send(e, Notice(NoticeAudience.All));

        foreach (var who in new[] { e.OwnerA, e.TenantA, e.OwnerB, e.Loose }) Assert.Single(await Mine(who));
        Assert.Empty(await Mine(e.Manager));
    }

    [Fact]
    public async Task A_street_limits_the_audience_to_the_houses_under_it()
    {
        var e = await BuildEstate();
        await Send(e, Notice(NoticeAudience.All, e.BlockA));

        Assert.Single(await Mine(e.OwnerA));
        Assert.Single(await Mine(e.TenantA));
        Assert.Empty(await Mine(e.OwnerB));
        Assert.Empty(await Mine(e.Loose));
    }

    [Fact]
    public async Task Owners_and_tenants_can_be_addressed_separately()
    {
        var e = await BuildEstate();
        await Send(e, Notice(NoticeAudience.Owners));
        Assert.Single(await Mine(e.OwnerA));
        Assert.Single(await Mine(e.OwnerB));
        Assert.Empty(await Mine(e.TenantA));

        await Send(e, Notice(NoticeAudience.Tenants, e.BlockA));
        Assert.Single(await Mine(e.TenantA));
        Assert.Single(await Mine(e.OwnerA)); // still only the earlier one
    }

    [Fact]
    public async Task Someone_who_moved_out_is_no_longer_in_the_audience()
    {
        var e = await BuildEstate();
        var houses = await e.Manager.Client.GetFromJsonAsync<List<HouseDto>>($"/houses?underLocationId={e.BlockA}");
        var links = await e.Manager.Client.GetFromJsonAsync<List<ResidentLinkDto>>($"/houses/{houses![0].Id}/residents");
        var tenantLink = links!.Single(l => l.Relation == "tenant");
        Assert.True((await e.Manager.Client.PostAsJsonAsync($"/resident-links/{tenantLink.Id}/end", new EndResidentLink(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))))).IsSuccessStatusCode);

        // Nobody is a tenant any more, so there is no one to send to.
        var refused = await e.Manager.Client.PostAsJsonAsync("/notices", Notice(NoticeAudience.Tenants));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await Send(e, Notice(NoticeAudience.All, e.BlockA));
        Assert.Single(await Mine(e.OwnerA));
        Assert.Empty(await Mine(e.TenantA));
    }

    [Fact]
    public async Task The_recipient_gets_the_urdu_text_when_their_language_is_urdu_and_english_otherwise()
    {
        var e = await BuildEstate();
        Assert.True((await e.OwnerA.Client.PutAsJsonAsync("/me/preferences", new SavePreferences("ur", []))).IsSuccessStatusCode);
        await Send(e, Notice(NoticeAudience.All, ur: "ٹینکی جمعہ کو صاف ہوگی۔"));

        Assert.Equal("ٹینکی کی صفائی", Assert.Single(await Mine(e.OwnerA)).Title);
        Assert.Equal("Water tank cleaning", Assert.Single(await Mine(e.OwnerB)).Title);
    }

    [Fact]
    public async Task The_sender_sees_delivered_and_read_counts_and_who_read()
    {
        var e = await BuildEstate();
        var notice = await Send(e, Notice(NoticeAudience.All));

        var before = await Ok<NoticeDto>(await e.Manager.Client.GetAsync($"/notices/{notice.Id}"));
        Assert.Equal((4, 4, 0, 0), (before.Recipients, before.Delivered, before.Read, before.Failed));

        var mine = Assert.Single(await Mine(e.OwnerA));
        Assert.True((await e.OwnerA.Client.PostAsync($"/notifications/{mine.Id}/read", null)).IsSuccessStatusCode);

        var after = await Ok<NoticeDto>(await e.Manager.Client.GetAsync($"/notices/{notice.Id}"));
        Assert.Equal(1, after.Read);
        var readers = await Ok<List<ReaderDto>>(await e.Manager.Client.GetAsync($"/notices/{notice.Id}/readers"));
        Assert.Equal(4, readers.Count);
        Assert.Equal(await UserId(e.OwnerA), Assert.Single(readers, r => r.ReadAt != null).UserId);
        Assert.Equal(after, Assert.Single(await Ok<List<NoticeDto>>(await e.Manager.Client.GetAsync("/notices"))));
    }

    [Fact]
    public async Task A_scheduled_notice_waits_until_its_time_and_can_be_called_back_until_then()
    {
        var e = await BuildEstate();
        var later = await Ok<NoticeDto>(await e.Manager.Client.PostAsJsonAsync("/notices", Notice(NoticeAudience.All, at: DateTimeOffset.UtcNow.AddHours(2))));
        var cancelled = await Ok<NoticeDto>(await e.Manager.Client.PostAsJsonAsync("/notices", Notice(NoticeAudience.All, at: DateTimeOffset.UtcNow.AddHours(3))));
        await Drain();
        Assert.Equal(NoticeStatus.Scheduled, later.Status);
        Assert.Empty(await Mine(e.OwnerA));

        Assert.Equal(NoticeStatus.Cancelled, (await Ok<NoticeDto>(await e.Manager.Client.PostAsync($"/notices/{cancelled.Id}/cancel", null))).Status);

        // The sweeper releases what is due; the cancelled one stays quiet.
        await using var scope = app.Services.CreateAsyncScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(e.Org, null);
        var released = await app.Services.GetRequiredService<NoticeSweeper>().ReleaseDueAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(), scope.ServiceProvider.GetRequiredService<NoticeService>(), DateTimeOffset.UtcNow.AddHours(4), default);
        await Drain();
        Assert.Equal(1, released);
        Assert.Single(await Mine(e.OwnerA));

        // Once sent, a notice cannot be called back.
        Assert.Equal(HttpStatusCode.Conflict, (await e.Manager.Client.PostAsync($"/notices/{later.Id}/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task Handling_the_same_release_twice_sends_nothing_twice()
    {
        var e = await BuildEstate();
        var notice = await Send(e, Notice(NoticeAudience.All));

        await using var scope = app.Services.CreateAsyncScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(e.Org, null);
        var service = scope.ServiceProvider.GetRequiredService<NoticeService>();
        var published = new NoticePublished(e.Org, notice.Id);
        await service.Handle(published, default);
        await service.Handle(published, default);
        Assert.Single(await Mine(e.OwnerA));
    }

    [Fact]
    public async Task Only_notice_managers_can_write_and_residents_cannot_see_the_sender_view()
    {
        var e = await BuildEstate();
        Assert.Equal(HttpStatusCode.Forbidden, (await e.OwnerA.Client.PostAsJsonAsync("/notices", Notice(NoticeAudience.All))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await e.OwnerA.Client.GetAsync("/notices")).StatusCode);
        var supervisor = await app.AddMemberAsync(e.Manager, e.Org, Role.Supervisor);
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.Client.PostAsJsonAsync("/notices", Notice(NoticeAudience.All))).StatusCode);
        var committee = await app.AddMemberAsync(e.Manager, e.Org, Role.Committee);
        Assert.Equal(HttpStatusCode.Created, (await committee.Client.PostAsJsonAsync("/notices", Notice(NoticeAudience.All))).StatusCode);
    }

    [Fact]
    public async Task Bad_notices_are_refused_with_a_reason()
    {
        var e = await BuildEstate();
        async Task<string> Refused(CreateNotice req)
        {
            var res = await e.Manager.Client.PostAsJsonAsync("/notices", req);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }
        Assert.Contains("invalid_title", await Refused(Notice("all") with { Title = " " }));
        Assert.Contains("invalid_body", await Refused(Notice("all") with { Body = new string('x', 4001) }));
        Assert.Contains("invalid_audience", await Refused(Notice("everybody")));
        Assert.Contains("invalid_urdu_text", await Refused(Notice("all") with { TitleUr = "ٹینکی" }));
        Assert.Contains("location_not_found", await Refused(Notice("all", Guid.NewGuid())));

        // A street with nobody in it: sending now is refused, so a notice is never "sent" to no one.
        var empty = await Ok<LocationDto>(await e.Manager.Client.PostAsJsonAsync("/locations", new CreateLocation(null, "block", "Block C", null, null, null)));
        Assert.Contains("no_recipients", await Refused(Notice("all", empty.Id)));
        Assert.Equal(0, (await Ok<AudienceDto>(await e.Manager.Client.GetAsync($"/notices/audience?kind=all&locationId={empty.Id}"))).Recipients);
        Assert.Equal(4, (await Ok<AudienceDto>(await e.Manager.Client.GetAsync("/notices/audience?kind=all"))).Recipients);
    }

    [Fact]
    public async Task One_organizations_notice_never_reaches_another()
    {
        var a = await BuildEstate();
        var b = await BuildEstate();
        var notice = await Send(a, Notice(NoticeAudience.All));
        Assert.Empty(await Mine(b.OwnerA));
        Assert.Equal(HttpStatusCode.NotFound, (await b.Manager.Client.GetAsync($"/notices/{notice.Id}")).StatusCode);
    }
}
