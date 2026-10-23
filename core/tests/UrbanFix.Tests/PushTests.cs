using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Infrastructure.Push;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.NotificationEndpoints;
using static UrbanFix.Api.Endpoints.PushEndpoints;

namespace UrbanFix.Tests;

public class WebPushCryptoTests
{
    // The worked example in RFC 8291, appendix A: fixed keys and salt, and the exact bytes that must come out.
    private const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    private const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    private const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    private const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    private const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    private const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    // The RFC prints the 86-byte header and the ciphertext separately; each is its own base64 text, so they are joined as bytes.
    private const string ExpectedHeader = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    private const string ExpectedCipher = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";

    [Fact]
    public void The_encryption_gives_exactly_the_bytes_in_the_standards_worked_example()
    {
        var result = WebPushEncryption.Encrypt(Base64Url.Decode(Plaintext), Base64Url.Decode(UaPublic), Base64Url.Decode(AuthSecret), Base64Url.Decode(AsPrivate), Base64Url.Decode(Salt));
        Assert.Equal(Base64Url.Decode(ExpectedHeader).Concat(Base64Url.Decode(ExpectedCipher)).ToArray(), result);
    }

    /// <summary>What a browser does with a push message, so a round trip proves it can be read by the one it was meant for.</summary>
    internal static byte[] Decrypt(byte[] message, byte[] uaPrivate, byte[] uaPublic, byte[] authSecret)
    {
        var salt = message[..16];
        var asPublic = message[21..86];
        var cipher = message[86..^16]; var tag = message[^16..];
        using var mine = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = uaPrivate, Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] } });
        using var theirs = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = asPublic[1..33], Y = asPublic[33..65] } });
        var secret = mine.DeriveRawSecretAgreement(theirs.PublicKey);
        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, secret, authSecret);
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, Encoding.ASCII.GetBytes("WebPush: info\0").Concat(uaPublic).Concat(asPublic).ToArray());
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, cipher, tag, plain);
        Assert.Equal(2, plain[^1]);
        return plain[..^1];
    }

    [Fact]
    public void A_fresh_message_can_be_read_by_the_browser_it_is_for_and_is_different_every_time()
    {
        using var browser = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = browser.ExportParameters(true);
        var pub = new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        var auth = RandomNumberGenerator.GetBytes(16);
        var text = Encoding.UTF8.GetBytes("""{"title":"شکایت موصول ہو گئی","body":"Your complaint UF-1 has been received."}""");

        var one = WebPushEncryption.Encrypt(text, pub, auth);
        var two = WebPushEncryption.Encrypt(text, pub, auth);
        Assert.NotEqual(one, two); // a new key and salt each time
        Assert.Equal(text, Decrypt(one, p.D!, pub, auth));
        Assert.Equal(text, Decrypt(two, p.D!, pub, auth));
    }

    [Fact]
    public void A_key_or_secret_of_the_wrong_size_is_refused()
    {
        Assert.Throws<CryptographicException>(() => WebPushEncryption.Encrypt([1], new byte[10], new byte[16]));
        Assert.Throws<CryptographicException>(() => WebPushEncryption.Encrypt([1], Base64Url.Decode(UaPublic), new byte[8]));
        Assert.Throws<CryptographicException>(() => WebPushEncryption.Encrypt(new byte[5000], Base64Url.Decode(UaPublic), Base64Url.Decode(AuthSecret)));
    }

    [Fact]
    public void The_server_identity_token_is_signed_and_names_who_it_is_for_and_how_long_it_lasts()
    {
        var (pub, priv) = VapidKeys.Generate();
        var expires = DateTimeOffset.UtcNow.AddHours(12);
        var jwt = VapidKeys.Token("https://push.example.org", "mailto:ops@example.org", priv, pub, expires).Split('.');

        var claims = JsonDocument.Parse(Base64Url.Decode(jwt[1])).RootElement;
        Assert.Equal("https://push.example.org", claims.GetProperty("aud").GetString());
        Assert.Equal("mailto:ops@example.org", claims.GetProperty("sub").GetString());
        Assert.Equal(expires.ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());

        var point = Base64Url.Decode(pub);
        using var verifier = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = point[1..33], Y = point[33..65] } });
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}"), Base64Url.Decode(jwt[2]), HashAlgorithmName.SHA256));
        Assert.False(verifier.VerifyData(Encoding.ASCII.GetBytes($"{jwt[0]}.{jwt[1]}x"), Base64Url.Decode(jwt[2]), HashAlgorithmName.SHA256)); // tamper-evident
    }
}

