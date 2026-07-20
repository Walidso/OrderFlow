using InventoryService.Worker.Consumers;
using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Stock;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrderFlow.Contracts;
using Xunit;

namespace InventoryService.UnitTests;

/// <summary>
/// Consumer tests mock ConsumeContext&lt;T&gt; directly (it's just an
/// interface) instead of standing up a real or in-memory bus — the fastest,
/// least flaky way to unit test what a single Consume() call does, matching
/// how OrderService.UnitTests calls handlers directly rather than going
/// through MediatR. IStockStore/IProcessedOrderStore are mocked here too;
/// EfStockStoreTests/EfProcessedOrderStoreTests cover the real, persisted
/// implementations separately.
/// </summary>
public class OrderCreatedConsumerTests
{
    private static ConsumeContext<OrderCreated> CreateContext(OrderCreated message)
    {
        var context = Substitute.For<ConsumeContext<OrderCreated>>();
        context.Message.Returns(message);
        return context;
    }

    private static IStockStore StockThatAlwaysReserves()
    {
        var stock = Substitute.For<IStockStore>();
        stock.TryReserveAsync(Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StockReservationResult.Reserved()));
        return stock;
    }

    [Fact]
    public async Task Consume_StockAvailable_PublishesStockReserved()
    {
        var consumer = new OrderCreatedConsumer(
            StockThatAlwaysReserves(), new InMemoryProcessedOrderStore(), NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var context = CreateContext(new OrderCreated(orderId, Guid.NewGuid(),
            new List<OrderLine> { new("APPLE-1", 2) }, DateTime.UtcNow));

        await consumer.Consume(context);

        await context.Received(1).Publish(
            Arg.Is<StockReserved>(e => e.OrderId == orderId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_StockUnavailable_PublishesStockRejected()
    {
        var stock = Substitute.For<IStockStore>();
        stock.TryReserveAsync(Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StockReservationResult.Rejected("Insufficient stock for 'MANGO-1'.")));

        var consumer = new OrderCreatedConsumer(
            stock, new InMemoryProcessedOrderStore(), NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var context = CreateContext(new OrderCreated(orderId, Guid.NewGuid(),
            new List<OrderLine> { new("MANGO-1", 10) }, DateTime.UtcNow));

        await consumer.Consume(context);

        await context.Received(1).Publish(
            Arg.Is<StockRejected>(e => e.OrderId == orderId && e.Reason.Contains("MANGO-1")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_PoisonProduct_ThrowsWithoutTouchingStockOrPublishing()
    {
        var stock = StockThatAlwaysReserves();
        var consumer = new OrderCreatedConsumer(
            stock, new InMemoryProcessedOrderStore(), NullLogger<OrderCreatedConsumer>.Instance);

        var context = CreateContext(new OrderCreated(Guid.NewGuid(), Guid.NewGuid(),
            new List<OrderLine> { new("DURIAN-1", 1) }, DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(context));

        await stock.DidNotReceive().TryReserveAsync(
            Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_DuplicateDeliveryOfSameOrder_DoesNotReserveOrPublishTwice()
    {
        // Simulates the exact scenario the outbox relay can cause: it
        // republishes OrderCreated as a brand-new message (new MessageId)
        // if it crashes after a successful publish but before marking its
        // row processed. Same OrderId, different message.
        var stock = StockThatAlwaysReserves();
        var processed = new InMemoryProcessedOrderStore();
        var consumer = new OrderCreatedConsumer(stock, processed, NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine> { new("APPLE-1", 2) };

        await consumer.Consume(CreateContext(new OrderCreated(orderId, Guid.NewGuid(), lines, DateTime.UtcNow)));

        var redeliveryContext = CreateContext(new OrderCreated(orderId, Guid.NewGuid(), lines, DateTime.UtcNow));
        await consumer.Consume(redeliveryContext);

        await stock.Received(1).TryReserveAsync(Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>());
        await redeliveryContext.DidNotReceive().Publish(
            Arg.Any<StockReserved>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_ThrownException_DoesNotMarkOrderProcessed()
    {
        // A throw means MassTransit will redeliver the SAME attempt per the
        // retry ladder in Program.cs. If we marked the order processed
        // before succeeding, that retry would silently no-op instead of
        // actually retrying — breaking the retry/error-queue demo.
        var stock = StockThatAlwaysReserves();
        var processed = new InMemoryProcessedOrderStore();
        var consumer = new OrderCreatedConsumer(stock, processed, NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var context = CreateContext(new OrderCreated(orderId, Guid.NewGuid(),
            new List<OrderLine> { new("DURIAN-1", 1) }, DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(context));

        Assert.False(await processed.HasBeenProcessedAsync(orderId));
    }
}
