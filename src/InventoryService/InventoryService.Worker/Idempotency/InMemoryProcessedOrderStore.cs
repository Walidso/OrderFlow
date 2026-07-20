namespace InventoryService.Worker.Idempotency;

public sealed class InMemoryProcessedOrderStore : IProcessedOrderStore
{
    private readonly HashSet<Guid> _processedOrderIds = new();

    // Same reasoning as InMemoryStockStore's _gate: the bus can hand
    // messages to this singleton concurrently, and HashSet isn't
    // thread-safe for concurrent writes.
    private readonly object _gate = new();

    public bool HasBeenProcessed(Guid orderId)
    {
        lock (_gate)
        {
            return _processedOrderIds.Contains(orderId);
        }
    }

    public void MarkProcessed(Guid orderId)
    {
        lock (_gate)
        {
            _processedOrderIds.Add(orderId);
        }
    }
}
