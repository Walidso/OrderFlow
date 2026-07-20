using InventoryService.Worker.Idempotency;
using Xunit;

namespace InventoryService.UnitTests;

public class InMemoryProcessedOrderStoreTests
{
    [Fact]
    public void HasBeenProcessed_UnknownOrder_ReturnsFalse()
    {
        var store = new InMemoryProcessedOrderStore();

        Assert.False(store.HasBeenProcessed(Guid.NewGuid()));
    }

    [Fact]
    public void HasBeenProcessed_AfterMarkProcessed_ReturnsTrue()
    {
        var store = new InMemoryProcessedOrderStore();
        var orderId = Guid.NewGuid();

        store.MarkProcessed(orderId);

        Assert.True(store.HasBeenProcessed(orderId));
    }

    [Fact]
    public void HasBeenProcessed_DoesNotConfuseDifferentOrders()
    {
        var store = new InMemoryProcessedOrderStore();
        store.MarkProcessed(Guid.NewGuid());

        Assert.False(store.HasBeenProcessed(Guid.NewGuid()));
    }

    [Fact]
    public void MarkProcessed_ConcurrentCallsForDifferentOrders_AllRecorded()
    {
        // Mirrors why InMemoryStockStore takes a lock: the bus can hand this
        // singleton messages for many orders concurrently.
        var store = new InMemoryProcessedOrderStore();
        var orderIds = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToList();

        Parallel.ForEach(orderIds, store.MarkProcessed);

        Assert.All(orderIds, id => Assert.True(store.HasBeenProcessed(id)));
    }
}