public class PushSenderTests
{
    private sealed class Capture(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct)));
            return await reply(request);
        }
    }

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };

    // Web ----------------------------------------------------------------------------------------------------------

    private static (PushDevice Device, byte[] Private, byte[] Public, byte[] Auth, WebPushOptions Options) Browser(string endpoint = "https://push.example.org/send/abc")
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        var pub = new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
        var auth = RandomNumberGenerator.GetBytes(16);
        var (vp, vs) = VapidKeys.Generate();
        var device = new PushDevice { Kind = PushKinds.Web, Token = endpoint, Keys = JsonSerializer.Serialize(new { p256dh = Base64Url.Encode(pub), auth = Base64Url.Encode(auth) }) };
        return (device, p.D!, pub, auth, new WebPushOptions { Enabled = true, Subject = "mailto:ops@example.org", PublicKey = vp, PrivateKey = vs });
    }

    [Fact]
    public async Task A_browser_gets_an_encrypted_message_the_push_service_cannot_read_signed_by_our_server()
    {
        var (device, priv, pub, auth, options) = Browser();
        var handler = new Capture(_ => Task.FromResult(Status(HttpStatusCode.Created)));
        var sender = new WebPushSender(new HttpClient(handler), options);

        var result = await sender.SendAsync(device, new PushMessage("Complaint received", "UF-1 has been received.", "/complaints/1", Guid.NewGuid()), default);
        Assert.Equal(PushOutcome.Sent, result.Outcome);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("https://push.example.org/send/abc", request.RequestUri!.ToString());
        Assert.Contains("aes128gcm", request.Content!.Headers.ContentEncoding);
        var authorization = request.Headers.GetValues("Authorization").Single();
        Assert.StartsWith("vapid t=", authorization);
        Assert.EndsWith($", k={options.PublicKey}", authorization);
        Assert.Equal("https://push.example.org", JsonDocument.Parse(Base64Url.Decode(authorization["vapid t=".Length..authorization.IndexOf(',')].Split('.')[1])).RootElement.GetProperty("aud").GetString());
        Assert.NotNull(request.Headers.GetValues("TTL").Single());

        var message = JsonDocument.Parse(WebPushCryptoTests.Decrypt(body, priv, pub, auth)).RootElement;
        Assert.Equal("Complaint received", message.GetProperty("title").GetString());
        Assert.Equal("/complaints/1", message.GetProperty("link").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, PushOutcome.Sent)]
    [InlineData(HttpStatusCode.Gone, PushOutcome.Gone)]          // the browser forgot the subscription
    [InlineData(HttpStatusCode.NotFound, PushOutcome.Gone)]
    [InlineData(HttpStatusCode.BadRequest, PushOutcome.Gone)]    // will never be accepted
    [InlineData(HttpStatusCode.TooManyRequests, PushOutcome.Retry)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PushOutcome.Retry)]
    public async Task What_the_push_service_answers_decides_whether_to_keep_the_device_or_try_again(HttpStatusCode status, PushOutcome expected)
    {
        var (device, _, _, _, options) = Browser();
        var sender = new WebPushSender(new HttpClient(new Capture(_ => Task.FromResult(Status(status)))), options);
        Assert.Equal(expected, (await sender.SendAsync(device, new PushMessage("t", "b", null, null), default)).Outcome);
    }

    [Fact]
    public async Task A_subscription_with_no_usable_keys_is_dropped_not_retried()
    {
        var (device, _, _, _, options) = Browser();
        var sender = new WebPushSender(new HttpClient(new Capture(_ => Task.FromResult(Status(HttpStatusCode.Created)))), options);
        device.Keys = null;
        Assert.Equal(PushOutcome.Gone, (await sender.SendAsync(device, new PushMessage("t", "b", null, null), default)).Outcome);
        device.Keys = """{"p256dh":"AAAA","auth":"AAAA"}""";
        Assert.Equal(PushOutcome.Gone, (await sender.SendAsync(device, new PushMessage("t", "b", null, null), default)).Outcome);
    }

    // FCM ----------------------------------------------------------------------------------------------------------

    private static (FcmOptions Options, RSA Key) Fcm()
    {
        var rsa = RSA.Create(2048);
        var account = JsonSerializer.Serialize(new { client_email = "push@urbanfix.iam.example", private_key = rsa.ExportPkcs8PrivateKeyPem(), token_uri = "https://oauth.example.org/token" });
        return (new FcmOptions { Enabled = true, ProjectId = "urbanfix-test", ServiceAccountJson = account, ApiBase = "https://fcm.example.org" }, rsa);
    }

    private static Func<HttpRequestMessage, Task<HttpResponseMessage>> FcmServer(Func<int, HttpResponseMessage> send, Action<int>? onToken = null)
    {
        var sends = 0; var tokens = 0;
        return request =>
        {
            if (request.RequestUri!.Host == "oauth.example.org") { tokens++; onToken?.Invoke(tokens); return Task.FromResult(Status(HttpStatusCode.OK, $$"""{"access_token":"tok-{{tokens}}","expires_in":3600}""")); }
            return Task.FromResult(send(++sends));
        };
    }

    [Fact]
    public async Task A_phone_gets_a_message_through_FCM_after_the_server_proves_who_it_is_and_the_proof_is_reused()
    {
        var (options, rsa) = Fcm();
        var handler = new Capture(FcmServer(_ => Status(HttpStatusCode.OK, """{"name":"projects/urbanfix-test/messages/0:1"}""")));
        var sender = new FcmPushSender(new HttpClient(handler), options);
        var device = new PushDevice { Kind = PushKinds.Fcm, Token = "phone-token-1" };

        var first = await sender.SendAsync(device, new PushMessage("Job assigned", "Job J-1 is yours.", "/jobs/1", Guid.NewGuid()), default);
        await sender.SendAsync(device, new PushMessage("Again", "Second.", null, null), default);
        Assert.Equal((PushOutcome.Sent, "projects/urbanfix-test/messages/0:1"), (first.Outcome, first.MessageId));

        // One sign-in covers both sends, and it was a properly signed assertion.
        var tokenCalls = handler.Calls.Where(c => c.Request.RequestUri!.Host == "oauth.example.org").ToList();
        Assert.Single(tokenCalls);
        var form = Encoding.UTF8.GetString(tokenCalls[0].Body);
        Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", form);
        var assertion = Uri.UnescapeDataString(form[(form.IndexOf("assertion=", StringComparison.Ordinal) + 10)..]).Split('.');
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{assertion[0]}.{assertion[1]}"), Base64Url.Decode(assertion[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var claims = JsonDocument.Parse(Base64Url.Decode(assertion[1])).RootElement;
        Assert.Equal("push@urbanfix.iam.example", claims.GetProperty("iss").GetString());
        Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", claims.GetProperty("scope").GetString());

        var sends = handler.Calls.Where(c => c.Request.RequestUri!.Host == "fcm.example.org").ToList();
        Assert.Equal(2, sends.Count);
        Assert.Equal("https://fcm.example.org/v1/projects/urbanfix-test/messages:send", sends[0].Request.RequestUri!.ToString());
        Assert.Equal("Bearer tok-1", sends[0].Request.Headers.Authorization!.ToString());
        var message = JsonDocument.Parse(sends[0].Body).RootElement.GetProperty("message");
        Assert.Equal("phone-token-1", message.GetProperty("token").GetString());
        Assert.Equal("Job assigned", message.GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("/jobs/1", message.GetProperty("data").GetProperty("link").GetString());
    }

    [Fact]
    public async Task A_stale_sign_in_is_renewed_once_and_the_message_still_goes()
    {
        var (options, _) = Fcm();
        var tokens = 0;
        var handler = new Capture(FcmServer(n => n == 1 ? Status(HttpStatusCode.Unauthorized) : Status(HttpStatusCode.OK, """{"name":"m"}"""), t => tokens = t));
        var result = await new FcmPushSender(new HttpClient(handler), options).SendAsync(new PushDevice { Kind = PushKinds.Fcm, Token = "t" }, new PushMessage("a", "b", null, null), default);
        Assert.Equal(PushOutcome.Sent, result.Outcome);
        Assert.Equal(2, tokens);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"status":"NOT_FOUND"}}""", PushOutcome.Gone, "NOT_FOUND")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"status":"INVALID_ARGUMENT"}}""", PushOutcome.Gone, "INVALID_ARGUMENT")]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"status":"UNREGISTERED"}}""", PushOutcome.Gone, "UNREGISTERED")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"error":{"status":"UNAVAILABLE"}}""", PushOutcome.Retry, "UNAVAILABLE")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":{"status":"QUOTA_EXCEEDED"}}""", PushOutcome.Retry, "QUOTA_EXCEEDED")]
    [InlineData(HttpStatusCode.BadGateway, "not json", PushOutcome.Retry, "http_502")]
    public async Task A_removed_app_is_dropped_and_a_busy_service_is_tried_again(HttpStatusCode status, string body, PushOutcome expected, string detail)
    {
        var (options, _) = Fcm();
        var sender = new FcmPushSender(new HttpClient(new Capture(FcmServer(_ => Status(status, body)))), options);
        var result = await sender.SendAsync(new PushDevice { Kind = PushKinds.Fcm, Token = "t" }, new PushMessage("a", "b", null, null), default);
        Assert.Equal((expected, detail), (result.Outcome, result.Detail));
    }
}

