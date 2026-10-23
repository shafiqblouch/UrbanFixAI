using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Infrastructure.Complaints;
using UrbanFix.Tests.Support;
using static UrbanFix.Api.Endpoints.ComplaintEndpoints;

namespace UrbanFix.Tests;

[Collection("db")]
public class EmbeddingTests(TestApp app)
{
    private async Task Sql(string sql, params (string, object)[] args)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> Complain(Society s, string text) =>
        (await (await s.Resident.Client.PostAsJsonAsync("/complaints", new CreateComplaint(text, null, s.CategoryId, s.HouseId, null, null, null))).Content.ReadFromJsonAsync<CreatedDto>())!.Id;

    private async Task Embed(Society s, Guid complaint, Guid? media = null, int retries = 0)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EmbeddingJob>().RunCoreAsync(s.Org, complaint, media, retries, default);
    }

    private static Task<List<SimilarDto>> Similar(Society s, Guid id, string query = "") =>
        s.Supervisor.Client.GetFromJsonAsync<List<SimilarDto>>($"/complaints/{id}/similar{query}")!;

    [Fact]
    public async Task Complaints_about_the_same_problem_are_found_and_unrelated_ones_are_not()
    {
        var s = await Society.CreateAsync(app);
        var water1 = await Complain(s, "No water supply in block A since morning");
        var water2 = await Complain(s, "Water supply stopped in block A this morning");
        var lights = await Complain(s, "Street lamp broken near the mosque gate");
        foreach (var id in new[] { water1, water2, lights }) await Embed(s, id);

        var found = await Similar(s, water1, "?minSimilarity=0.5");
        Assert.Equal(water2, found.First().ComplaintId);
        Assert.DoesNotContain(found, f => f.ComplaintId == water1); // never itself
        Assert.True(found.First().Similarity > 0.7); // six of eight words shared
        Assert.DoesNotContain(found, f => f.ComplaintId == lights && f.Similarity > found.First().Similarity);

        Assert.DoesNotContain(await Similar(s, water1, "?minSimilarity=0.99"), f => f.ComplaintId == lights);
    }

    [Fact]
    public async Task Rejected_and_merged_complaints_and_old_ones_are_left_out()
    {
        var s = await Society.CreateAsync(app);
        var a = await Complain(s, "Garbage pile outside house 5");
        var rejected = await Complain(s, "Garbage pile outside house 5");
        var old = await Complain(s, "Garbage pile outside house 5");
        foreach (var id in new[] { a, rejected, old }) await Embed(s, id);
        await Sql("UPDATE complaints SET status = 'Rejected' WHERE id = @id", ("id", rejected));
        await Sql("UPDATE complaints SET created_at = now() - interval '90 days' WHERE id = @id", ("id", old));

        Assert.Empty(await Similar(s, a));
        Assert.Equal([old], (await Similar(s, a, "?days=365")).Select(f => f.ComplaintId));
    }

    [Fact]
    public async Task Vectors_from_different_models_are_never_compared()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var one = await Complain(s, "Pothole on the main road");
        var two = await Complain(s, "Pothole on the main road");
        await Embed(s, one);
        ai.EmbeddingModel = "other-model";
        ai.Embedder = _ => new float[64]; // different size too: comparing these would be an error
        try { await Embed(s, two); }
        finally { ai.EmbeddingModel = "fake-embed"; ai.Embedder = FakeAiClient.Hash; }
        Assert.Empty(await Similar(s, one));
    }

    [Fact]
    public async Task Another_organizations_complaints_never_appear()
    {
        var a = await Society.CreateAsync(app);
        var b = await Society.CreateAsync(app);
        var mine = await Complain(a, "Broken pipe leaking at the corner");
        var theirs = await Complain(b, "Broken pipe leaking at the corner");
        await Embed(a, mine);
        await Embed(b, theirs);
        Assert.Empty(await Similar(a, mine));
    }

    [Fact]
    public async Task A_photo_description_is_embedded_as_its_own_row_and_matches_other_photos()
    {
        var s = await Society.CreateAsync(app);
        var one = await Complain(s, "Something on the road");
        var two = await Complain(s, "Unrelated words entirely here");
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        foreach (var (complaint, media) in new[] { (one, m1), (two, m2) })
            await Sql("""
                INSERT INTO complaint_media (id, organization_id, complaint_id, storage_key, kind, mime_type, size_bytes, sha256, ai_status, ai_description)
                VALUES (@m, @o, @c, 'k', 'image', 'image/jpeg', 1, @sha, 'done', 'A possible pothole is visible in the road surface')
                """, ("m", media), ("o", s.Org), ("c", complaint), ("sha", media.ToString("N")));

        await Embed(s, one, m1);
        await Embed(s, two, m2);
        var found = await Similar(s, one, "?minSimilarity=0.99");
        Assert.Equal([two], found.Select(f => f.ComplaintId)); // same description, so identical vectors

        await Embed(s, one, Guid.NewGuid()); // a photo that does not exist stores nothing and does not fail
    }

    [Fact]
    public async Task A_failing_service_is_retried_and_after_the_last_retry_the_complaint_just_has_no_vector()
    {
        var s = await Society.CreateAsync(app);
        var ai = app.Services.GetRequiredService<FakeAiClient>();
        var id = await Complain(s, "Tap leaking");
        var attempts = app.Services.GetRequiredService<UrbanFix.Infrastructure.Jobs.JobOptions>().Attempts;

        ai.Down = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Embed(s, id));
            await Embed(s, id, retries: attempts); // gives up quietly
        }
        finally { ai.Down = false; }
        Assert.Empty(await Similar(s, id, "?minSimilarity=0"));
    }

    [Fact]
    public async Task Only_staff_who_can_read_complaints_may_ask_and_a_missing_complaint_is_a_404()
    {
        var s = await Society.CreateAsync(app);
        var id = await Complain(s, "Anything");
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Resident.Client.GetAsync($"/complaints/{id}/similar")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Supervisor.Client.GetAsync($"/complaints/{Guid.NewGuid()}/similar")).StatusCode);
    }

    [Fact]
    public async Task The_search_stays_fast_with_thousands_of_vectors()
    {
        var s = await Society.CreateAsync(app);
        var probe = await Complain(s, "Search probe complaint");
        await Embed(s, probe);
        var other = await Complain(s, "Search probe complaint");
        await Sql("""
            INSERT INTO complaint_embeddings (id, organization_id, complaint_id, source, media_id, model, embedding)
            SELECT gen_random_uuid(), @o, @c, 'photo', gen_random_uuid(), 'fake-embed',
                   ARRAY(SELECT random()::real FROM generate_series(1, 64) WHERE g.n IS NOT NULL)::vector
            FROM generate_series(1, 5000) AS g(n)
            """, ("o", s.Org), ("c", other));

        await Similar(s, probe, "?minSimilarity=0"); // warm up
        var watch = Stopwatch.StartNew();
        var found = await Similar(s, probe, "?minSimilarity=0");
        watch.Stop();
        Assert.Contains(found, f => f.ComplaintId == other);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }
}
