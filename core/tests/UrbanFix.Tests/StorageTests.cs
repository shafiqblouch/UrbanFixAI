using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.SharedKernel;

namespace UrbanFix.Tests;

public sealed class MinioFixture : IAsyncLifetime
{
    private readonly IContainer _minio = new ContainerBuilder()
        .WithImage("bitnamilegacy/minio:latest")
        .WithPortBinding(9000, true)
        .WithEnvironment("MINIO_ROOT_USER", "testkey")
        .WithEnvironment("MINIO_ROOT_PASSWORD", "testsecret-123")
        .WithEnvironment("MINIO_DEFAULT_BUCKETS", "test-media")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(9000).ForPath("/minio/health/live")))
        .Build();

    public StorageOptions Options { get; private set; } = new();
    public IAmazonS3 S3 { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _minio.StartAsync();
        Options = new StorageOptions
        {
            ServiceUrl = $"http://{_minio.Hostname}:{_minio.GetMappedPublicPort(9000)}",
            AccessKey = "testkey",
            SecretKey = "testsecret-123",
            Bucket = "test-media",
        };
        S3 = new AmazonS3Client(new BasicAWSCredentials(Options.AccessKey, Options.SecretKey),
            new AmazonS3Config { ServiceURL = Options.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        // The default-bucket bootstrap runs after the health endpoint comes up.
        for (var i = 0; i < 30; i++)
        {
            try { await S3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = Options.Bucket }); return; }
            catch (AmazonS3Exception) { await Task.Delay(500); }
        }
    }

    public Task DisposeAsync() => _minio.DisposeAsync().AsTask();
}

public class StorageTests(MinioFixture minio) : IClassFixture<MinioFixture>
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private ObjectStorage StorageFor(Guid org)
    {
        var tenant = new TenantContext();
        tenant.Set(org, null);
        return new ObjectStorage(minio.S3, minio.Options, tenant);
    }

    private static MemoryStream PhotoWithGpsExif()
    {
        using var image = new Image<Rgb24>(64, 48, new Rgb24(200, 30, 30));
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, new Rational[] { new(24, 1), new(51, 1), new(3852, 100) });
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, new Rational[] { new(67, 1), new(0, 1), new(400, 100) });
        exif.SetValue(ExifTag.DateTimeOriginal, "2026:10:21 09:30:00");
        exif.SetValue(ExifTag.Make, "SecretPhoneCo");
        image.Metadata.ExifProfile = exif;

        var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder());
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Location_is_extracted_then_the_stored_copy_carries_no_exif()
    {
        var storage = StorageFor(OrgA);
        await using var upload = PhotoWithGpsExif();

        // Sanity: the input really has GPS data.
        using (var probe = await Image.LoadAsync(upload)) Assert.NotNull(probe.Metadata.ExifProfile);
        upload.Position = 0;

        var stored = await storage.PutImageAsync(upload, default);

        Assert.NotNull(stored.Location);
        Assert.Equal(24.8607, stored.Location!.Latitude, 3);
        Assert.Equal(67.0011, stored.Location.Longitude, 3);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero), stored.CapturedAt);

        await using var read = await storage.OpenReadAsync(stored.Key, default);
        using var saved = await Image.LoadAsync(read);
        Assert.True(saved.Metadata.ExifProfile is null || saved.Metadata.ExifProfile.Values.Count == 0, "stored image still has EXIF");
        Assert.Equal((64, 48), (saved.Width, saved.Height));
    }

    [Fact]
    public async Task Objects_are_never_publicly_reachable_only_through_short_lived_signed_urls()
    {
        var storage = StorageFor(OrgA);
        await using var upload = PhotoWithGpsExif();
        var stored = await storage.PutImageAsync(upload, default);
        using var http = new HttpClient();

        var signed = storage.CreateDownloadUrl(stored.Key);
        var unsigned = signed[..signed.IndexOf('?')];
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(unsigned)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(signed)).StatusCode);

        var shortLived = storage.CreateDownloadUrl(stored.Key, TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(shortLived)).StatusCode);
    }

    [Fact]
    public async Task Requested_lifetime_is_capped_at_the_configured_maximum()
    {
        var storage = StorageFor(OrgA);
        await using var upload = PhotoWithGpsExif();
        var stored = await storage.PutImageAsync(upload, default);

        var url = new Uri(storage.CreateDownloadUrl(stored.Key, TimeSpan.FromDays(7)));
        var expires = int.Parse(System.Web.HttpUtility.ParseQueryString(url.Query)["X-Amz-Expires"]!);
        // The SDK derives seconds from "now" at signing time, so allow one second of rounding.
        Assert.True(expires <= minio.Options.MaxUrlLifetime.TotalSeconds + 1, $"expires={expires}");
    }

    [Fact]
    public async Task One_organization_cannot_sign_or_read_another_organizations_object()
    {
        await using var upload = PhotoWithGpsExif();
        var stored = await StorageFor(OrgA).PutImageAsync(upload, default);

        Assert.Throws<UnauthorizedAccessException>(() => StorageFor(OrgB).CreateDownloadUrl(stored.Key));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => StorageFor(OrgB).OpenReadAsync(stored.Key, default));
        Assert.StartsWith(OrgA.ToString("N") + "/", stored.Key);
    }

    [Fact]
    public async Task Non_image_files_are_stored_as_is()
    {
        var storage = StorageFor(OrgA);
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var stored = await storage.PutAsync(new MemoryStream(bytes), "audio/ogg", ".ogg", default);

        await using var read = await storage.OpenReadAsync(stored.Key, default);
        using var ms = new MemoryStream();
        await read.CopyToAsync(ms);
        Assert.Equal(bytes, ms.ToArray());
        Assert.EndsWith(".ogg", stored.Key);
    }

    [Fact]
    public async Task Corrupt_image_is_rejected_and_nothing_is_stored()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => StorageFor(OrgA).PutImageAsync(new MemoryStream([0, 1, 2, 3]), default));
    }
}
