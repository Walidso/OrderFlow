using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryService.UnitTests;

public class EfProcessedOrderStoreTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly InventoryDbContext _db;
    private readonly EfProcessedOrderStore _sut;

    public EfProcessedOrderStoreTests()
    {
        _connection.Open();

        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new InventoryDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new EfProcessedOrderStore(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task HasBeenProcessedAsync_UnknownOrder_ReturnsFalse()
    {
        Assert.False(await _sut.HasBeenProcessedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task HasBeenProcessedAsync_AfterMarkProcessed_ReturnsTrue()
    {
        var orderId = Guid.NewGuid();

        await _sut.MarkProcessedAsync(orderId);

        Assert.True(await _sut.HasBeenProcessedAsync(orderId));
    }

    [Fact]
    public async Task HasBeenProcessedAsync_SurvivesANewDbContextAgainstTheSameDatabase()
    {
        // The whole point of moving off InMemoryProcessedOrderStore: this
        // guard must outlive the process/DbContext that wrote it, because a
        // restart shouldn't forget which orders were already decided.
        var orderId = Guid.NewGuid();
        await _sut.MarkProcessedAsync(orderId);

        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseSqlite(_connection).Options;
        await using var freshDb = new InventoryDbContext(options);
        var freshStore = new EfProcessedOrderStore(freshDb);

        Assert.True(await freshStore.HasBeenProcessedAsync(orderId));
    }
}
