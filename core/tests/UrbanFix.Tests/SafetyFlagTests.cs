using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Notifications;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Tests;

public class SafetyTermCorpusTests
{
    // Every one of these describes a danger to people, so every one must be flagged (AI-10).
    [Theory]
    [InlineData("A live wire is hanging down near the school gate")]
    [InlineData("There is an exposed wire on the pole outside house 14")]
    [InlineData("Child got an electric shock from the street light")]
    [InlineData("The transformer is sparking and making noise")]
    [InlineData("Smoke is coming out of the electric panel")]
    [InlineData("I can smell gas in the lane, possible gas leak")]
    [InlineData("Sewage overflow is flooding the road and entering houses")]
    [InlineData("The manhole cover is missing, an open manhole on the main road")]
    [InlineData("Part of the boundary wall collapsed onto the footpath")]
    [InlineData("A tree has fallen on the cars and someone is injured")]
    [InlineData("Water flooding into the basement, an old man is unconscious")]
    [InlineData("بجلی کی ننگی تار لٹک رہی ہے")]
    [InlineData("کھمبے سے چنگاری نکل رہی ہے اور آگ لگنے کا خطرہ ہے")]
    [InlineData("گلی میں کھلا مین ہول ہے")]
    [InlineData("دیوار گر گئی ہے اور ایک بچہ زخمی ہوا")]
    [InlineData("سیوریج کا پانی گھروں میں آ رہا ہے")]
    [InlineData("گیس کی بو آ رہی ہے")]
    public void Danger_in_the_text_is_always_flagged(string text) => Assert.True(SafetyTerms.IsSafety(text), text);

    [Theory]
    [InlineData("The tap in the park is dripping")]
    [InlineData("Street light is not working since Monday")]
    [InlineData("Garbage has not been collected this week")]
    [InlineData("Water pressure is low in the morning")]
    [InlineData("پارک کی بینچ ٹوٹی ہوئی ہے")]
    [InlineData("کچرا نہیں اٹھایا گیا")]
    public void Ordinary_complaints_are_not_flagged(string text) => Assert.False(SafetyTerms.IsSafety(text), text);
}

[Collection("db")]
public class PhotoAndVoiceSafetyTests(TestApp app)
{
    private static byte[] Photo(int seed)
    {
        var rng = new Random(seed);
        using var img = new Image<Rgb24>(320, 240);
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (ref var p in rows.GetRowSpan(y)) p = new Rgb24((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
        });
        using var ms = new MemoryStream();
        img.Save(ms, new JpegEncoder { Quality = 90 });
        return ms.ToArray();
    }

    private static async Task<(Guid Complaint, MediaDto Media)> ComplaintWith(Society s, byte[] file, string name, string text = "Something near my house")
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        var res = await s.Resident.Client.PostAsync($"/complaints/{created.Id}/media", new MultipartFormDataContent { { new ByteArrayContent(file), "file", name } });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (created.Id, (await res.Content.ReadFromJsonAsync<MediaDto>())!);
    }

    private static async Task<ComplaintDetailDto> Detail(Society s, Guid id) => (await s.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!;

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    private async Task Photos(Society s, Guid mediaId, int retries = 0) =>
        await InScope(async sp => { await sp.GetRequiredService<PhotoInterpretationJob>().RunCoreAsync(s.Org, mediaId, retries, default); return 0; });

    private async Task Voice(Society s, Guid mediaId) =>
        await InScope(async sp => { await sp.GetRequiredService<VoiceTranscriptionJob>().RunCoreAsync(s.Org, mediaId, 0, default); return 0; });

    private async Task Drain() => await InScope(async sp =>
    {
        var processor = sp.GetRequiredService<OutboxProcessor>();
        for (var i = 0; i < 50 && await processor.ProcessBatchAsync(default) > 0; i++) { }
        return 0;
    });

    [Fact]
    public async Task A_photo_gets_labels_and_a_description_beside_it_and_a_hazard_makes_the_complaint_a_safety_matter()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var previous = ai.Photo;
        try
        {
            ai.Photo = new AiPhoto(["open_manhole"], "A possible open manhole appears to be visible in the road.", false, 0.85f, true, false, "fake-vision", "v1");
            var (id, media) = await ComplaintWith(s, Photo(10), "manhole.jpg");
            Assert.Equal(ComplaintStatus.New, (await Detail(s, id)).Complaint.Status);
            Assert.False((await Detail(s, id)).Complaint.SafetyFlag);

            await Photos(s, media.Id);
            var d = await Detail(s, id);
            Assert.Equal((true, "Safety"), (d.Complaint.SafetyFlag, d.Complaint.Priority));
            var m = d.Media.Single();
            Assert.Equal(["open_manhole"], m.AiLabels);
            Assert.False(m.AiNeedsMoreInfo);
            Assert.Contains("open manhole", m.AiDescription);

            await Drain();
            var staff = await s.Supervisor.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications");
            Assert.Contains(staff!.Items, n => n.Template == NotificationTemplates.SafetyAlert && n.Body.Contains(d.Complaint.Reference));
            var resident = await s.Resident.Client.GetFromJsonAsync<NotificationEndpoints.InboxDto>("/notifications");
            Assert.Contains(resident!.Items, n => n.Template == NotificationTemplates.SafetyPrompt);
        }
        finally { ai.Photo = previous; }
    }

    [Fact]
    public async Task An_ordinary_photo_changes_nothing_but_the_labels()
    {
        var s = await Society.CreateAsync(app);
        var (id, media) = await ComplaintWith(s, Photo(11), "pothole.jpg");
        await Photos(s, media.Id);
        var d = await Detail(s, id);
        Assert.False(d.Complaint.SafetyFlag);
        Assert.NotEqual("Safety", d.Complaint.Priority);
        Assert.Equal(["pothole"], d.Media.Single().AiLabels);
    }

    [Fact]
    public async Task A_failed_photo_look_never_blocks_the_complaint_and_is_marked_after_the_last_retry()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var (id, media) = await ComplaintWith(s, Photo(12), "x.jpg");
        var attempts = app.Services.GetRequiredService<UrbanFix.Infrastructure.Jobs.JobOptions>().Attempts;

        ai.Down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Photos(s, media.Id));
            await Photos(s, media.Id, attempts);
        }
        finally { ai.Down = false; }

        var m = (await Detail(s, id)).Media.Single();
        Assert.Empty(m.AiLabels!);
        Assert.False(string.IsNullOrEmpty(m.Url));
        Assert.Equal(ComplaintStatus.New, (await Detail(s, id)).Complaint.Status);
    }

    [Fact]
    public async Task Danger_words_in_a_voice_note_flag_the_complaint_even_when_the_typed_text_was_calm()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var previous = ai.Transcript;
        ai.Transcript = previous with { Transcript = "there is a live wire hanging, please come quickly", Confidence = 0.9f };
        try
        {
            byte[] note = [0, 0, 0, 0x20, .. "ftypM4A "u8.ToArray(), .. Enumerable.Repeat((byte)7, 300)];
            var (id, media) = await ComplaintWith(s, note, "note.m4a", "Light problem");
            Assert.False((await Detail(s, id)).Complaint.SafetyFlag);
            await Voice(s, media.Id);
            var d = (await Detail(s, id)).Complaint;
            Assert.Equal((true, "Safety"), (d.SafetyFlag, d.Priority));
        }
        finally { ai.Transcript = previous; }
    }
}
