using System.Text.Json;
using InventoryService.Worker.Persistence;
using InventoryService.Worker.Reservations;
using InventoryService.Worker.Stock;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using Xunit;

namespace InventoryService.UnitTests;

/// <summary>
/// This is the atomicity guarantee itself, checked against the real EF
/// implementations (not mocks): a single ReserveAsync call must leave the
/// stock change, the idempotency marker, and the outbox row that will
/// eventually publish the outcome ALL consistent with each other — because
/// they all came from the same transaction (or, on rejection, from writes
/// with no partial stock changes to worry about). SQLite, for the same
/// "real relational engine, no Docker" reason used elsewhere in this repo.
/// </summary>
public class StockReservationCoordinatorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly InventoryDbContext _db;
    private readonly StockReservationCoordinator _sut;

    public StockReservationCoordinatorTests()
    {
        _connection.Open();

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new InventoryDbContext(options);
        _db.Database.EnsureCreated(); // applies the HasData seed rows too
        _sut = new StockReservationCoordinator(_db, new EfStockStore(_db));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ReserveAsync_SufficientStock_CommitsStockChangeMarkerAndOutboxRowTogether()
    {
        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine> { new("APPLE-1", 3) };

        var result = await _sut.ReserveAsync(orderId, lines);

        Assert.True(result.Success);

        var stock = await _db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == "APPLE-1");
        Assert.Equal(97, stock.AvailableQuantity); // seeded at 100

        Assert.True(await _db.ProcessedOrders.AnyAsync(p => p.OrderId == orderId));

        var outboxMessage = await _db.OutboxMessages.SingleAsync();
        Assert.Contains(nameof(StockReserved), outboxMessage.Type);
        var payload = JsonSerializer.Deserialize<StockReserved>(outboxMessage.Content)!;
        Assert.Equal(orderId, payload.OrderId);
        Assert.Null(outboxMessage.ProcessedOnUtc); // enqueued, not yet dispatched — that's the relay's job
    }

    [Fact]
    public async Task ReserveAsync_InsufficientStock_MarksProcessedAndEnqueuesRejectionWithNoStockChange()
    {
        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine> { new("MANGO-1", 10) }; // seeded at 3

        var result = await _sut.ReserveAsync(orderId, lines);

        Assert.False(result.Success);

        // Even on rejection, the order's fate IS decided — the marker and
        // the outbox row must still exist, just with no stock touched.
        var stock = await _db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == "MANGO-1");
        Assert.Equal(3, stock.AvailableQuantity);

        Assert.True(await _db.ProcessedOrders.AnyAsync(p => p.OrderId == orderId));

        var outboxMessage = await _db.OutboxMessages.SingleAsync();
        Assert.Contains(nameof(StockRejected), outboxMessage.Type);
        var payload = JsonSerializer.Deserialize<StockRejected>(outboxMessage.Content)!;
        Assert.Equal(orderId, payload.OrderId);
        Assert.Contains("MANGO-1", payload.Reason);
    }

    [Fact]
    public async Task ReserveAsync_MultiLineWhereSecondLineFails_RollsBackFirstLinesStockChange()
    {
        // Proves the coordinator's rollback path actually undoes a partial
        // reservation — not just that it reports failure. APPLE-1 alone
        // would succeed; MANGO-1 in the same order can't be satisfied.
        var orderId = Guid.NewGuid();
        var lines = new List<OrderLine>
        {
            new("APPLE-1", 5),
            new("MANGO-1", 10) // only 3 available
        };

        var result = await _sut.ReserveAsync(orderId, lines);

        Assert.False(result.Success);

        var apple = await _db.StockItems.AsNoTracking().SingleAsync(s => s.ProductId == "APPLE-1");
        Assert.Equal(100, apple.AvailableQuantity); // NOT 95 — rolled back

        var outboxMessage = await _db.OutboxMessages.SingleAsync();
        Assert.Contains(nameof(StockRejected), outboxMessage.Type);
    }
}
