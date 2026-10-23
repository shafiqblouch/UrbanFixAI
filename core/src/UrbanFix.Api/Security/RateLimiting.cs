using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace UrbanFix.Api.Security;

/// <summary>
/// Throttling for the endpoints anyone can call (ID-09). The budget is per client address, so one noisy caller
/// cannot use up another organization's allowance. Per-phone limits for OTP live in <c>OtpService</c>.
/// </summary>
public static class RateLimiting
{
    public const string PublicPolicy = "public";
    /// <summary>For the webhooks of services we depend on: they send bursts, so the budget is far larger. Authenticity is checked by signature, not by this.</summary>
    public const string WebhookPolicy = "webhook";

    public static IServiceCollection AddUrbanFixRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        var perMinute = config.GetValue("RateLimits:PublicPerMinute", 30);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(PublicPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy(WebhookPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = Math.Max(perMinute * 40, 1200), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry)) ctx.HttpContext.Response.Headers.RetryAfter = ((int)retry.TotalSeconds).ToString();
                ctx.HttpContext.Response.ContentType = "application/json";
                await ctx.HttpContext.Response.WriteAsync("{\"error\":\"rate_limited\"}", ct);
            };
        });
        return services;
    }
}
