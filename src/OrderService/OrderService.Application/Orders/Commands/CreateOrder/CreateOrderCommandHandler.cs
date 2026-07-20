using MediatR;
using OrderFlow.Contracts;
using OrderService.Application.Abstractions;
using OrderService.Domain.Entities;

namespace OrderService.Application.Orders.Commands.CreateOrder;

/// <summary>
/// The write-side workflow:
///   1. Build the Order aggregate (Domain enforces its own rules).
///   2. Enqueue OrderCreated in the outbox.
///   3. Persist BOTH in one SaveChangesAsync — one transaction, one commit.
///
/// This is the Transactional Outbox pattern. The order row and its outbox
/// row live or die together: if SaveChangesAsync succeeds, both exist and
/// the relay (Infrastructure/Outbox/OutboxDispatcher) will eventually
/// publish the event, even if the process crashes right after this method
/// returns. If it fails, neither exists. There is no window where an order
/// is saved but its event is lost — the gap this project used to call out
/// as a known limitation is closed.
///
/// The handler depends only on ABSTRACTIONS (IApplicationDbContext,
/// IOutboxWriter) — that's what makes the unit tests in
/// tests/OrderService.UnitTests possible without Postgres or RabbitMQ.
/// </summary>
public sealed class CreateOrderCommandHandler : IRequestHandler<CreateOrderCommand, Guid>
{
    private readonly IApplicationDbContext _db;
    private readonly IOutboxWriter _outbox;

    public CreateOrderCommandHandler(IApplicationDbContext db, IOutboxWriter outbox)
    {
        _db = db;
        _outbox = outbox;
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

        var lines = request.Items
            .Select(i => new OrderLine(i.ProductId, i.Quantity))
            .ToList();

        // Staged, not sent. Enqueue() only adds a row to this same
        // DbContext — nothing hits the broker until the relay picks it up
        // after this transaction has actually committed.
        _outbox.Enqueue(new OrderCreated(order.Id, order.UserId, lines, order.CreatedAtUtc));

        await _db.SaveChangesAsync(cancellationToken);

        return order.Id;
    }
}