[Collection("db")]
public class PushTests(TestApp app)
{
    private FakePush Fake => app.Services.GetRequiredService<FakePush>();

    private async Task Drain()
    {
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private static async Task<T> Ok<T>(Task<HttpResponseMessage> call)
    {
        var res = await call;
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>())!;
    }

    private static string B64(int n) => Base64Url.Encode(RandomNumberGenerator.GetBytes(n));
    private static BrowserKeys Keys() => new(Base64Url.Encode(new byte[] { 4 }.Concat(RandomNumberGenerator.GetBytes(64)).ToArray()), B64(16));

    private static Task<HttpResponseMessage> Phone(ApiSession who, string token, string? label = "Pixel") => who.Client.PostAsJsonAsync("/me/push-devices", new RegisterDevice("fcm", token, null, label));

    [Fact]
    public async Task The_installation_says_what_it_can_push_to()
    {
        var s = await Society.CreateAsync(app);
        var config = await Ok<PushConfigDto>(s.Resident.Client.GetAsync("/push/config"));
        Assert.True(config.Fcm);
        Assert.True(config.Web);
    }

    [Fact]
    public async Task A_phone_and_a_browser_can_be_registered_and_listed_and_removed_by_their_owner_only()
    {
        var s = await Society.CreateAsync(app);
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        var phone = await Ok<DeviceDto>(Phone(s.Resident, token));
        var browser = await Ok<DeviceDto>(s.Resident.Client.PostAsJsonAsync("/me/push-devices", new RegisterDevice("web", "https://push.example.org/" + Guid.NewGuid().ToString("N"), Keys(), "Chrome")));
        Assert.Equal(2, (await Ok<List<DeviceDto>>(s.Resident.Client.GetAsync("/me/push-devices"))).Count);
        Assert.Empty(await Ok<List<DeviceDto>>(s.Neighbour.Client.GetAsync("/me/push-devices")));

        Assert.Equal(HttpStatusCode.NotFound, (await s.Neighbour.Client.DeleteAsync($"/me/push-devices/{phone.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Resident.Client.DeleteAsync($"/me/push-devices/{phone.Id}")).StatusCode);
        Assert.Equal(browser.Id, Assert.Single(await Ok<List<DeviceDto>>(s.Resident.Client.GetAsync("/me/push-devices"))).Id);
    }

    [Fact]
    public async Task Bad_registrations_are_refused_with_a_reason()
    {
        var s = await Society.CreateAsync(app);
        async Task<string> Refused(RegisterDevice req, HttpStatusCode code = HttpStatusCode.BadRequest)
        {
            var res = await s.Resident.Client.PostAsJsonAsync("/me/push-devices", req);
            Assert.Equal(code, res.StatusCode);
            return await res.Content.ReadAsStringAsync();
        }
        Assert.Contains("invalid_kind", await Refused(new RegisterDevice("pigeon", "x", null, null)));
        Assert.Contains("invalid_token", await Refused(new RegisterDevice("fcm", "", null, null)));
        Assert.Contains("invalid_token", await Refused(new RegisterDevice("web", "http://insecure.example.org/x", Keys(), null)));
        Assert.Contains("invalid_keys", await Refused(new RegisterDevice("web", "https://push.example.org/x", null, null)));
        Assert.Contains("invalid_keys", await Refused(new RegisterDevice("web", "https://push.example.org/x", new BrowserKeys("AAAA", "AAAA"), null)));
        Assert.Contains("invalid_keys", await Refused(new RegisterDevice("web", "https://push.example.org/x", new BrowserKeys("***", "***"), null)));
    }

    [Fact]
    public async Task A_test_push_reaches_the_phone_and_the_browser_and_says_what_happened_on_each()
    {
        var s = await Society.CreateAsync(app);
        Fake.Fcm.Reset(); Fake.Web.Reset();
        var phoneToken = "fcm-" + Guid.NewGuid().ToString("N");
        var browserToken = "https://push.example.org/" + Guid.NewGuid().ToString("N");
        await Phone(s.Resident, phoneToken);
        await s.Resident.Client.PostAsJsonAsync("/me/push-devices", new RegisterDevice("web", browserToken, Keys(), "Edge"));

        var result = await Ok<TestResultDto>(s.Resident.Client.PostAsync("/me/push-test", null));
        Assert.Equal((2, 0, 0), (result.Sent, result.Gone, result.Failed));
        Assert.Equal(["fcm", "web"], result.Devices.Select(d => d.Kind).Order().ToArray());
        Assert.Equal(phoneToken, Assert.Single(Fake.Fcm.Sent).Token);
        Assert.Equal(browserToken, Assert.Single(Fake.Web.Sent).Token);

        var nobody = await s.Neighbour.Client.PostAsync("/me/push-test", null);
        Assert.Equal(HttpStatusCode.Conflict, nobody.StatusCode);
        Assert.Contains("no_push_device", await nobody.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_device_the_service_says_is_gone_is_dropped_and_one_having_trouble_is_kept_and_counted()
    {
        var s = await Society.CreateAsync(app);
        Fake.Fcm.Reset(); Fake.Web.Reset();
        await Phone(s.Resident, "fcm-" + Guid.NewGuid().ToString("N"));

        Fake.Fcm.Outcome = PushOutcome.Retry;
        var busy = await Ok<TestResultDto>(s.Resident.Client.PostAsync("/me/push-test", null));
        Assert.Equal((0, 0, 1), (busy.Sent, busy.Gone, busy.Failed));
        var device = Assert.Single(await Ok<List<DeviceDto>>(s.Resident.Client.GetAsync("/me/push-devices")));
        Assert.Equal(1, device.Failures);

        Fake.Fcm.Outcome = PushOutcome.Gone;
        var gone = await Ok<TestResultDto>(s.Resident.Client.PostAsync("/me/push-test", null));
        Assert.Equal(1, gone.Gone);
        Assert.Empty(await Ok<List<DeviceDto>>(s.Resident.Client.GetAsync("/me/push-devices"))); // no longer notified
        Fake.Fcm.Reset();
    }

    [Fact]
    public async Task A_device_that_changes_hands_stops_notifying_the_old_person()
    {
        var s = await Society.CreateAsync(app);
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        await Phone(s.Resident, token);
        await Phone(s.Neighbour, token); // the same phone, now signed in as someone else

        Assert.Empty(await Ok<List<DeviceDto>>(s.Resident.Client.GetAsync("/me/push-devices")));
        Assert.Single(await Ok<List<DeviceDto>>(s.Neighbour.Client.GetAsync("/me/push-devices")));
    }

    [Fact]
    public async Task Allowing_notifications_on_a_device_turns_the_push_channel_on_for_the_person_and_it_is_offered_in_preferences()
    {
        var s = await Society.CreateAsync(app);
        var before = await Ok<PreferencesDto>(s.Resident.Client.GetAsync("/me/preferences"));
        Assert.Contains("push", before.AvailableChannels);
        Assert.DoesNotContain("push", before.Channels);

        await Phone(s.Resident, "fcm-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("push", (await Ok<PreferencesDto>(s.Resident.Client.GetAsync("/me/preferences"))).Channels);
    }

    [Fact]
    public async Task A_complaint_event_is_pushed_to_the_residents_phone_as_well_as_their_inbox()
    {
        var s = await Society.CreateAsync(app);
        Fake.Fcm.Reset(); Fake.Web.Reset();
        var token = "fcm-" + Guid.NewGuid().ToString("N");
        await Phone(s.Resident, token);

        var created = await Ok<CreatedDto>(s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null)));
        await Drain();

        var (sentTo, message) = Assert.Single(Fake.Fcm.Sent);
        Assert.Equal(token, sentTo);
        Assert.Equal("Complaint received", message.Title);
        Assert.Equal($"/complaints/{created.Id}", message.Link);
        var inbox = await Ok<InboxDto>(s.Resident.Client.GetAsync("/notifications"));
        Assert.Single(inbox.Items);

        // The delivery log shows it went out by push.
        var log = await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log"));
        Assert.Contains(log, d => d.Channel == "push" && d.Status == "Sent");
    }

    [Fact]
    public async Task A_person_with_the_channel_on_but_no_device_is_recorded_as_not_pushed_and_nothing_is_retried()
    {
        var s = await Society.CreateAsync(app);
        Fake.Fcm.Reset();
        Assert.True((await s.Resident.Client.PutAsJsonAsync("/me/preferences", new SavePreferences(null, ["push"]))).IsSuccessStatusCode);

        await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null));
        await Drain();

        Assert.Empty(Fake.Fcm.Sent);
        var log = await Ok<List<DeliveryDto>>(s.Manager.Client.GetAsync("/notifications/log?status=Failed"));
        var failed = Assert.Single(log, d => d.Channel == "push");
        Assert.Equal("no_push_device", failed.LastError);
        Assert.Equal(1, failed.Attempts); // permanent: not retried
    }

    [Fact]
    public async Task Platform_admins_can_make_a_key_pair_for_browser_push_and_nobody_else_can()
    {
        var admin = await app.LoginPlatformAdminAsync();
        var pair = await Ok<VapidKeyPair>(admin.Client.PostAsync("/admin/push/vapid-keys", null));
        Assert.Equal(65, Base64Url.Decode(pair.PublicKey).Length);
        Assert.Equal(32, Base64Url.Decode(pair.PrivateKey).Length);

        var s = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Manager.Client.PostAsync("/admin/push/vapid-keys", null)).StatusCode);
    }
}
