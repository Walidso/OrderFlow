using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Messaging.Consumers;

/// <summary>
/// Closes the async loop: Inventory said "stock reserved", so we flip the
/// order from Pending to Confirmed.
///
/// MassTransit consumers are resolved from DI PER MESSAGE with a scoped
/// lifetime, so injecting the (scoped) DbContext directly is safe here —
/// each message gets a fresh context, just like each HTTP request does.
/// </summary>
public sealed class StockReservedConsumer : IConsumer<OrderFlow.Contracts.StockReserved>
{
    private readonly OrderDbContext _db;
    private readonly ILogger<StockReservedConsumer> _logger;

    public StockReservedConsumer(OrderDbContext db, ILogger<StockReservedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderFlow.Contracts.StockReserved> context)
    {
        var order = await _db.Orders
            .FirstOrDefaultAsync(o => o.Id == context.Message.OrderId);

        if (order is null)
        {
            // Can genuinely happen with eventual consistency (e.g. replayed
            // message for a deleted order). Log and move on — throwing would
            // send an unfixable message into retry/error loops for nothing.
            _logger.LogWarning("StockReserved for unknown order {OrderId}", context.Message.OrderId);
            return;
        }

        order.MarkConfirmed(); // idempotent — duplicate deliveries are no-ops
        await _db.SaveChangesAsync();

        _logger.LogInformation("Order {OrderId} confirmed", order.Id);
    }
}
