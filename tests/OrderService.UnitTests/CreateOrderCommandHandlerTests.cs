using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using OrderFlow.Contracts;
using OrderService.Application.Abstractions;
using OrderService.Application.Orders.Commands.CreateOrder;
using OrderService.Application.Orders.Dtos;
using OrderService.Domain.Entities;
using OrderService.Infrastructure.Outbox;
using Xunit;

namespace OrderService.UnitTests;

public class CreateOrderCommandHandlerTests
{
    // Test names read as sentences: Method_Scenario_ExpectedOutcome.
    // When one fails in CI, the name alone tells you what broke.

    [Fact]
    public async Task Handle_ValidCommand_SavesOrderAsPending()
    {
        // ---- Arrange: build the world this test needs ----
        await using var db = TestDbContextFactory.Create();
        var handler = new CreateOrderCommandHandler(db, new EfOutboxWriter(db));

        var userId = Guid.NewGuid();
        var command = new CreateOrderCommand(userId, new List<OrderItemInput>
        {
            new("APPLE-1", "Apple", 3, 25.00m)
        });

        // ---- Act: the one line under test ----
        var orderId = await handler.Handle(command, CancellationToken.None);

        // ---- Assert: check observable outcomes ----
        var saved = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == orderId);
        Assert.Equal(OrderStatus.Pending, saved.Status);   // orders always start Pending
        Assert.Equal(userId, saved.UserId);
        Assert.Single(saved.Items);
    }

    [Fact]
    public async Task Handle_ValidCommand_EnqueuesOrderCreatedOnTheOutbox()
    {
        await using var db = TestDbContextFactory.Create();
        var outbox = Substitute.For<IOutboxWriter>(); // mock: records calls, does nothing
        var handler = new CreateOrderCommandHandler(db, outbox);

        var command = new CreateOrderCommand(Guid.NewGuid(), new List<OrderItemInput>
        {
            new("APPLE-1", "Apple", 2, 25.00m)
        });

        var orderId = await handler.Handle(command, CancellationToken.None);

        // The Inventory Service only learns about orders via this event —
        // if it stops being enqueued, the whole system silently breaks.
        // This assertion is the guard rail.
        outbox.Received(1).Enqueue(
            Arg.Is<OrderCreated>(e =>
                e.OrderId == orderId &&
                e.Lines.Count == 1 &&
                e.Lines[0].ProductId == "APPLE-1" &&
                e.Lines[0].Quantity == 2));
    }

    [Fact]
    public async Task Handle_ValidCommand_PersistsOutboxMessageInSameTransactionAsOrder()
    {
        // This is the atomicity guarantee itself, not just an interaction:
        // use the REAL EfOutboxWriter (not a mock) against the same
        // DbContext, then prove a single SaveChangesAsync committed both the
        // Order and its OutboxMessage row together.
        await using var db = TestDbContextFactory.Create();
        var handler = new CreateOrderCommandHandler(db, new EfOutboxWriter(db));

        var command = new CreateOrderCommand(Guid.NewGuid(), new List<OrderItemInput>
        {
            new("APPLE-1", "Apple", 3, 25.00m)
        });

        var orderId = await handler.Handle(command, CancellationToken.None);

        Assert.True(await db.Orders.AnyAsync(o => o.Id == orderId));

        var outboxMessage = await db.OutboxMessages.SingleAsync();
        Assert.Null(outboxMessage.ProcessedOnUtc); // not dispatched yet — that's the relay's job
        Assert.Contains(nameof(OrderCreated), outboxMessage.Type);

        var payload = JsonSerializer.Deserialize<OrderCreated>(outboxMessage.Content)!;
        Assert.Equal(orderId, payload.OrderId);
    }

    [Fact]
    public async Task Handle_MultipleItems_ComputesTotalFromAllLines()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new CreateOrderCommandHandler(db, new EfOutboxWriter(db));

        var command = new CreateOrderCommand(Guid.NewGuid(), new List<OrderItemInput>
        {
            new("APPLE-1", "Apple", 3, 25.00m),         //  75.00
            new("MANGO-1", "Mango", 2, 10.50m)          //  21.00
        });

        var orderId = await handler.Handle(command, CancellationToken.None);

        var saved = await db.Orders.Include(o => o.Items).SingleAsync(o => o.Id == orderId);
        Assert.Equal(96.00m, saved.Total); // computed property, never stored
    }
}
