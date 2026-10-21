using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using UrbanFix.Api.Endpoints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Storage;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.PricingEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class MediaTests(TestApp app)
{
    private static byte[] Noise(int w, int h, bool png = false, int seed = 1, Action<ExifProfile>? exif = null)
    {
        var rng = new Random(seed);
        using var img = new Image<Rgb24>(w, h);
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var p in rows.GetRowSpan(y)) p = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        });
        if (exif is not null) { var profile = new ExifProfile(); exif(profile); img.Metadata.ExifProfile = profile; }
        using var ms = new MemoryStream();
        if (png) img.Save(ms, new PngEncoder()); else img.Save(ms, new JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }

    private static byte[] Flat(int w, int h, byte value, bool gradient = false)
    {
        using var img = new Image<Rgb24>(w, h);
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) { var v = gradient ? (byte)(x * 255 / w) : value; row[x] = new Rgb24(v, v, v); }
            }
        });
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder());
        return ms.ToArray();
    }

    private static MultipartFormDataContent Form(byte[] bytes, string name = "photo.jpg")
        => new() { { new ByteArrayContent(bytes), "file", name } };

    private async Task<(Society S, CreatedDto C)> NewComplaint()
    {
        var s = await Society.CreateAsync(app);
        var res = await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Leak", null, null, null, null, null, null));
        return (s, (await res.Content.ReadFromJsonAsync<CreatedDto>())!);
    }

    private static async Task<MediaDto> UploadOk(ApiSession who, Guid complaintId, byte[] bytes)
    {
        var res = await who.Client.PostAsync($"/complaints/{complaintId}/media", Form(bytes));
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<MediaDto>())!;
    }

    [Fact]
    public async Task Photo_is_stored_with_a_thumbnail_a_hash_and_its_exif_location_but_without_exif()
    {
        var (s, c) = await NewComplaint();
        var bytes = Noise(800, 600, exif: e =>
        {
            e.SetValue(ExifTag.GPSLatitudeRef, "N"); e.SetValue(ExifTag.GPSLatitude, new Rational[] { new(24, 1), new(51, 1), new(3852, 100) });
            e.SetValue(ExifTag.GPSLongitudeRef, "E"); e.SetValue(ExifTag.GPSLongitude, new Rational[] { new(67, 1), new(0, 1), new(400, 100) });
        });

        var media = await UploadOk(s.Resident, c.Id, bytes);
        Assert.Equal(("image", "image/jpeg", 800, 600), (media.Kind, media.MimeType, media.Width, media.Height));
        Assert.Empty(media.QualityFlags);
        Assert.NotNull(media.ThumbUrl);

        using var http = new HttpClient();
        using var thumb = await Image.LoadAsync(await http.GetStreamAsync(media.ThumbUrl));
        Assert.True(thumb.Width <= 320 && thumb.Height <= 320);
        using var stored = await Image.LoadAsync(await http.GetStreamAsync(media.Url));
        Assert.True(stored.Metadata.ExifProfile is null || stored.Metadata.ExifProfile.Values.Count == 0);

        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT latitude, sha256, size_bytes FROM complaint_media WHERE id = @i", conn);
        cmd.Parameters.AddWithValue("i", media.Id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(24.8607, r.GetDouble(0), 3);
        Assert.Equal(64, r.GetString(1).Length);
        Assert.Equal(bytes.Length, r.GetInt64(2));
    }

    [Fact]
    public async Task Uploading_the_same_file_twice_returns_the_existing_record()
    {
        var (s, c) = await NewComplaint();
        var bytes = Noise(640, 480, seed: 7);
        var first = await UploadOk(s.Resident, c.Id, bytes);
        var second = await UploadOk(s.Resident, c.Id, bytes);
        Assert.Equal(first.Id, second.Id);
        Assert.Single((await s.Resident.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{c.Id}"))!.Media);
    }

    [Fact]
    public async Task Quality_problems_are_flagged_but_never_block_the_upload()
    {
        var (s, c) = await NewComplaint();
        Assert.Contains(ImageQuality.LowResolution, (await UploadOk(s.Resident, c.Id, Noise(120, 90, seed: 2))).QualityFlags);
        Assert.Contains(ImageQuality.TooDark, (await UploadOk(s.Resident, c.Id, Flat(800, 600, 8))).QualityFlags);
        Assert.Contains(ImageQuality.TooBright, (await UploadOk(s.Resident, c.Id, Flat(800, 600, 250))).QualityFlags);
        Assert.Contains(ImageQuality.Blurry, (await UploadOk(s.Resident, c.Id, Flat(800, 600, 0, gradient: true))).QualityFlags);
    }

    [Fact]
    public async Task Unsupported_corrupt_empty_and_oversized_files_are_refused_with_clear_codes()
    {
        var (s, c) = await NewComplaint();
        async Task<HttpStatusCode> Try(byte[] b) => (await s.Resident.Client.PostAsync($"/complaints/{c.Id}/media", Form(b))).StatusCode;

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Try("MZ this is an executable pretending to be a photo"u8.ToArray()));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Try("just some text"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, await Try([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Equal(HttpStatusCode.BadRequest, await Try([]));
        var huge = new byte[11 * 1024 * 1024]; huge[0] = 0xFF; huge[1] = 0xD8; huge[2] = 0xFF;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Try(huge));
    }

    [Fact]
    public async Task The_declared_name_and_type_are_ignored_the_real_content_decides()
    {
        var (s, c) = await NewComplaint();
        var res = await s.Resident.Client.PostAsync($"/complaints/{c.Id}/media", Form(Noise(640, 480, png: true, seed: 3), "totally-a-document.pdf"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Equal("image/png", (await res.Content.ReadFromJsonAsync<MediaDto>())!.MimeType);
    }

    [Fact]
    public async Task Audio_notes_are_accepted_and_stored_as_is()
    {
        var (s, c) = await NewComplaint();
        var ogg = "OggS"u8.ToArray().Concat(new byte[200]).ToArray();
        var media = await UploadOk(s.Resident, c.Id, ogg);
        Assert.Equal(("audio", "audio/ogg"), (media.Kind, media.MimeType));
        Assert.Null(media.ThumbUrl);
    }

    [Fact]
    public async Task Only_people_involved_in_the_complaint_can_attach_files()
    {
        var (s, c) = await NewComplaint();
        var stranger = await s.Neighbour.Client.PostAsync($"/complaints/{c.Id}/media", Form(Noise(640, 480, seed: 4)));
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await s.Office.Client.PostAsync($"/complaints/{c.Id}/media", Form(Noise(640, 480, seed: 5)))).StatusCode);
    }

    [Fact]
    public async Task A_five_megabyte_photo_uploads_in_chunks_and_resumes_after_a_dropped_connection()
    {
        var (s, c) = await NewComplaint();
        var photo = Noise(1500, 1200, png: true, seed: 11); // incompressible: about 5.4 MB
        Assert.InRange(photo.Length, 4_500_000, 7_000_000);
        const int chunk = 1_000_000;

        var start = await s.Resident.Client.PostAsJsonAsync("/uploads", new StartUpload("image/png", photo.Length));
        var upload = (await start.Content.ReadFromJsonAsync<UploadDto>())!;

        async Task<HttpResponseMessage> Put(long offset, int length) =>
            await s.Resident.Client.PutAsync($"/uploads/{upload.Id}?offset={offset}", new ByteArrayContent(photo, (int)offset, length));

        // Two chunks arrive, then the connection drops.
        Assert.Equal(HttpStatusCode.OK, (await Put(0, chunk)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(chunk, chunk)).StatusCode);

        // The client resumes by asking where the server got to.
        var progress = await s.Resident.Client.GetFromJsonAsync<UploadDto>($"/uploads/{upload.Id}");
        Assert.Equal(2 * chunk, progress!.ReceivedBytes);

        // A retry of an already-received chunk is harmless; skipping ahead is refused; finishing early is refused.
        var again = await (await Put(0, chunk)).Content.ReadFromJsonAsync<UploadDto>();
        Assert.Equal(2 * chunk, again!.ReceivedBytes);
        Assert.Equal(HttpStatusCode.Conflict, (await Put(4 * chunk, 1000)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Resident.Client.PostAsync($"/complaints/{c.Id}/media/from-upload/{upload.Id}", null)).StatusCode);

        for (long offset = 2 * chunk; offset < photo.Length; offset += chunk)
            Assert.Equal(HttpStatusCode.OK, (await Put(offset, (int)Math.Min(chunk, photo.Length - offset))).StatusCode);

        var done = await s.Resident.Client.PostAsync($"/complaints/{c.Id}/media/from-upload/{upload.Id}", null);
        Assert.Equal(HttpStatusCode.Created, done.StatusCode);
        var media = (await done.Content.ReadFromJsonAsync<MediaDto>())!;
        Assert.Equal((1500, 1200, photo.Length), (media.Width, media.Height, (int)media.SizeBytes));

        // The assembled file is byte-identical in content (hash matches the original).
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT sha256 FROM complaint_media WHERE id = @i", conn);
        cmd.Parameters.AddWithValue("i", media.Id);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(photo)).ToLowerInvariant(), (string)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Uploads_are_private_to_their_owner_and_validated_up_front()
    {
        var (s, _) = await NewComplaint();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await s.Resident.Client.PostAsJsonAsync("/uploads", new StartUpload("image/png", 50_000_000))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PostAsJsonAsync("/uploads", new StartUpload("image/png", 0))).StatusCode);

        var upload = (await (await s.Resident.Client.PostAsJsonAsync("/uploads", new StartUpload("image/png", 100))).Content.ReadFromJsonAsync<UploadDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await s.Neighbour.Client.PutAsync($"/uploads/{upload.Id}?offset=0", new ByteArrayContent([1, 2, 3]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Resident.Client.PutAsync($"/uploads/{upload.Id}?offset=0", new ByteArrayContent(new byte[200]))).StatusCode);
    }

    [Fact]
    public async Task Stored_media_are_not_publicly_reachable_and_other_organizations_cannot_sign_them()
    {
        var (s, c) = await NewComplaint();
        var media = await UploadOk(s.Resident, c.Id, Noise(640, 480, seed: 21));
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(media.Url[..media.Url.IndexOf('?')])).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(media.Url)).StatusCode);

        var other = await Society.CreateAsync(app);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Manager.Client.GetAsync($"/complaints/{c.Id}")).StatusCode);
    }

    [Fact]
    public void Image_quality_checker_passes_a_sharp_well_exposed_photo()
    {
        using var img = Image.Load<Rgb24>(Noise(800, 600, seed: 9));
        Assert.Empty(ImageQuality.Analyze(img));
    }

    [Fact]
    public void File_sniffer_recognises_the_supported_formats_and_nothing_else()
    {
        Assert.Equal("image/jpeg", FileSniffer.Identify([0xFF, 0xD8, 0xFF, 0xE0])!.Mime);
        Assert.Equal("image/png", FileSniffer.Identify([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])!.Mime);
        Assert.Equal("image/webp", FileSniffer.Identify("RIFF\0\0\0\0WEBPVP8 "u8)!.Mime);
        Assert.Equal("audio/wav", FileSniffer.Identify("RIFF\0\0\0\0WAVEfmt "u8)!.Mime);
        Assert.Equal("audio/ogg", FileSniffer.Identify("OggS\0\0"u8)!.Mime);
        Assert.Equal("audio/mpeg", FileSniffer.Identify("ID3\u0003\0"u8)!.Mime);
        Assert.Equal("audio/mp4", FileSniffer.Identify("\0\0\0\u0020ftypM4A "u8)!.Mime);
        Assert.Null(FileSniffer.Identify("%PDF-1.7 not allowed"u8));
        Assert.Null(FileSniffer.Identify([]));
    }
}

