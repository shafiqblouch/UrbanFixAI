namespace UrbanFix.SharedKernel;

public interface IDomainEvent
{
    Guid EventId { get; }
    DateTimeOffset OccurredAt { get; }
    Guid OrganizationId { get; }
}

public abstract record DomainEvent(Guid OrganizationId) : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

public interface IEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task Handle(TEvent domainEvent, CancellationToken ct);
}

/// <summary>
/// Stages an event in the current unit of work. It is written to the outbox in the same transaction as the
/// state change and delivered to subscribers afterwards, so a rolled-back change never leaks an event.
/// </summary>
public interface IEventBus
{
    void Publish(IDomainEvent domainEvent);
}
