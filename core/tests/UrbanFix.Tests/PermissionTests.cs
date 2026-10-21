using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

[Collection("db")]
public class PermissionTests(TestApp app)
{
    [Fact]
    public void Every_endpoint_declares_authorization_or_is_explicitly_anonymous()
    {
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        Assert.NotEmpty(endpoints);

        var unprotected = endpoints.Where(e =>
            e.Metadata.GetMetadata<IAllowAnonymous>() is null &&
            !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any())
            .Select(e => e.RoutePattern.RawText).ToList();

        Assert.True(unprotected.Count == 0, "Endpoints with no authorization metadata: " + string.Join(", ", unprotected));
    }

    [Fact]
    public void Anonymous_endpoints_are_exactly_the_expected_set()
    {
        var anonymous = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText!).Order().ToArray();

        Assert.Equal(["/auth/login", "/health/live", "/health/ready", "/openapi/{documentName}.json"], anonymous);
    }

    [Fact]
    public void Every_permission_is_granted_to_at_least_one_role_and_roles_only_hold_known_permissions()
    {
        var granted = Enum.GetValues<Role>().SelectMany(r => RolePermissions.For(r)).ToHashSet();
        Assert.Empty(Permissions.All.Except(granted));
        Assert.Empty(granted.Except(Permissions.All));
    }

    [Fact]
    public void Resident_cannot_hold_any_staff_permission()
    {
        var staff = new[]
        {
            Permissions.ComplaintAssign, Permissions.ComplaintTriage, Permissions.GateLog, Permissions.GateManage,
            Permissions.MembershipManage, Permissions.AuditRead, Permissions.OrganizationManage, Permissions.PaymentRecord,
        };
        Assert.All(staff, p => Assert.False(RolePermissions.Allows(Role.Resident, p), p));
    }

    // endpoint -> the single permission it needs
    public static TheoryData<string, string, string> ProtectedEndpoints => new()
    {
        { "GET", "/members", Permissions.MembershipManage },
        { "GET", "/audit", Permissions.AuditRead },
        { "GET", "/admin/organizations", Permissions.OrganizationManage },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Each_role_gets_200_or_403_exactly_as_the_matrix_says(string method, string path, string permission)
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        foreach (var role in Enum.GetValues<Role>().Where(r => r != Role.PlatformAdmin))
        {
            var session = role == Role.Manager ? manager : await app.AddMemberAsync(manager, org, role);
            var res = await session.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            var expected = RolePermissions.Allows(role, permission) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
            Assert.True(expected == res.StatusCode, $"{role} {method} {path}: expected {expected}, got {res.StatusCode}");
        }

        var admin = await app.LoginPlatformAdminAsync();
        var adminRes = await admin.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        var adminExpected = RolePermissions.Allows(Role.PlatformAdmin, permission) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
        Assert.Equal(adminExpected, adminRes.StatusCode);
    }

    [Fact]
    public async Task Resident_token_cannot_reach_staff_endpoints()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var resident = await app.AddMemberAsync(manager, org, Role.Resident);
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.PostAsJsonAsync("/members",
            new AdminEndpoints.AddMemberRequest("x@x.test", "x", TestSecrets.UserPassword, Role.Guard, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.GetAsync("/admin/jobs")).StatusCode);
    }

    [Fact]
    public async Task Only_platform_organization_can_hold_platform_admins_and_managers_cannot_mint_them()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        var res = await manager.Client.PostAsJsonAsync("/members",
            new AdminEndpoints.AddMemberRequest("evil@x.test", "Evil", TestSecrets.UserPassword, Role.PlatformAdmin, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var admin = await app.LoginPlatformAdminAsync();
        var asRegular = await admin.Client.PostAsJsonAsync("/members",
            new AdminEndpoints.AddMemberRequest("misrole@x.test", "x", TestSecrets.UserPassword, Role.Manager, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, asRegular.StatusCode);
    }

    [Fact]
    public async Task Only_platform_admin_can_create_organizations()
    {
        var (_, manager) = await app.CreateOrganizationAsync();
        var res = await manager.Client.PostAsJsonAsync("/admin/organizations",
            new AdminEndpoints.CreateOrganizationRequest("x", "x-" + Guid.NewGuid().ToString("N")[..6], "m@x.test", "M", TestSecrets.UserPassword));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Scoped_membership_applies_only_to_its_department_or_gate()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        var dept = Guid.NewGuid();
        var other = Guid.NewGuid();
        var scoped = await app.AddMemberAsync(manager, org, Role.Supervisor, departmentId: dept);

        await using var scope = app.Services.CreateAsyncScope();
        var tenant = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var me = await scoped.Client.GetFromJsonAsync<AuthEndpoints.MeResponse>("/me");
        tenant.Set(org, me!.UserId);
        var memberships = scope.ServiceProvider.GetRequiredService<UrbanFix.Api.Security.CurrentMemberships>();

        Assert.True(await memberships.CanAsync(Permissions.ComplaintAssign, dept, null, default));
        Assert.False(await memberships.CanAsync(Permissions.ComplaintAssign, other, null, default));
        Assert.False(await memberships.CanAsync(Permissions.ComplaintAssign, null, null, default));
        Assert.False(await memberships.CanAsync(Permissions.GateManage, dept, null, default));
    }
}