[Collection("db")]
public class PricingTests(TestApp app)
{
    private static async Task<PriceItemDto> Item(ApiSession s, string name, decimal rate, DateTimeOffset? from = null)
    {
        var res = await s.Client.PostAsJsonAsync("/price-items", new CreatePriceItem(name, null, "metre", rate, from));
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<PriceItemDto>())!;
    }

    [Fact]
    public async Task Changing_a_rate_keeps_history_and_the_old_rate_is_still_available_for_past_dates()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var jan = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var jul = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var pipe = await Item(m, "PVC pipe 4 inch", 450m, jan);
        Assert.Equal(450m, pipe.CurrentRate!.Rate);

        (await m.Client.PostAsJsonAsync($"/price-items/{pipe.Id}/rates", new AddRate(520.50m, jul))).EnsureSuccessStatusCode();

        var now = (await m.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items"))!.Single(i => i.Id == pipe.Id);
        Assert.Equal(520.50m, now.CurrentRate!.Rate);
        var march = (await m.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items?at=2026-03-15T00:00:00Z"))!.Single(i => i.Id == pipe.Id);
        Assert.Equal(450m, march.CurrentRate!.Rate); // an estimate made in March still sees March's price

        var history = await m.Client.GetFromJsonAsync<List<RateDto>>($"/price-items/{pipe.Id}/rates");
        Assert.Equal([520.50m, 450m], history!.Select(h => h.Rate).ToArray());
    }

    [Fact]
    public async Task Future_dated_rates_do_not_apply_yet_and_before_the_first_rate_there_is_none()
    {
        var (_, m) = await app.CreateOrganizationAsync();
        var bolt = await Item(m, "Bolt M10", 12m, DateTimeOffset.UtcNow.AddDays(-1));
        await m.Client.PostAsJsonAsync($"/price-items/{bolt.Id}/rates", new AddRate(15m, DateTimeOffset.UtcNow.AddDays(30)));

        var items = await m.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items");
        Assert.Equal(12m, items!.Single(i => i.Id == bolt.Id).CurrentRate!.Rate);
        var ancient = await m.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items?at=2000-01-01T00:00:00Z");
        Assert.Null(ancient!.Single(i => i.Id == bolt.Id).CurrentRate);
    }

    [Fact]
    public async Task Rate_history_cannot_be_edited_or_deleted_by_anyone()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        await Item(m, "Cement bag", 1300m);

        await using var owner = new NpgsqlConnection(app.OwnerConnectionString);
        await owner.OpenAsync();
        foreach (var sql in new[] { "UPDATE price_rates SET rate = 1", "DELETE FROM price_rates" })
        {
            await using var cmd = new NpgsqlCommand(sql, owner);
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        }
        await using var conn = await app.OpenAppConnectionAsync(org);
        await using var upd = new NpgsqlCommand("UPDATE price_rates SET rate = 1", conn);
        Assert.Equal("42501", (await Assert.ThrowsAsync<PostgresException>(() => upd.ExecuteNonQueryAsync())).SqlState);
    }

    [Fact]
    public async Task Prices_are_validated_unique_per_organization_and_role_restricted()
    {
        var (org, m) = await app.CreateOrganizationAsync();
        await Item(m, "Sand", 80m);
        Assert.Equal(HttpStatusCode.Conflict, (await m.Client.PostAsJsonAsync("/price-items", new CreatePriceItem("sand", null, "bag", 5m, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync("/price-items", new CreatePriceItem("Gravel", null, "bag", -1m, null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await m.Client.PostAsJsonAsync("/price-items", new CreatePriceItem("", null, "bag", 1m, null))).StatusCode);

        var tech = await app.AddMemberAsync(m, org, Role.Technician);
        var accounts = await app.AddMemberAsync(m, org, Role.Accounts);
        var resident = await app.AddMemberAsync(m, org, Role.Resident);
        Assert.Equal(HttpStatusCode.OK, (await tech.Client.GetAsync("/price-items")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.Client.PostAsJsonAsync("/price-items", new CreatePriceItem("Nail", null, "kg", 1m, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await accounts.Client.PostAsJsonAsync("/price-items", new CreatePriceItem("Nail", null, "kg", 1m, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await resident.Client.GetAsync("/price-items")).StatusCode);
    }

    [Fact]
    public async Task Price_lists_are_per_organization_and_deactivated_items_disappear_from_the_default_list()
    {
        var (_, a) = await app.CreateOrganizationAsync();
        var (_, b) = await app.CreateOrganizationAsync();
        var item = await Item(a, "Wire 2.5mm", 95m);
        Assert.DoesNotContain(await b.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items") ?? [], i => i.Id == item.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client.PostAsJsonAsync($"/price-items/{item.Id}/rates", new AddRate(1m, null))).StatusCode);

        await a.Client.PutAsJsonAsync($"/price-items/{item.Id}", new UpdatePriceItem(null, null, null, false));
        Assert.DoesNotContain(await a.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items") ?? [], i => i.Id == item.Id);
        Assert.Contains(await a.Client.GetFromJsonAsync<List<PriceItemDto>>("/price-items?includeInactive=true") ?? [], i => i.Id == item.Id);
        // Its history survives deactivation.
        Assert.Single(await a.Client.GetFromJsonAsync<List<RateDto>>($"/price-items/{item.Id}/rates") ?? []);
    }
}
