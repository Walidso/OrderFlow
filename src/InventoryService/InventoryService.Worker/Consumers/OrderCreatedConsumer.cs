using InventoryService.Worker.Stock;
using MassTransit;
using OrderFlow.Contracts;

namespace InventoryService.Worker.Consumers;

/// <summary>
/// The heart of the Inventory Service: reacts to OrderCreated events.
///
/// ==================== FAILURE HANDLING: RETRY + ERROR QUEUE ================
/// This endpoint is configured (in Program.cs) with:
///     e.UseMessageRetry(r => r.Intervals(1s, 5s, 15s));
///
/// If Consume() throws, MassTransit retries after 1s, then 5s, then 15s
/// (increasing gaps give transient problems — a DB hiccup, a flaky network —
/// time to heal). If the LAST retry also throws, MassTransit moves the
/// message to a RabbitMQ queue named:
///
///     inventory-order-created_error
///
/// ...where it waits for a human, complete with the exception details
/// stamped into its headers.
///
/// >>> MAPPING TO YOUR AZURE SERVICE BUS EXPERIENCE <<<
/// The `_error` queue IS the dead-letter queue (DLQ), just spelled
/// differently:
///   - ASB: broker-native DLQ, entered after MaxDeliveryCount is exceeded
///     or when you explicitly call DeadLetterMessageAsync(...).
///   - RabbitMQ: no automatic DLQ concept per se — MassTransit builds the
///     equivalent by republishing the poisoned message to `<queue>_error`.
///   - Your manual `CompleteMessageAsync` in ASB ~= MassTransit acking the
///     message after Consume() returns without throwing. Throwing here is
///     the moral equivalent of AbandonMessageAsync.
/// Same philosophy in both: NEVER silently drop a failed message. Park it
/// where a human can inspect, fix, and replay it.
/// ============================================================================
/// </summary>
public sealed class OrderCreatedConsumer : IConsumer<OrderCreated>
{
    private readonly IStockStore _stock;
    private readonly ILogger<OrderCreatedConsumer> _logger;

    public OrderCreatedConsumer(IStockStore stock, ILogger<OrderCreatedConsumer> logger)
    {
        _stock = stock;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderCreated> context)
    {
        var message = context.Message;
        _logger.LogInformation("Received OrderCreated for order {OrderId}", message.OrderId);

        // --- Demo poison message -------------------------------------------
        // Ordering the product "GLITCH-1" makes this consumer throw so you
        // can WATCH the retry pattern happen: check the service logs (3
        // retries) and then the RabbitMQ UI (http://localhost:15672) for the
        // message sitting in inventory-order-created_error.
        if (message.Lines.Any(l => l.ProductId == "GLITCH-1"))
        {
            throw new InvalidOperationException(
                "GLITCH-1 ordered — simulated transient failure to demonstrate retry + error queue.");
        }
        // --------------------------------------------------------------------

        if (_stock.TryReserve(message.Lines, out var reason))
        {
            // context.Publish (instead of a raw IPublishEndpoint) ties the
            // outgoing event to the incoming message's conversation, so
            // correlation ids flow through — great for tracing.
            await context.Publish(new StockReserved(message.OrderId));
            _logger.LogInformation("Stock reserved for order {OrderId}", message.OrderId);
        }
        else
        {
            // Business failure != technical failure. Out-of-stock is a VALID
            // outcome, so we publish StockRejected and complete the message.
            // Throwing here would retry forever for something retries can't
            // fix. (Interview one-liner: "retry transient faults, never
            // business outcomes.")
            await context.Publish(new StockRejected(message.OrderId, reason));
            _logger.LogInformation(
                "Stock rejected for order {OrderId}: {Reason}", message.OrderId, reason);
        }
    }
}
