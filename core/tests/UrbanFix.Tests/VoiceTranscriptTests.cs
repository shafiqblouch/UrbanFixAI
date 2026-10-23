using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Infrastructure.Jobs;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class VoiceTranscriptTests(TestApp app)
{
    private static byte[] VoiceNote(byte salt) => [0, 0, 0, 0x20, .. "ftypM4A "u8.ToArray(), .. Enumerable.Repeat(salt, 300)];

    private static async Task<(Guid Complaint, MediaDto Media)> ComplaintWithVoice(Society s, byte salt = 1)
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Voice complaint", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        var form = new MultipartFormDataContent { { new ByteArrayContent(VoiceNote(salt)), "file", "note.m4a" } };
        var res = await s.Resident.Client.PostAsync($"/complaints/{created.Id}/media", form);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (created.Id, (await res.Content.ReadFromJsonAsync<MediaDto>())!);
    }

    private static async Task<MediaDto> Voice(Society s, Guid complaint) =>
        (await s.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{complaint}"))!.Media.Single(m => m.Kind == "audio");

    private async Task Run(Society s, Guid mediaId, int retries = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<VoiceTranscriptionJob>().RunCoreAsync(s.Org, mediaId, retries, default);
    }

    [Fact]
    public async Task A_voice_note_gets_a_transcript_with_confidence_shown_beside_the_audio()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var (complaint, media) = await ComplaintWithVoice(s);
        Assert.Null(media.Transcript);

        await Run(s, media.Id);
        var after = await Voice(s, complaint);
        Assert.Equal(("no water since morning", 0.8f, "en", "done"), (after.Transcript, after.TranscriptConfidence, after.TranscriptLanguage, after.TranscriptStatus));
        Assert.Contains(ai.TranscribeCalls, c => c.Mime == "audio/mp4" && c.Bytes == VoiceNote(1).Length);

        // the reporter sees it too, and a second run does not call the service again
        var calls = ai.TranscribeCalls.Count;
        await Run(s, media.Id);
        Assert.Equal(calls, ai.TranscribeCalls.Count);
        var mine = (await s.Resident.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{complaint}"))!.Media.Single(m => m.Kind == "audio");
        Assert.Equal("done", mine.TranscriptStatus);

        var rows = await s.Supervisor.Client.GetFromJsonAsync<List<SuggestionDto>>($"/complaints/{complaint}/ai-suggestions");
        Assert.Contains(rows!, r => r.Task == "transcription" && r.Status == "done" && r.Confidence == 0.8f);
    }

    [Fact]
    public async Task A_failed_transcription_keeps_the_recording_and_is_retried_then_marked_failed()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var (complaint, media) = await ComplaintWithVoice(s, 2);
        var attempts = app.Services.GetRequiredService<JobOptions>().Attempts;

        ai.Down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Run(s, media.Id));
            Assert.Null((await Voice(s, complaint)).TranscriptStatus); // still waiting, may be retried
            await Run(s, media.Id, attempts); // out of retries
        }
        finally { ai.Down = false; }

        var failed = await Voice(s, complaint);
        Assert.Equal(("failed", null), (failed.TranscriptStatus, failed.Transcript));
        Assert.False(string.IsNullOrEmpty(failed.Url)); // the audio itself is untouched

        await Run(s, media.Id); // a later manual run still works
        Assert.Equal("done", (await Voice(s, complaint)).TranscriptStatus);
    }

    [Fact]
    public async Task Doubtful_transcripts_are_stored_with_their_low_confidence_so_staff_can_judge()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var previous = ai.Transcript;
        ai.Transcript = previous with { Transcript = "pani nahi", Confidence = 0.3f, Language = "roman_ur", LowConfidence = true };
        try
        {
            var (complaint, media) = await ComplaintWithVoice(s, 3);
            await Run(s, media.Id);
            var m = await Voice(s, complaint);
            Assert.Equal((0.3f, "roman_ur"), (m.TranscriptConfidence, m.TranscriptLanguage));
        }
        finally { ai.Transcript = previous; }
    }
}
