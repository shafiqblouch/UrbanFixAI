using System.Net;
using Microsoft.Extensions.DependencyInjection;
using UrbanFix.Api.Endpoints;
using UrbanFix.Complaints;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.OrgSetup;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;
using static UrbanFix.Api.Endpoints.DashboardEndpoints;
using static UrbanFix.Api.Endpoints.RoutingEndpoints;

namespace UrbanFix.Tests;

public class RomanUrduSafetyTests
{
    [Theory]
    [InlineData("Gali mein nangi taar latak rahi hai")]
    [InlineData("khamba gir gaya hai aur karant hai")]
    [InlineData("Diwar gir gayi, ek bacha zakhmi hai")]
    [InlineData("khula manhole hai road par")]
    [InlineData("Gas ki boo aa rahi hai")]
    public void Roman_urdu_danger_is_flagged(string text) => Assert.True(SafetyTerms.IsSafety(text), text);

    [Theory]
    [InlineData("Pani nahi aa raha subah se")]
    [InlineData("kachra nahi uthaya gaya")]
    public void Ordinary_roman_urdu_is_not(string text) => Assert.False(SafetyTerms.IsSafety(text), text);
}

[Collection("db")]
public class FeedbackAndVoiceInputTests(TestApp app)
{
    private static async Task<Guid> Resolved(Society s)
    {
        var created = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("No water", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!;
        await s.Office.Client.PostAsJsonAsync($"/complaints/{created.Id}/transition", new Transition(ComplaintStatus.UnderReview, null, null));
        var job = (await (await s.Supervisor.Client.PostAsJsonAsync($"/complaints/{created.Id}/jobs", new CreateJob("Fix", null))).Content.ReadFromJsonAsync<JobDto>())!;
        await s.FinishWorkAsync(job);
        return created.Id;
    }

    private static Task<HttpResponseMessage> Rate(ApiSession who, Guid id, int rating, bool fixedIt = true, string? comment = null) =>
        who.Client.PostAsJsonAsync($"/complaints/{id}/feedback", new SaveFeedback(rating, fixedIt, comment));

    // JB-08 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_reporter_rates_finished_work_once_and_can_change_their_answer()
    {
        var s = await Society.CreateAsync(app);
        var id = await Resolved(s);

        var first = await Rate(s.Resident, id, 4, true, "  Good job  ");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(("Good job", 4), ((await first.Content.ReadFromJsonAsync<FeedbackDto>())!.Comment, 4));

        await Rate(s.Resident, id, 2, false, "Leaking again");
        var detail = (await s.Supervisor.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{id}"))!;
        Assert.Equal((2, false, "Leaking again"), (detail.Feedback!.Rating, detail.Feedback.ConfirmedFixed, detail.Feedback.Comment));

        // it can also be given after closing
        Assert.Equal(HttpStatusCode.OK, (await s.Resident.Client.PostAsJsonAsync($"/complaints/{id}/transition", new Transition(ComplaintStatus.Closed, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Rate(s.Resident, id, 5)).StatusCode);
    }

    [Fact]
    public async Task Feedback_is_refused_before_the_work_is_finished_from_other_people_and_when_out_of_range()
    {
        var s = await Society.CreateAsync(app);
        var open = (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint("Leak", null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!.Id;
        Assert.Equal(HttpStatusCode.Conflict, (await Rate(s.Resident, open, 5)).StatusCode);

        var done = await Resolved(s);
        Assert.Equal(HttpStatusCode.NotFound, (await Rate(s.Neighbour, done, 5)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Rate(s.Supervisor, done, 5)).StatusCode); // staff do not speak for the resident
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(s.Resident, done, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(s.Resident, done, 6)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Rate(s.Resident, done, 3, true, new string('x', 1001))).StatusCode);
        Assert.Null((await s.Resident.Client.GetFromJsonAsync<ComplaintDetailDto>($"/complaints/{done}"))!.Feedback);
    }

    [Fact]
    public async Task The_dashboard_shows_the_average_rating_and_how_many_confirmed_the_fix()
    {
        var s = await Society.CreateAsync(app);
        Assert.Null((await s.Manager.Client.GetFromJsonAsync<Summary>("/dashboard/summary"))!.AverageRating);

        var one = await Resolved(s);
        var two = await Resolved(s);
        await Rate(s.Resident, one, 5, true);
        await Rate(s.Resident, two, 2, false);

        var summary = (await s.Manager.Client.GetFromJsonAsync<Summary>("/dashboard/summary"))!;
        Assert.Equal((3.5, 2, 1), (summary.AverageRating, summary.RatingCount, summary.ConfirmedFixedCount));
    }

    // TA-08 -----------------------------------------------------------------------------------------------------------

    private static MultipartFormDataContent Voice(byte salt = 1) =>
        new() { { new ByteArrayContent([0, 0, 0, 0x20, .. "ftypM4A "u8.ToArray(), .. Enumerable.Repeat(salt, 300)]), "file", "note.m4a" } };

    [Fact]
    public async Task A_technician_dictates_a_note_and_gets_text_back_to_correct_before_saving()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var res = await s.Technician.Client.PostAsync("/voice/transcribe?language=ur", Voice());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var t = (await res.Content.ReadFromJsonAsync<TranscriptDto>())!;
        Assert.Equal(("no water since morning", false), (t.Transcript, t.LowConfidence));
        Assert.Contains(ai.TranscribeCalls, c => c.Hint == "ur" && c.Mime == "audio/mp4");
    }

    [Fact]
    public async Task Dictation_checks_who_asks_what_is_sent_and_how_big_and_reports_trouble_plainly()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();

        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.PostAsync("/voice/transcribe", Voice())).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await s.Technician.Client.PostAsync("/voice/transcribe", new MultipartFormDataContent { { new ByteArrayContent(new byte[400]), "file", "x.bin" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Technician.Client.PostAsync("/voice/transcribe", new MultipartFormDataContent { { new ByteArrayContent([]), "file", "x.m4a" } })).StatusCode);
        Assert.Equal((HttpStatusCode)413, (await s.Technician.Client.PostAsync("/voice/transcribe", new MultipartFormDataContent { { new ByteArrayContent(new byte[5 * 1024 * 1024 + 1]), "file", "x.m4a" } })).StatusCode);

        ai.Down = true;
        try { Assert.Equal(HttpStatusCode.BadGateway, (await s.Technician.Client.PostAsync("/voice/transcribe", Voice(2))).StatusCode); }
        finally { ai.Down = false; }

        (await s.Manager.Client.PutAsJsonAsync("/organization/policies", OrgPolicy.Default with { AiMonthlyTokenCap = 0 })).EnsureSuccessStatusCode();
        var off = await s.Technician.Client.PostAsync("/voice/transcribe", Voice(3));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, off.StatusCode);
        Assert.Contains("ai_not_available", await off.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dictated_calls_are_counted_against_the_organization()
    {
        var s = await Society.CreateAsync(app);
        await s.Technician.Client.PostAsync("/voice/transcribe", Voice(4));
        var report = (await s.Manager.Client.GetFromJsonAsync<UsageReport>("/reports/ai-usage"))!;
        Assert.Contains(report.ByTask, t => t.Task == "transcription" && t.Calls == 1);
    }
}
