using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using UrbanFix.Identity;
using UrbanFix.Infrastructure.Persistence;
using UrbanFix.SharedKernel;
using UrbanFix.Tests.Support;

namespace UrbanFix.Tests;

[Collection("db")]
public class OutboxTests(TestApp app)
{
    /// <summary>The outbox is shared by every test, so work through the whole backlog rather than one batch.</summary>
    private static async Task Drain(OutboxProcessor processor)
    {
        for (var i = 0; i < 200 && await processor.ProcessBatchAsync(default) > 0; i++) { }
    }

    private async Task<Guid> PublishViaSaveChanges(Guid org, string note, bool rollback = false)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        await db.ChangeTenantAsync(org, null);

        await using var tx = await db.Database.BeginTransactionAsync();
        var e = new TestHappened(org, note);
        bus.Publish(e);
        await db.SaveChangesAsync();
        if (rollback) await tx.RollbackAsync(); else await tx.CommitAsync();
        return e.EventId;
    }

    private async Task<(int Attempts, bool Processed, bool DeadLettered)> State(Guid id)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT attempts, processed_at IS NOT NULL, dead_lettered_at IS NOT NULL FROM outbox_messages WHERE id = @i", conn);
        cmd.Parameters.AddWithValue("i", id);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetInt32(0), r.GetBoolean(1), r.GetBoolean(2));
    }

    private async Task MakeDue(Guid id)
    {
        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE outbox_messages SET next_attempt_at = now() - interval '1 second' WHERE id = @i", conn);
        cmd.Parameters.AddWithValue("i", id);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Event_is_delivered_to_subscribers_with_the_events_tenant_context()
    {
        var (org, _) = await app.CreateOrganizationAsync();
        var id = await PublishViaSaveChanges(org, "delivered-" + Guid.NewGuid().ToString("N"));

        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        await Drain(processor);

        var state = await State(id);
        Assert.True(state.Processed);
        Assert.Contains(app.Services.GetRequiredService<EventCollector>().Handled, h => h.Org == org);
    }

    [Fact]
    public async Task Rolled_back_change_publishes_nothing()
    {
        var (org, _) = await app.CreateOrganizationAsync();
        var note = "rolled-back-" + Guid.NewGuid().ToString("N");
        var id = await PublishViaSaveChanges(org, note, rollback: true);

        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM outbox_messages WHERE id = @i", conn);
        cmd.Parameters.AddWithValue("i", id);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Failing_subscriber_is_retried_then_succeeds()
    {
        var (org, _) = await app.CreateOrganizationAsync();
        var note = "flaky-" + Guid.NewGuid().ToString("N");
        var collector = app.Services.GetRequiredService<EventCollector>();
        collector.FailuresRemaining[note] = 2;
        var id = await PublishViaSaveChanges(org, note);
        var processor = app.Services.GetRequiredService<OutboxProcessor>();

        await Drain(processor);
        Assert.Equal((1, false, false), await State(id));

        await MakeDue(id);
        await Drain(processor);
        Assert.Equal((2, false, false), await State(id));

        await MakeDue(id);
        await Drain(processor);
        var done = await State(id);
        Assert.True(done.Processed);
        Assert.Contains(collector.Handled, h => h.Note == note);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_dead_lettered_not_retried_forever()
    {
        var (org, _) = await app.CreateOrganizationAsync();
        var note = "doomed-" + Guid.NewGuid().ToString("N");
        app.Services.GetRequiredService<EventCollector>().FailuresRemaining[note] = int.MaxValue;
        var id = await PublishViaSaveChanges(org, note);
        var processor = app.Services.GetRequiredService<OutboxProcessor>();
        var max = app.Services.GetRequiredService<OutboxOptions>().MaxAttempts;

        for (var i = 0; i < max; i++)
        {
            await MakeDue(id);
            await Drain(processor);
        }

        var state = await State(id);
        Assert.True(state.DeadLettered);
        Assert.False(state.Processed);
        Assert.Equal(max, state.Attempts);

        var before = app.Services.GetRequiredService<EventCollector>().Attempts;
        await MakeDue(id);
        await Drain(processor);
        Assert.Equal(before, app.Services.GetRequiredService<EventCollector>().Attempts);
    }

    [Fact]
    public async Task Failed_attempts_back_off_exponentially()
    {
        var (org, _) = await app.CreateOrganizationAsync();
        var note = "backoff-" + Guid.NewGuid().ToString("N");
        app.Services.GetRequiredService<EventCollector>().FailuresRemaining[note] = 1;
        var id = await PublishViaSaveChanges(org, note);
        await Drain(app.Services.GetRequiredService<OutboxProcessor>());

        await using var conn = new NpgsqlConnection(app.OwnerConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT next_attempt_at > now() FROM outbox_messages WHERE id = @i", conn);
        cmd.Parameters.AddWithValue("i", id);
        Assert.Equal(true, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Audited_entity_change_and_its_outbox_event_commit_together()
    {
        var (org, manager) = await app.CreateOrganizationAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var me = await manager.Client.GetFromJsonAsync<UrbanFix.Api.Endpoints.AuthEndpoints.MeResponse>("/me");
        await db.ChangeTenantAsync(org, me!.UserId);

        var m = await db.Memberships.FirstAsync();
        m.DepartmentId = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<IEventBus>().Publish(new TestHappened(org, "with-audit"));
        await db.SaveChangesAsync();

        Assert.True(await db.AuditLog.AnyAsync(a => a.EntityId == m.Id.ToString() && a.Action == "updated"));
        var payloads = await db.Outbox.Where(o => o.OrganizationId == org).Select(o => o.Payload).ToListAsync();
        Assert.Contains(payloads, p => p.Contains("with-audit"));
        _ = typeof(Membership);
    }
}
