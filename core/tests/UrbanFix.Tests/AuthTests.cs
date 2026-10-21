using System.Net;
using System.Net.Http.Headers;
using UrbanFix.Api.Endpoints;
using UrbanFix.Identity;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

[Collection("db")]
public class AuthTests(TestApp app)
{
    [Fact]
    public async Task Wrong_password_and_unknown_user_are_indistinguishable()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        _ = manager;
        var client = app.CreateClient();
        var unknown = await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest("nobody@x.test", "Whatever-123456!", null, null));
        var wrong = await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest(TestSecrets.PlatformEmail, "Wrong-Pass-123456!", null, null));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task First_login_is_limited_to_totp_enrolment_until_confirmed()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var email = $"fresh-{Guid.NewGuid():N}@x.test";
        (await manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest(email, "Fresh", TestSecrets.UserPassword, Role.Office, null, null))).EnsureSuccessStatusCode();

        var client = app.CreateClient();
        var login = await (await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest(email, TestSecrets.UserPassword, null, org)))
            .Content.ReadFromJsonAsync<AuthEndpoints.LoginResponse>();
        Assert.True(login!.TotpEnrollmentRequired);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me")).StatusCode);

        var enroll = await (await client.PostAsync("/auth/totp/enroll", null)).Content.ReadFromJsonAsync<AuthEndpoints.TotpEnrollmentResponse>();
        Assert.StartsWith("otpauth://totp/", enroll!.ProvisioningUri);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/auth/totp/confirm", new AuthEndpoints.TotpConfirmRequest("000000"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/me")).StatusCode);

        var ok = await client.PostAsJsonAsync("/auth/totp/confirm", new AuthEndpoints.TotpConfirmRequest(TotpService.CodeAt(enroll.Secret, DateTime.UtcNow)));
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Enrolled_user_must_supply_a_valid_code_to_log_in()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var client = app.CreateClient();
        var email = (await manager.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.Email;

        var noCode = await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest(email, TestSecrets.UserPassword, null, org));
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Contains("totp_required", await noCode.Content.ReadAsStringAsync());

        var bad = await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest(email, TestSecrets.UserPassword, "123456", org));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        var good = await client.PostAsJsonAsync("/auth/login", new AuthEndpoints.LoginRequest(email, TestSecrets.UserPassword, TotpService.CodeAt(manager.TotpSecret, DateTime.UtcNow), org));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.False((await good.Content.ReadFromJsonAsync<AuthEndpoints.LoginResponse>())!.TotpEnrollmentRequired);
    }

    [Fact]
    public async Task Revoked_session_is_rejected_immediately()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        Assert.Equal(HttpStatusCode.OK, (await manager.Client.GetAsync("/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await manager.Client.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.Client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Expired_session_is_rejected()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        await using var owner = new Npgsql.NpgsqlConnection(app.OwnerConnectionString);
        await owner.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("UPDATE sessions SET expires_at = now() - interval '1 minute' WHERE token_hash = @h", owner);
        cmd.Parameters.AddWithValue("h", SessionTokens.Hash(manager.Token));
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, (await manager.Client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Garbage_and_missing_tokens_are_rejected()
    {
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task User_with_roles_in_two_organizations_switches_and_sees_only_that_data()
    {
        var (orgA, managerA) = await app.CreateOrganizationAsync();
        var (orgB, managerB) = await app.CreateOrganizationAsync();

        // Manager A also becomes an Office user in B.
        var email = (await managerA.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me"))!.Email;
        (await managerB.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest(email, "x", null, Role.Office, null, null))).EnsureSuccessStatusCode();

        var me = await managerA.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me");
        Assert.Equal(orgA, me!.OrganizationId);
        Assert.Equal(2, me.Memberships.Count);

        var membersInA = await managerA.Client.GetFromJsonAsync<List<AdminEndpoints.MemberDto>>("/members");
        Assert.DoesNotContain(membersInA!, m => m.Email.StartsWith("manager@") && m.Email.Contains("-") && m.Email != email);

        // Switch: now in B as Office, who may not list members, but the context changed.
        Assert.Equal(HttpStatusCode.NoContent, (await managerA.Client.PostAsJsonAsync("/auth/switch-organization", new AuthEndpoints.SwitchOrganizationRequest(orgB))).StatusCode);
        var after = await managerA.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me");
        Assert.Equal(orgB, after!.OrganizationId);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerA.Client.GetAsync("/members")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await managerA.Client.GetAsync("/audit")).StatusCode);
    }

    [Fact]
    public async Task Removed_membership_loses_access_without_a_new_login()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var office = await app.AddMemberAsync(manager, org, Role.Manager);
        Assert.Equal(HttpStatusCode.OK, (await office.Client.GetAsync("/members")).StatusCode);

        var members = await manager.Client.GetFromJsonAsync<List<AdminEndpoints.MemberDto>>("/members");
        var target = members!.Single(m => m.Role == Role.Manager && m.Email != (manager.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me").Result)!.Email);
        Assert.Equal(HttpStatusCode.NoContent, (await manager.Client.DeleteAsync($"/members/{target.MembershipId}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await office.Client.GetAsync("/members")).StatusCode);
    }

    [Fact]
    public async Task Weak_passwords_are_refused_for_new_users()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        var res = await manager.Client.PostAsJsonAsync("/members", new AdminEndpoints.AddMemberRequest("weak@x.test", "Weak", "short", Role.Office, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
