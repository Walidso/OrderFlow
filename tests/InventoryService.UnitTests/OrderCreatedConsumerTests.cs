using InventoryService.Worker.Consumers;
using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Reservations;
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
/// through MediatR.
///
/// IStockReservationCoordinator is mocked here — this consumer's whole job
/// is "check idempotency, then delegate to the coordinator", so that's what
/// gets tested. The coordinator's own atomicity guarantees (stock + marker +
/// outbox row, one transaction) are covered separately in
/// StockReservationCoordinatorTests, against the real EF implementation.
/// </summary>
public class OrderCreatedConsumerTests
{
    private static ConsumeContext<OrderCreated> CreateContext(OrderCreated message)
    {
        var context = Substitute.For<ConsumeContext<OrderCreated>>();
        context.Message.Returns(message);
        return context;
    }

    private static IStockReservationCoordinator CoordinatorThatAlwaysReserves()
    {
        var coordinator = Substitute.For<IStockReservationCoordinator>();
        coordinator.ReserveAsync(Arg.Any<Guid>(), Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(StockReservationResult.Reserved()));
        return coordinator;
    }

    [Fact]
    public async Task Consume_NewOrder_DelegatesToCoordinator()
    {
        var coordinator = CoordinatorThatAlwaysReserves();
        var consumer = new OrderCreatedConsumer(
            new InMemoryProcessedOrderStore(), coordinator, NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine> { new("APPLE-1", 2) };
        var context = CreateContext(new OrderCreated(orderId, Guid.NewGuid(), lines, DateTime.UtcNow));

        await consumer.Consume(context);

        await coordinator.Received(1).ReserveAsync(orderId, lines, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Consume_PoisonProduct_ThrowsWithoutCallingCoordinatorOrMarkingProcessed()
    {
        // A throw means MassTransit will redeliver the SAME attempt per the
        // retry ladder in Program.cs — so this must not touch the
        // coordinator (no partial reservation) or the idempotency store (a
        // marked-processed order would make the retry silently no-op
        // instead of actually retrying).
        var coordinator = CoordinatorThatAlwaysReserves();
        var processed = new InMemoryProcessedOrderStore();
        var consumer = new OrderCreatedConsumer(processed, coordinator, NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var context = CreateContext(new OrderCreated(orderId, Guid.NewGuid(),
            new List<OrderLine> { new("DURIAN-1", 1) }, DateTime.UtcNow));

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(context));

        await coordinator.DidNotReceive().ReserveAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>());
        Assert.False(await processed.HasBeenProcessedAsync(orderId));
    }

    [Fact]
    public async Task Consume_DuplicateDeliveryOfSameOrder_CallsCoordinatorOnlyOnce()
    {
        // Simulates the exact scenario the outbox relay can cause: it
        // republishes OrderCreated as a brand-new message if it crashes
        // after enqueueing the outcome but before... actually it can't
        // anymore — StockReservationCoordinator marks the order processed
        // in the SAME transaction as the outbox row. This test still
        // matters: redelivery (e.g. a network blip re-acking) must not
        // call the coordinator twice regardless of why it happened.
        var coordinator = CoordinatorThatAlwaysReserves();
        var processed = new InMemoryProcessedOrderStore();
        var consumer = new OrderCreatedConsumer(processed, coordinator, NullLogger<OrderCreatedConsumer>.Instance);

        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine> { new("APPLE-1", 2) };

        await consumer.Consume(CreateContext(new OrderCreated(orderId, Guid.NewGuid(), lines, DateTime.UtcNow)));
        await processed.MarkProcessedAsync(orderId); // what the coordinator would have done for real
        await consumer.Consume(CreateContext(new OrderCreated(orderId, Guid.NewGuid(), lines, DateTime.UtcNow)));

        await coordinator.Received(1).ReserveAsync(
            Arg.Any<Guid>(), Arg.Any<IReadOnlyList<OrderLine>>(), Arg.Any<CancellationToken>());
    }
}
