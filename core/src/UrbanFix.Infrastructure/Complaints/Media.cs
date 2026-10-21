using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Storage;

namespace UrbanFix.Infrastructure.Complaints;

public sealed record MediaKind(string Kind, string Mime);

public static class FileSniffer
{
    public const long MaxBytes = 10 * 1024 * 1024;

    /// <summary>Identifies the real file type from its first bytes; the client-declared type is never trusted.</summary>
    public static MediaKind? Identify(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return new("image", "image/jpeg");
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return new("image", "image/png");
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8)) return new("image", "image/webp");
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WAVE"u8)) return new("audio", "audio/wav");
        if (b.Length >= 4 && b[..4].SequenceEqual("OggS"u8)) return new("audio", "audio/ogg");
        if (b.Length >= 3 && b[..3].SequenceEqual("ID3"u8)) return new("audio", "audio/mpeg");
        if (b.Length >= 2 && b[0] == 0xFF && (b[1] & 0xE0) == 0xE0) return new("audio", "audio/mpeg");
        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8)) return new("audio", "audio/mp4");
        return null;
    }
}

/// <summary>Advisory image-quality checks (CP-04). Flags are shown to the resident; they never reject the photo.</summary>
public static class ImageQuality
{
    public const string LowResolution = "low_resolution";
    public const string TooDark = "too_dark";
    public const string TooBright = "too_bright";
    public const string Blurry = "blurry";

    public static string[] Analyze(Image<Rgb24> image)
    {
        var flags = new List<string>();
        if ((long)image.Width * image.Height < 300_000) flags.Add(LowResolution);

        using var small = image.Clone(x => x.Resize(new ResizeOptions { Size = new Size(256, 256), Mode = ResizeMode.Max }).Grayscale());
        var w = small.Width; var h = small.Height;
        var luma = new double[w * h];
        small.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < w; x++) luma[y * w + x] = row[x].R;
            }
        });

        var mean = luma.Average();
        if (mean < 40) flags.Add(TooDark);
        else if (mean > 235) flags.Add(TooBright);

        // Variance of the Laplacian: sharp images have strong edges, blurred ones do not.
        double sum = 0, sumSq = 0; var n = 0;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var lap = 4 * luma[y * w + x] - luma[y * w + x - 1] - luma[y * w + x + 1] - luma[(y - 1) * w + x] - luma[(y + 1) * w + x];
                sum += lap; sumSq += lap * lap; n++;
            }
        var variance = n == 0 ? 0 : sumSq / n - Math.Pow(sum / n, 2);
        if (variance < 40 && !flags.Contains(TooDark) && !flags.Contains(TooBright)) flags.Add(Blurry);
        return [.. flags];
    }
}

public sealed record ProcessedMedia(ComplaintMedia Media, bool Duplicate);

public interface IMediaProcessor
{
    /// <summary>Validates, hashes, quality-checks, strips EXIF, stores the file and a thumbnail. Returns the existing row for a duplicate.</summary>
    Task<(ComplaintMedia? Media, string? Error)> ProcessAsync(byte[] bytes, Guid organizationId, Guid complaintId, Guid? uploadedBy, Func<string, Task<ComplaintMedia?>> findBySha, CancellationToken ct);
}

public sealed class MediaProcessor(IObjectStorage storage) : IMediaProcessor
{
    public async Task<(ComplaintMedia? Media, string? Error)> ProcessAsync(
        byte[] bytes, Guid organizationId, Guid complaintId, Guid? uploadedBy, Func<string, Task<ComplaintMedia?>> findBySha, CancellationToken ct)
    {
        if (bytes.Length == 0) return (null, "empty_file");
        if (bytes.Length > FileSniffer.MaxBytes) return (null, "file_too_large");
        var kind = FileSniffer.Identify(bytes);
        if (kind is null) return (null, "unsupported_file_type");

        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (await findBySha(sha) is { } existing) return (existing, "duplicate");

        var media = new ComplaintMedia
        {
            OrganizationId = organizationId, ComplaintId = complaintId, Kind = kind.Kind, MimeType = kind.Mime,
            SizeBytes = bytes.Length, Sha256 = sha, UploadedBy = uploadedBy,
        };

        if (kind.Kind == "audio")
        {
            await using var audio = new MemoryStream(bytes);
            media.StorageKey = (await storage.PutAsync(audio, kind.Mime, kind.Mime.Split('/')[1], ct)).Key;
            return (media, null);
        }

        Image<Rgb24> image;
        try { image = Image.Load<Rgb24>(bytes); }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            return (null, "corrupt_image");
        }

        using (image)
        {
            media.Width = image.Width;
            media.Height = image.Height;
            media.QualityFlags = ImageQuality.Analyze(image);

            await using var original = new MemoryStream(bytes);
            var stored = await storage.PutImageAsync(original, ct);
            media.StorageKey = stored.Key;
            media.Latitude = stored.Location?.Latitude;
            media.Longitude = stored.Location?.Longitude;

            using var thumb = image.Clone(x => x.AutoOrient().Resize(new ResizeOptions { Size = new Size(320, 320), Mode = ResizeMode.Max }));
            await using var thumbStream = new MemoryStream();
            await thumb.SaveAsync(thumbStream, new JpegEncoder { Quality = 75 }, ct);
            thumbStream.Position = 0;
            media.ThumbKey = (await storage.PutAsync(thumbStream, "image/jpeg", "jpg", ct)).Key;
        }
        return (media, null);
    }
}
