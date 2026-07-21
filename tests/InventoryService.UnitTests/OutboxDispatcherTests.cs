using InventoryService.Worker.Outbox;
using InventoryService.Worker.Persistence;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OrderFlow.Contracts;
using Xunit;

namespace InventoryService.UnitTests;

/// <summary>
/// Same shape as OrderService.UnitTests.OutboxDispatcherTests — see its
/// comments for the reasoning. Publishing here goes through MassTransit's
/// IPublishEndpoint directly rather than a custom IEventPublisher port,
/// matching how Inventory's OutboxDispatcher is actually wired.
/// </summary>
public class OutboxDispatcherTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly InventoryDbContext _db;

    public OutboxDispatcherTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(_connection).Options;
        _db = new InventoryDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private OutboxDispatcher CreateSut(IPublishEndpoint publishEndpoint, int maxRetries = 5) =>
        new(_db, publishEndpoint, NullLogger<OutboxDispatcher>.Instance,
            Options.Create(new OutboxOptions { BatchSize = 20, MaxRetries = maxRetries }));

    private void Enqueue(object @event)
    {
        var type = @event.GetType();
        _db.OutboxMessages.Add(OutboxMessage.Create(
            type.AssemblyQualifiedName!, System.Text.Json.JsonSerializer.Serialize(@event, type), DateTime.UtcNow));
    }

    [Fact]
    public async Task DispatchPendingAsync_UnprocessedMessage_PublishesAndMarksProcessed()
    {
        var orderId = Guid.NewGuid();
        Enqueue(new StockReserved(orderId));
        await _db.SaveChangesAsync();

        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        var sut = CreateSut(publishEndpoint);

        var dispatched = await sut.DispatchPendingAsync();

        Assert.Equal(1, dispatched);
        await publishEndpoint.Received(1).Publish(
            Arg.Is<object>(o => ((StockReserved)o).OrderId == orderId),
            typeof(StockReserved),
            Arg.Any<CancellationToken>());

        var message = await _db.OutboxMessages.SingleAsync();
        Assert.NotNull(message.ProcessedOnUtc);
    }

    [Fact]
    public async Task DispatchPendingAsync_PublishThrows_MarksFailedWithBackoffAndLeavesUnprocessed()
    {
        Enqueue(new StockReserved(Guid.NewGuid()));
        await _db.SaveChangesAsync();

        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.Publish(Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("broker unreachable")));
        var sut = CreateSut(publishEndpoint);

        await sut.DispatchPendingAsync();

        var message = await _db.OutboxMessages.SingleAsync();
        Assert.Null(message.ProcessedOnUtc);
        Assert.Equal(1, message.RetryCount);
        Assert.NotNull(message.NextAttemptUtc);
        Assert.True(message.NextAttemptUtc > DateTime.UtcNow);
    }

    [Fact]
    public async Task DispatchPendingAsync_MessageAtMaxRetries_IsSkipped()
    {
        Enqueue(new StockReserved(Guid.NewGuid()));
        await _db.SaveChangesAsync();

        var publishEndpoint = Substitute.For<IPublishEndpoint>();
        publishEndpoint.Publish(Arg.Any<object>(), Arg.Any<Type>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("poison payload")));

        var sut = CreateSut(publishEndpoint, maxRetries: 1);

        await sut.DispatchPendingAsync(); // attempt 1: fails, RetryCount -> 1
        var secondPoll = await sut.DispatchPendingAsync();

        Assert.Equal(0, secondPoll);
        var message = await _db.OutboxMessages.SingleAsync();
        Assert.Equal(1, message.RetryCount);
        Assert.Null(message.ProcessedOnUtc); // still visible for a human, not silently dropped
    }
}
