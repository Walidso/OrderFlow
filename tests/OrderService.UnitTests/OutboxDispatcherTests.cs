using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OrderFlow.Contracts;
using OrderService.Application.Abstractions;
using OrderService.Infrastructure.Outbox;
using OrderService.Infrastructure.Persistence;
using Xunit;

namespace OrderService.UnitTests;

/// <summary>
/// The relay half of the outbox pattern. These tests don't touch a real
/// broker — IEventPublisher is mocked — because the thing under test is the
/// dispatcher's own logic: which rows it picks up, how it marks them, and
/// that one failure doesn't take the rest of the batch down with it.
/// </summary>
public class OutboxDispatcherTests
{
    private static OutboxDispatcher CreateSut(
        OrderDbContext db,
        IEventPublisher publisher,
        int maxRetries = 5) =>
        new(db, publisher, NullLogger<OutboxDispatcher>.Instance,
            Options.Create(new OutboxOptions { BatchSize = 20, MaxRetries = maxRetries }));

    [Fact]
    public async Task DispatchPendingAsync_UnprocessedMessage_PublishesAndMarksProcessed()
    {
        await using var db = TestDbContextFactory.Create();
        var @event = new OrderCreated(Guid.NewGuid(), Guid.NewGuid(),
            new List<OrderLine> { new("APPLE-1", 1) }, DateTime.UtcNow);

        var writer = new EfOutboxWriter(db);
        writer.Enqueue(@event);
        await db.SaveChangesAsync();

        var publisher = Substitute.For<IEventPublisher>();
        var sut = CreateSut(db, publisher);

        var dispatched = await sut.DispatchPendingAsync();

        Assert.Equal(1, dispatched);
        await publisher.Received(1).PublishAsync(
            Arg.Is<object>(o => ((OrderCreated)o).OrderId == @event.OrderId),
            typeof(OrderCreated),
            Arg.Any<CancellationToken>());

        var message = await db.OutboxMessages.SingleAsync();
        Assert.NotNull(message.ProcessedOnUtc);
    }

    [Fact]
    public async Task DispatchPendingAsync_AlreadyProcessedMessage_IsNotRedispatched()
    {
        await using var db = TestDbContextFactory.Create();
        var writer = new EfOutboxWriter(db);
        writer.Enqueue(new OrderCreated(Guid.NewGuid(), Guid.NewGuid(),
            new List<OrderLine> { new("APPLE-1", 1) }, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var publisher = Substitute.For<IEventPublisher>();
        var sut = CreateSut(db, publisher);

        await sut.DispatchPendingAsync(); // first poll: dispatches it
        var dispatchedAgain = await sut.DispatchPendingAsync(); // second poll: nothing left to do

        Assert.Equal(0, dispatchedAgain);
        await publisher.Received(1).PublishAsync(
            Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchPendingAsync_PublishThrows_MarksFailedAndLeavesUnprocessedForRetry()
    {
        await using var db = TestDbContextFactory.Create();
        var writer = new EfOutboxWriter(db);
        writer.Enqueue(new OrderCreated(Guid.NewGuid(), Guid.NewGuid(),
            new List<OrderLine> { new("APPLE-1", 1) }, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var publisher = Substitute.For<IEventPublisher>();
        publisher.PublishAsync(Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("broker unreachable")));
        var sut = CreateSut(db, publisher);

        await sut.DispatchPendingAsync();

        // A broker outage is not the message's fault — it stays unprocessed
        // so the next poll retries it, exactly like a transient fault would
        // in the RabbitMQ retry policy on the consumer side.
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Null(message.ProcessedOnUtc);
        Assert.Equal(1, message.RetryCount);
        Assert.Contains("broker unreachable", message.Error);
    }

    [Fact]
    public async Task DispatchPendingAsync_MessageAtMaxRetries_IsSkipped()
    {
        await using var db = TestDbContextFactory.Create();
        var writer = new EfOutboxWriter(db);
        writer.Enqueue(new OrderCreated(Guid.NewGuid(), Guid.NewGuid(),
            new List<OrderLine> { new("APPLE-1", 1) }, DateTime.UtcNow));
        await db.SaveChangesAsync();

        var publisher = Substitute.For<IEventPublisher>();
        publisher.PublishAsync(Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("poison payload")));

        // MaxRetries: 1 means the row is skipped after its first failure —
        // exercises the same code path a real deployment reaches after
        // enough attempts, without needing five real polls in a test.
        var sut = CreateSut(db, publisher, maxRetries: 1);

        await sut.DispatchPendingAsync(); // attempt 1: fails, RetryCount -> 1
        var secondPoll = await sut.DispatchPendingAsync(); // RetryCount (1) is no longer < MaxRetries (1)

        Assert.Equal(0, secondPoll);
        var message = await db.OutboxMessages.SingleAsync();
        Assert.Equal(1, message.RetryCount);
        Assert.Null(message.ProcessedOnUtc); // still sitting there for a human to inspect, not silently dropped
    }
}
