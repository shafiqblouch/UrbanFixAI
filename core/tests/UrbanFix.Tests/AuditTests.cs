using System.Net;
using Npgsql;
using UrbanFix.Api.Endpoints;
using UrbanFix.Identity;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

[Collection("db")]
public class AuditTests(TestApp app)
{
    [Fact]
    public async Task Audit_rows_cannot_be_updated_or_deleted_even_by_the_owner()
    {
        var (org, _) = await app.CreateOrganizationAsync();

        await using var owner = new NpgsqlConnection(app.OwnerConnectionString);
        await owner.OpenAsync();
        foreach (var sql in new[] { "UPDATE audit_log SET action = 'x'", "DELETE FROM audit_log", "TRUNCATE audit_log" })
        {
            await using var cmd = new NpgsqlCommand(sql, owner);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Contains("append-only", ex.MessageText);
        }

        // And the application role has no UPDATE/DELETE privilege at all.
        await using var conn = await app.OpenAppConnectionAsync(org);
        await using var upd = new NpgsqlCommand("UPDATE audit_log SET action = 'x'", conn);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync());
        Assert.Equal("42501", denied.SqlState);
    }

    [Fact]
    public async Task Membership_changes_are_audited_with_before_and_after_and_traceable_by_entity()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var add = await manager.Client.PostAsJsonAsync("/members",
            new AdminEndpoints.AddMemberRequest("audit-sub@x.test", "Audit Sub", TestSecrets.UserPassword, Role.Office, null, null));
        var member = (await add.Content.ReadFromJsonAsync<AdminEndpoints.MemberDto>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await manager.Client.DeleteAsync($"/members/{member.MembershipId}")).StatusCode);

        var trail = await manager.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>(
            $"/audit?entityType=Membership&entityId={member.MembershipId}&order=asc");
        Assert.Equal(["created", "updated"], trail!.Select(a => a.Action).ToArray());
        Assert.Null(trail![0].Before);
        Assert.Equal("Office", trail[0].After!.Value.GetProperty("Role").GetString());
        Assert.True(trail[1].Before!.Value.GetProperty("IsActive").GetBoolean());
        Assert.False(trail[1].After!.Value.GetProperty("IsActive").GetBoolean());
        Assert.All(trail, a => Assert.NotNull(a.ActorUserId));
        Assert.All(trail, a => Assert.False(string.IsNullOrEmpty(a.Device)) );
        _ = org;
    }

    [Fact]
    public async Task Audit_query_filters_by_actor_and_requires_the_audit_permission()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var resident = await app.AddMemberAsync(manager, org, Role.Resident);
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.GetAsync("/audit")).StatusCode);

        var me = await manager.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me");
        var mine = await manager.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>($"/audit?actorUserId={me!.UserId}");
        Assert.NotEmpty(mine!);
        Assert.All(mine!, a => Assert.Equal(me.UserId, a.ActorUserId));
    }

    [Fact]
    public async Task Platform_creating_an_organization_is_audited_in_the_platform_organization()
    {
        var admin = await app.LoginPlatformAdminAsync();
        var (orgId, _) = await app.CreateOrganizationAsync();
        var trail = await admin.Client.GetFromJsonAsync<List<AdminEndpoints.AuditDto>>($"/audit?entityType=Organization&entityId={orgId}");
        Assert.Single(trail!);
        Assert.Equal("created", trail![0].Action);
    }
}
