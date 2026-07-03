using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Infrastructure.Messaging.Consumers;

/// <summary>Mirror of StockReservedConsumer for the unhappy path.</summary>
public sealed class StockRejectedConsumer : IConsumer<OrderFlow.Contracts.StockRejected>
{
    private readonly OrderDbContext _db;
    private readonly ILogger<StockRejectedConsumer> _logger;

    public StockRejectedConsumer(OrderDbContext db, ILogger<StockRejectedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<OrderFlow.Contracts.StockRejected> context)
    {
        var order = await _db.Orders
            .FirstOrDefaultAsync(o => o.Id == context.Message.OrderId);

        if (order is null)
        {
            _logger.LogWarning("StockRejected for unknown order {OrderId}", context.Message.OrderId);
            return;
        }

        order.MarkRejected(context.Message.Reason);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Order {OrderId} rejected: {Reason}", order.Id, context.Message.Reason);
    }
}
