namespace OrderService.Application.Abstractions;

/// <summary>
/// Publishes integration events to the message broker.
/// The Application layer doesn't know (or care) that the implementation is
/// MassTransit over RabbitMQ — tomorrow it could be Azure Service Bus and
/// not a single handler would change. That is the whole point of the port.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class;
    // Fun C# detail: "event" is a reserved keyword, so we escape the
    // parameter name with @. Purely cosmetic — the variable is named "event".
}
