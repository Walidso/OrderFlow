using Microsoft.EntityFrameworkCore;
using NSubstitute;
using OrderFlow.Contracts;
using OrderService.Application.Abstractions;
using OrderService.Application.Orders.Commands.CreateOrder;
using OrderService.Application.Orders.Dtos;
using OrderService.Domain.Entities;
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
        var publisher = Substitute.For<IEventPublisher>(); // mock: records calls, does nothing
        var handler = new CreateOrderCommandHandler(db, publisher);

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
    public async Task Handle_ValidCommand_PublishesOrderCreatedEvent()
    {
        await using var db = TestDbContextFactory.Create();
        var publisher = Substitute.For<IEventPublisher>();
        var handler = new CreateOrderCommandHandler(db, publisher);

        var command = new CreateOrderCommand(Guid.NewGuid(), new List<OrderItemInput>
        {
            new("APPLE-1", "Apple", 2, 25.00m)
        });

        var orderId = await handler.Handle(command, CancellationToken.None);

        // The Inventory Service only learns about orders via this event —
        // if it stops being published, the whole system silently breaks.
        // This assertion is the guard rail.
        await publisher.Received(1).PublishAsync(
            Arg.Is<OrderCreated>(e =>
                e.OrderId == orderId &&
                e.Lines.Count == 1 &&
                e.Lines[0].ProductId == "APPLE-1" &&
                e.Lines[0].Quantity == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_MultipleItems_ComputesTotalFromAllLines()
    {
        await using var db = TestDbContextFactory.Create();
        var handler = new CreateOrderCommandHandler(db, Substitute.For<IEventPublisher>());

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
