using MassTransit;
using OrderService.Application.Abstractions;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// The concrete IEventPublisher: a thin adapter over MassTransit.
/// MassTransit's Publish() creates a RabbitMQ *exchange* named after the
/// message type and fans the message out to every queue bound to it —
/// conceptually the same as an Azure Service Bus TOPIC with subscriptions,
/// which you already know. Exchange ~= Topic, bound queue ~= Subscription.
/// </summary>
public sealed class MassTransitEventPublisher : IEventPublisher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitEventPublisher(IPublishEndpoint publishEndpoint)
        => _publishEndpoint = publishEndpoint;

    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken cancellationToken = default)
        where TEvent : class
        => _publishEndpoint.Publish(@event, cancellationToken);
}
