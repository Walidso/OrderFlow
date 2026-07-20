using InventoryService.Worker.Persistence;
using InventoryService.Worker.Stock;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;
using Xunit;

namespace InventoryService.UnitTests;

/// <summary>
/// SQLite, not EF's InMemory provider: EfStockStore's atomic reservation
/// uses ExecuteUpdateAsync, which the InMemory provider can't translate
/// (it doesn't generate SQL at all). SQLite is a real relational engine that
/// supports it, matching how OrderService.IntegrationTests uses SQLite for
/// the same "need real relational behavior, don't need Docker" reason.
///
/// These tests check the LOGIC deterministically (successful reserve,
/// all-or-nothing rollback, unknown product). The actual cross-replica
/// concurrency guarantee described in EfStockStore's comments comes from
/// Postgres's row-level locking on a conditional UPDATE — a property of the
/// real database, not something a single-connection SQLite test can
/// meaningfully stress-test.
/// </summary>
public class EfStockStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly InventoryDbContext _db;
    private readonly EfStockStore _sut;

    public EfStockStoreTests()
    {
        _connection.Open();

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new InventoryDbContext(options);
        _db.Database.EnsureCreated(); // also applies the HasData seed rows
        _sut = new EfStockStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task TryReserveAsync_SufficientStock_DecrementsAndReturnsSuccess()
    {
        var result = await _sut.TryReserveAsync(new List<OrderLine> { new("APPLE-1", 3) });

        Assert.True(result.Success);
        var snapshot = await _sut.SnapshotAsync();
        Assert.Equal(97, snapshot["APPLE-1"]); // seeded at 100
    }

    [Fact]
    public async Task TryReserveAsync_InsufficientStock_RejectsAndLeavesStockUntouched()
    {
        var result = await _sut.TryReserveAsync(new List<OrderLine> { new("MANGO-1", 10) }); // seeded at 3

        Assert.False(result.Success);
        Assert.Contains("MANGO-1", result.FailureReason);
        var snapshot = await _sut.SnapshotAsync();
        Assert.Equal(3, snapshot["MANGO-1"]); // unchanged
    }

    [Fact]
    public async Task TryReserveAsync_UnknownProduct_RejectsWithClearReason()
    {
        var result = await _sut.TryReserveAsync(new List<OrderLine> { new("KIWI-1", 1) });

        Assert.False(result.Success);
        Assert.Contains("Unknown product", result.FailureReason);
    }

    [Fact]
    public async Task TryReserveAsync_SecondLineFails_RollsBackFirstLinesDecrement()
    {
        // This is the ALL-OR-NOTHING guarantee itself: APPLE-1 has plenty of
        // stock and would succeed on its own, but MANGO-1 in the same order
        // can't be satisfied. The whole reservation must fail, and APPLE-1's
        // stock must come back exactly as it was — proving the transaction
        // actually rolled back, not just that the method returned false.
        var result = await _sut.TryReserveAsync(new List<OrderLine>
        {
            new("APPLE-1", 5),
            new("MANGO-1", 10) // only 3 available — this line fails
        });

        Assert.False(result.Success);
        var snapshot = await _sut.SnapshotAsync();
        Assert.Equal(100, snapshot["APPLE-1"]); // NOT 95 — rolled back
        Assert.Equal(3, snapshot["MANGO-1"]);
    }

    [Fact]
    public async Task SnapshotAsync_ReturnsSeededDemoInventory()
    {
        var snapshot = await _sut.SnapshotAsync();

        Assert.Equal(100, snapshot["APPLE-1"]);
        Assert.Equal(50, snapshot["BANANA-1"]);
        Assert.Equal(3, snapshot["MANGO-1"]);
        Assert.Equal(999, snapshot["DURIAN-1"]);
    }
}
