using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UrbanFix.Infrastructure.Push;

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

/// <summary>
/// Encrypts a message for one browser, as the Web Push standards require (RFC 8291, "aes128gcm"). The push service carries the
/// message but cannot read it. Checked against the worked example in the RFC's appendix.
/// </summary>
public static class WebPushEncryption
{
    public const int RecordSize = 4096;

    /// <param name="uaPublic">The browser's public key (65 bytes), from its subscription.</param>
    /// <param name="authSecret">The browser's 16-byte authentication secret, from its subscription.</param>
    /// <param name="asPrivate">Our one-time key. Null generates a fresh one; the tests pass the RFC's.</param>
    /// <param name="salt">16 random bytes. Null generates them; the tests pass the RFC's.</param>
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, byte[]? asPrivate = null, byte[]? salt = null)
    {
        if (uaPublic.Length != 65 || uaPublic[0] != 4) throw new CryptographicException("The browser's public key must be an uncompressed P-256 point.");
        if (authSecret.Length != 16) throw new CryptographicException("The authentication secret must be 16 bytes.");
        if (plaintext.Length > RecordSize - 17 - 1) throw new CryptographicException("The message is too long for one record.");
        salt ??= RandomNumberGenerator.GetBytes(16);

        using var ours = asPrivate is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : FromPrivate(asPrivate);
        var asPublic = PublicPoint(ours);
        using var theirs = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = uaPublic[1..33], Y = uaPublic[33..65] },
        });
        var secret = ours.DeriveRawSecretAgreement(theirs.PublicKey);

        var prkKey = HKDF.Extract(HashAlgorithmName.SHA256, secret, authSecret);
        var keyInfo = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(uaPublic).Concat(asPublic).ToArray();
        var ikm = HKDF.Expand(HashAlgorithmName.SHA256, prkKey, 32, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));

        var padded = plaintext.Append((byte)2).ToArray(); // 0x02 marks the last record
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, padded, cipher, tag);

        var header = salt.Concat(new byte[] { 0, 0, (byte)(RecordSize >> 8), (byte)(RecordSize & 0xFF), 65 }).Concat(asPublic);
        return header.Concat(cipher).Concat(tag).ToArray();
    }

    private static ECDiffieHellman FromPrivate(byte[] d)
    {
        // The public half is worked out from the private scalar, so only the private key has to be given.
        using var temp = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = d });
        var p = temp.ExportParameters(true);
        return ECDiffieHellman.Create(p);
    }

    private static byte[] PublicPoint(ECDiffieHellman key)
    {
        var p = key.ExportParameters(false);
        return new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray();
    }
}

public static class VapidKeys
{
    /// <summary>A new key pair for a server to identify itself to push services. Set the two values in configuration.</summary>
    public static (string Public, string Private) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (Base64Url.Encode(new byte[] { 4 }.Concat(p.Q.X!).Concat(p.Q.Y!).ToArray()), Base64Url.Encode(p.D!));
    }

    /// <summary>The signed token that proves to a push service which server is calling (RFC 8292).</summary>
    public static string Token(string audience, string subject, string privateKey, string publicKey, DateTimeOffset expires)
    {
        var pub = Base64Url.Decode(publicKey);
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256, D = Base64Url.Decode(privateKey), Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp = expires.ToUnixTimeSeconds(), sub = subject }));
        var signature = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256); // raw r||s, as JWT wants
        return $"{header}.{claims}.{Base64Url.Encode(signature)}";
    }
}

/// <summary>Push to browsers (the portal) through the browser's own push service, VAPID-signed and encrypted (FD-16).</summary>
public sealed class WebPushSender(HttpClient http, WebPushOptions options) : IPushSender
{
    public string Kind => PushKinds.Web;

    public async Task<PushResult> SendAsync(PushDevice device, PushMessage message, CancellationToken ct)
    {
        if (device.Keys is null) return new PushResult(PushOutcome.Gone, null, "no_keys");
        using var keys = JsonDocument.Parse(device.Keys);
        byte[] payload;
        try
        {
            payload = WebPushEncryption.Encrypt(
                JsonSerializer.SerializeToUtf8Bytes(new { title = message.Title, body = message.Body, link = message.Link, id = message.NotificationId }),
                Base64Url.Decode(keys.RootElement.GetProperty("p256dh").GetString()!), Base64Url.Decode(keys.RootElement.GetProperty("auth").GetString()!));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return new PushResult(PushOutcome.Gone, null, "bad_keys"); // a subscription that cannot be encrypted for will never work
        }

        var endpoint = new Uri(device.Token);
        var jwt = VapidKeys.Token($"{endpoint.Scheme}://{endpoint.Authority}", options.Subject, options.PrivateKey, options.PublicKey, DateTimeOffset.UtcNow.AddHours(12));
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt}, k={options.PublicKey}");
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "normal");

        using var response = await http.SendAsync(request, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted => new PushResult(PushOutcome.Sent, response.Headers.Location?.ToString()),
            // 404 and 410: the subscription has expired or the person withdrew permission.
            HttpStatusCode.NotFound or HttpStatusCode.Gone => new PushResult(PushOutcome.Gone, null, $"http_{(int)response.StatusCode}"),
            HttpStatusCode.TooManyRequests => new PushResult(PushOutcome.Retry, null, "http_429"),
            _ when (int)response.StatusCode >= 500 => new PushResult(PushOutcome.Retry, null, $"http_{(int)response.StatusCode}"),
            // Any other 4xx is a request the service will never accept (our key, a malformed subscription): not worth retrying.
            _ => new PushResult(PushOutcome.Gone, null, $"http_{(int)response.StatusCode}"),
        };
    }
}
