using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.Processing;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Storage;

public sealed class StorageOptions
{
    public string ServiceUrl { get; set; } = "http://localhost:9000";
    /// <summary>Host used inside signed URLs when it differs from <see cref="ServiceUrl"/> (e.g. container vs browser).</summary>
    public string? PublicUrl { get; set; }
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "urbanfix-media";
    public TimeSpan MaxUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record GeoPoint(double Latitude, double Longitude);

public sealed record StoredObject(string Key, GeoPoint? Location, DateTimeOffset? CapturedAt);

public interface IObjectStorage
{
    /// <summary>Stores an image after reading location/time from EXIF; the stored copy has no EXIF or other metadata.</summary>
    Task<StoredObject> PutImageAsync(Stream image, CancellationToken ct);

    /// <summary>Stores a non-image file (audio, PDF) as-is.</summary>
    Task<StoredObject> PutAsync(Stream content, string contentType, string extension, CancellationToken ct);

    /// <summary>Short-lived download link. Refuses keys belonging to another organization.</summary>
    string CreateDownloadUrl(string key, TimeSpan? lifetime = null);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Stores a temporary object (e.g. an upload chunk) under the organization's tmp prefix.</summary>
    Task<string> PutTempAsync(string name, Stream content, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);
}

public sealed class ObjectStorage(IAmazonS3 s3, StorageOptions options, ITenantContext tenant) : IObjectStorage
{
    public async Task<StoredObject> PutImageAsync(Stream image, CancellationToken ct)
    {
        using var img = await Image.LoadAsync(image, ct);
        var format = img.Metadata.DecodedImageFormat ?? throw new InvalidOperationException("Unsupported image format");

        var (location, capturedAt) = ReadExif(img.Metadata.ExifProfile);

        // Bake the orientation in first: dropping the EXIF tag would otherwise rotate the picture.
        img.Mutate(x => x.AutoOrient());
        img.Metadata.ExifProfile = null;
        img.Metadata.IptcProfile = null;
        img.Metadata.XmpProfile = null;
        img.Metadata.IccProfile = null;

        await using var buffer = new MemoryStream();
        await img.SaveAsync(buffer, format, ct);
        buffer.Position = 0;

        var key = NewKey(format.FileExtensions.First());
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = buffer,
            ContentType = format.DefaultMimeType,
        }, ct);
        return new StoredObject(key, location, capturedAt);
    }

    public async Task<StoredObject> PutAsync(Stream content, string contentType, string extension, CancellationToken ct)
    {
        var key = NewKey(extension.TrimStart('.'));
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = options.Bucket,
            Key = key,
            InputStream = buffer,
            ContentType = contentType,
        }, ct);
        return new StoredObject(key, null, null);
    }

    public string CreateDownloadUrl(string key, TimeSpan? lifetime = null)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        if (!key.StartsWith(org.ToString("N") + "/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Object belongs to another organization");

        var expires = lifetime is { } l && l < options.MaxUrlLifetime ? l : options.MaxUrlLifetime;
        var url = s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expires),
            Protocol = options.ServiceUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? Protocol.HTTPS : Protocol.HTTP,
        });
        return options.PublicUrl is { } pub ? url.Replace(options.ServiceUrl.TrimEnd('/'), pub.TrimEnd('/')) : url;
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        if (!key.StartsWith(org.ToString("N") + "/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Object belongs to another organization");
        var response = await s3.GetObjectAsync(options.Bucket, key, ct);
        return response.ResponseStream;
    }

    public async Task<string> PutTempAsync(string name, Stream content, CancellationToken ct)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        var key = $"{org:N}/tmp/{name}";
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = options.Bucket, Key = key, InputStream = buffer, ContentType = "application/octet-stream" }, ct);
        return key;
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        if (!key.StartsWith(org.ToString("N") + "/", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Object belongs to another organization");
        await s3.DeleteObjectAsync(options.Bucket, key, ct);
    }

    private string NewKey(string extension)
    {
        var org = tenant.OrganizationId ?? throw new InvalidOperationException("No organization context");
        return $"{org:N}/{DateTime.UtcNow:yyyy/MM}/{Guid.NewGuid():N}.{extension}";
    }

    private static (GeoPoint?, DateTimeOffset?) ReadExif(ExifProfile? exif)
    {
        if (exif is null) return (null, null);

        GeoPoint? point = null;
        if (exif.TryGetValue(ExifTag.GPSLatitude, out var lat) && exif.TryGetValue(ExifTag.GPSLatitudeRef, out var latRef)
            && exif.TryGetValue(ExifTag.GPSLongitude, out var lon) && exif.TryGetValue(ExifTag.GPSLongitudeRef, out var lonRef))
        {
            var latitude = ToDegrees(lat.Value) * (latRef.Value == "S" ? -1 : 1);
            var longitude = ToDegrees(lon.Value) * (lonRef.Value == "W" ? -1 : 1);
            if (latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180) point = new GeoPoint(latitude, longitude);
        }

        DateTimeOffset? taken = null;
        if (exif.TryGetValue(ExifTag.DateTimeOriginal, out var dto)
            && DateTime.TryParseExact(dto.Value, "yyyy:MM:dd HH:mm:ss", null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
            taken = new DateTimeOffset(parsed, TimeSpan.Zero);

        return (point, taken);
    }

    private static double ToDegrees(Rational[] dms) => dms[0].ToDouble() + dms[1].ToDouble() / 60 + dms[2].ToDouble() / 3600;
}
