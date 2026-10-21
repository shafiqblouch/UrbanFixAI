using UrbanFix.Identity;
using UrbanFix.SharedKernel;

namespace UrbanFix.Tests;

public class CredentialUnitTests
{
    [Theory]
    [InlineData("short1A!", false)]
    [InlineData("alllowercaseletters", false)]
    [InlineData("Correct-Horse-9!", true)]
    [InlineData("Passw0rdPassw0rd", true)]
    public void Password_policy(string password, bool ok) => Assert.Equal(ok, PasswordService.Validate(password) is null);

    [Fact]
    public void Password_hash_verifies_only_the_right_password()
    {
        var svc = new PasswordService();
        var user = new User { PasswordHash = svc.Hash("Correct-Horse-9!") };
        Assert.True(svc.Verify(user, "Correct-Horse-9!"));
        Assert.False(svc.Verify(user, "Correct-Horse-8!"));
    }

    [Fact]
    public void Totp_accepts_current_and_adjacent_step_only()
    {
        var secret = TotpService.NewSecret();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(TotpService.Verify(secret, TotpService.CodeAt(secret, now), now));
        Assert.True(TotpService.Verify(secret, TotpService.CodeAt(secret, now.AddSeconds(-30)), now));
        Assert.False(TotpService.Verify(secret, TotpService.CodeAt(secret, now.AddMinutes(-5)), now));
        Assert.False(TotpService.Verify(secret, "abcdef", now));
        Assert.False(TotpService.Verify(secret, "12345", now));
    }

    [Fact]
    public void Session_tokens_are_unique_url_safe_and_stored_hashed()
    {
        var a = SessionTokens.NewToken();
        var b = SessionTokens.NewToken();
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, SessionTokens.Hash(a));
        Assert.Equal(SessionTokens.Hash(a), SessionTokens.Hash(a));
    }
}

public class DomainEventTests
{
    private sealed record Sample(Guid OrganizationId) : DomainEvent(OrganizationId);

    [Fact]
    public void Events_get_unique_ids_and_timestamps()
    {
        var org = Guid.NewGuid();
        var a = new Sample(org);
        var b = new Sample(org);
        Assert.NotEqual(a.EventId, b.EventId);
        Assert.Equal(org, a.OrganizationId);
        Assert.True(a.OccurredAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Tenant_context_starts_empty_and_can_be_set()
    {
        var ctx = new TenantContext();
        Assert.Null(ctx.OrganizationId);
        var org = Guid.NewGuid();
        ctx.Set(org, null);
        Assert.Equal(org, ctx.OrganizationId);
    }
}
