using MediatR;
using OrderFlow.Contracts;
using OrderService.Application.Abstractions;
using OrderService.Domain.Entities;

namespace OrderService.Application.Orders.Commands.CreateOrder;

/// <summary>
/// The write-side workflow:
///   1. Build the Order aggregate (Domain enforces its own rules).
///   2. Persist it (status = Pending).
///   3. Publish OrderCreated so the Inventory Service can react.
///
/// The handler depends only on ABSTRACTIONS (IApplicationDbContext,
/// IEventPublisher) — that's what makes the unit tests in
/// tests/OrderService.UnitTests possible without Postgres or RabbitMQ.
///
/// HONEST LIMITATION (great interview material): steps 2 and 3 are not
/// atomic. If the process dies between SaveChanges and Publish, we have an
/// order but no event. The production-grade fix is the Transactional Outbox
/// pattern — listed under "future improvements" in the README.
/// </summary>
public sealed class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, Guid>
{
    private readonly IApplicationDbContext _db;
    private readonly IEventPublisher _publisher;

    public CreateOrderCommandHandler(IApplicationDbContext db, IEventPublisher publisher)
    {
        _db = db;
        _publisher = publisher;
    }

    public async Task<Guid> Handle(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        // Map input DTOs to Domain objects. The factory methods throw if the
        // data violates business rules (belt-and-braces after validation).
        var items = request.Items
            .Select(i => OrderItem.Create(i.ProductId, i.ProductName, i.Quantity, i.UnitPrice))
            .ToList();

        var order = Order.Create(request.UserId, items);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);

        // Publish AFTER a successful save — never announce something that
        // might still be rolled back.
        var lines = request.Items
            .Select(i => new OrderLine(i.ProductId, i.Quantity))
            .ToList();

        await _publisher.PublishAsync(
            new OrderCreated(order.Id, order.UserId, lines, order.CreatedAtUtc),
            cancellationToken);

        return order.Id;
    }
}
