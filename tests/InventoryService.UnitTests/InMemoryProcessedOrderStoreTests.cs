using InventoryService.Worker.Idempotency;
using Xunit;

namespace InventoryService.UnitTests;

public class InMemoryProcessedOrderStoreTests
{
    [Fact]
    public async Task HasBeenProcessedAsync_UnknownOrder_ReturnsFalse()
    {
        var store = new InMemoryProcessedOrderStore();

        Assert.False(await store.HasBeenProcessedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task HasBeenProcessedAsync_AfterMarkProcessed_ReturnsTrue()
    {
        var store = new InMemoryProcessedOrderStore();
        var orderId = Guid.NewGuid();

        await store.MarkProcessedAsync(orderId);

        Assert.True(await store.HasBeenProcessedAsync(orderId));
    }

    [Fact]
    public async Task HasBeenProcessedAsync_DoesNotConfuseDifferentOrders()
    {
        var store = new InMemoryProcessedOrderStore();
        await store.MarkProcessedAsync(Guid.NewGuid());

        Assert.False(await store.HasBeenProcessedAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task MarkProcessedAsync_ConcurrentCallsForDifferentOrders_AllRecorded()
    {
        // Mirrors why InMemoryStockStore takes a lock: the bus can hand this
        // singleton messages for many orders concurrently.
        var store = new InMemoryProcessedOrderStore();
        var orderIds = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();

        await Task.WhenAll(orderIds.Select(id => store.MarkProcessedAsync(id)));

        foreach (var id in orderIds)
            Assert.True(await store.HasBeenProcessedAsync(id));
    }
}
