using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UrbanFix.Infrastructure.Push;

/// <summary>
/// Push to the mobile app through Firebase Cloud Messaging's HTTP v1 API (FD-16). FCM also reaches iPhones through Apple's push
/// service once an APNs key is added in the Firebase console, so one sender covers both. Not yet run against the real service:
/// that needs the Firebase project (see docs/push-setup.md). It is tested against a stand-in that checks what is sent.
/// </summary>
public sealed class FcmPushSender(HttpClient http, FcmOptions options) : IPushSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public string Kind => PushKinds.Fcm;

    public async Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await AccessTokenAsync(ct, force: attempt > 0);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.ApiBase.TrimEnd('/')}/v1/projects/{options.ProjectId}/messages:send");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = JsonContent.Create(new
            {
                message = new
                {
                    token = device.Token,
                    notification = new { title = message.Title, body = message.Body },
                    data = new Dictionary<string, string> { ["link"] = message.Link ?? "", ["id"] = message.NotificationId?.ToString() ?? "" },
                    android = new { priority = "HIGH" },
                },
            });
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue; // the cached token went stale: get a new one once

            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return new PushResult(PushOutcome.Sent, JsonDocument.Parse(body).RootElement.TryGetProperty("name", out var name) ? name.GetString() : null);

            var status = Status(body);
            // UNREGISTERED: the app was removed. INVALID_ARGUMENT: the token was never valid. Neither will start working.
            if (status is "UNREGISTERED" or "INVALID_ARGUMENT" or "NOT_FOUND" || response.StatusCode == HttpStatusCode.NotFound)
                return new PushResult(PushOutcome.Gone, null, status ?? "not_found");
            return new PushResult(PushOutcome.Retry, null, status ?? $"http_{(int)response.StatusCode}");
        }
        return new PushResult(PushOutcome.Retry, null, "unauthorized");
    }

    private static string? Status(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("status").GetString(); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>Signs in as the service account (a signed assertion exchanged for a short-lived token) and keeps the token until shortly before it expires.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken ct, bool force)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _token is not null && _tokenExpires > DateTimeOffset.UtcNow.AddMinutes(2)) return _token;

            using var account = JsonDocument.Parse(options.ServiceAccountJson);
            var email = account.RootElement.GetProperty("client_email").GetString()!;
            var tokenUri = account.RootElement.TryGetProperty("token_uri", out var uri) ? uri.GetString()! : "https://oauth2.googleapis.com/token";
            using var rsa = RSA.Create();
            rsa.ImportFromPem(account.RootElement.GetProperty("private_key").GetString()!);

            var now = DateTimeOffset.UtcNow;
            var header = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
            var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = email, scope = Scope, aud = tokenUri, iat = now.ToUnixTimeSeconds(), exp = now.AddHours(1).ToUnixTimeSeconds() }));
            var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var assertion = $"{header}.{claims}.{Base64Url.Encode(signature)}";

            using var response = await http.PostAsync(tokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = assertion,
            }), ct);
            response.EnsureSuccessStatusCode();
            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            _token = reply.RootElement.GetProperty("access_token").GetString()!;
            _tokenExpires = now.AddSeconds(reply.RootElement.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3000);
            return _token;
        }
        finally { _gate.Release(); }
    }
}
