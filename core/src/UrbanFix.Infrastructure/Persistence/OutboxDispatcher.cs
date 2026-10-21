using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Persistence;

public sealed class OutboxOptions
{
    public int MaxAttempts { get; set; } = 8;
    public int BatchSize { get; set; } = 50;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Delivers outbox messages to <see cref="IEventHandler{TEvent}"/> subscribers, at least once. A failing
/// message is retried with exponential backoff and moved to dead-letter after MaxAttempts. Handlers must be
/// idempotent: if one handler of several fails, the message is retried for all of them.
/// </summary>
public sealed class OutboxProcessor(IServiceScopeFactory scopes, OutboxOptions options, ILogger<OutboxProcessor> log)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Processes one batch; returns how many messages were attempted.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.Outbox
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND dead_lettered_at IS NULL AND next_attempt_at <= now()
                ORDER BY occurred_at
                LIMIT {options.BatchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                await Deliver(message, ct);
                message.ProcessedAt = DateTimeOffset.UtcNow;
                message.Error = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                if (message.Attempts >= options.MaxAttempts)
                {
                    message.DeadLetteredAt = DateTimeOffset.UtcNow;
                    log.LogError(ex, "Outbox message {Id} ({Type}) dead-lettered after {Attempts} attempts", message.Id, message.Type, message.Attempts);
                }
                else
                {
                    var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, message.Attempts), options.MaxBackoff.TotalSeconds));
                    message.NextAttemptAt = DateTimeOffset.UtcNow + delay;
                    log.LogWarning(ex, "Outbox message {Id} ({Type}) failed, retry in {Delay}", message.Id, message.Type, delay);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count;
    }

    private async Task Deliver(OutboxMessage message, CancellationToken ct)
    {
        var type = Type.GetType(message.Type) ?? throw new InvalidOperationException($"Unknown event type {message.Type}");
        var domainEvent = (IDomainEvent)JsonSerializer.Deserialize(message.Payload, type, Json)!;

        // Each delivery runs in its own scope, as the event's organization, so handlers are tenant-isolated.
        await using var scope = scopes.CreateAsyncScope();
        ((TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>()).Set(message.OrganizationId, null);

        var handlerType = typeof(IEventHandler<>).MakeGenericType(type);
        var handle = handlerType.GetMethod(nameof(IEventHandler<IDomainEvent>.Handle))!;
        foreach (var handler in scope.ServiceProvider.GetServices(handlerType))
            await (Task)handle.Invoke(handler, [domainEvent, ct])!;
    }
}

public sealed class OutboxDispatcher(OutboxProcessor processor, OutboxOptions options, ILogger<OutboxDispatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await processor.ProcessBatchAsync(stoppingToken) > 0) continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Outbox dispatch loop error");
            }
            await Task.Delay(options.PollInterval, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
