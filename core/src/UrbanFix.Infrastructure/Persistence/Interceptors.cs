using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using UrbanFix.Identity;
using UrbanFix.SharedKernel;

namespace UrbanFix.Infrastructure.Persistence;

/// <summary>Who and from where, for the audit log. Filled per request by the API; empty in jobs.</summary>
public sealed class RequestInfo
{
    public string? Ip { get; set; }
    public string? Device { get; set; }
}

/// <summary>Applies the tenant context to every connection as it opens; PostgreSQL row-level security reads it.</summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    private const string Sql = "SELECT set_config('app.org_id', @org, false), set_config('app.user_id', @usr, false)";

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
        => await Apply(connection, ct);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection, CancellationToken.None).GetAwaiter().GetResult();

    private async Task Apply(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = Sql;
        AddParam(cmd, "org", tenant.OrganizationId?.ToString() ?? "");
        AddParam(cmd, "usr", tenant.UserId?.ToString() ?? "");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, string value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}

/// <summary>
/// Runs inside SaveChanges so the audit rows and outbox rows commit atomically with the change they describe.
/// </summary>
public sealed class UnitOfWorkInterceptor(ITenantContext tenant, EventBus bus, RequestInfo request) : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is { } db) Capture(db);
        return base.SavingChangesAsync(eventData, result, ct);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db) Capture(db);
        return base.SavingChanges(eventData, result);
    }

    private void Capture(DbContext db)
    {
        db.ChangeTracker.DetectChanges();

        var audits = new List<AuditEntry>();
        foreach (var entry in db.ChangeTracker.Entries<IAudited>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var action = entry.State switch
            {
                EntityState.Added => "created",
                EntityState.Deleted => "deleted",
                _ => "updated",
            };
            audits.Add(new AuditEntry
            {
                OrganizationId = entry.Entity.OrganizationId,
                ActorUserId = tenant.UserId,
                Action = action,
                EntityType = entry.Metadata.ClrType.Name,
                EntityId = entry.Entity.Id.ToString(),
                Before = entry.State == EntityState.Added ? null : Snapshot(entry, original: true),
                After = entry.State == EntityState.Deleted ? null : Snapshot(entry, original: false),
                Ip = request.Ip,
                Device = request.Device,
            });
        }
        db.AddRange(audits);

        foreach (var e in bus.Drain())
        {
            db.Add(new OutboxMessage
            {
                Id = e.EventId,
                OrganizationId = e.OrganizationId,
                Type = $"{e.GetType().FullName}, {e.GetType().Assembly.GetName().Name}",
                Payload = JsonSerializer.Serialize(e, e.GetType(), Json),
                OccurredAt = e.OccurredAt,
            });
        }
    }

    private static string Snapshot(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, bool original)
    {
        var values = entry.Properties.ToDictionary(
            p => p.Metadata.Name,
            p => original ? p.OriginalValue : p.CurrentValue);
        return JsonSerializer.Serialize(values, Json);
    }
}

public sealed class EventBus : IEventBus
{
    private readonly List<IDomainEvent> _pending = [];

    public void Publish(IDomainEvent domainEvent) => _pending.Add(domainEvent);

    internal IReadOnlyList<IDomainEvent> Drain()
    {
        var events = _pending.ToArray();
        _pending.Clear();
        return events;
    }
}
