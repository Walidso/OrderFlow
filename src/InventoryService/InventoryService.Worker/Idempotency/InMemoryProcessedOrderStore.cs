namespace InventoryService.Worker.Idempotency;

/// <summary>
/// No longer wired into Program.cs — production now uses
/// EfProcessedOrderStore against the same Postgres database as stock, so
/// the guard survives a restart. Kept as a fast, DB-less test double.
/// </summary>
public sealed class InMemoryProcessedOrderStore : IProcessedOrderStore
{
    private readonly HashSet<Guid> _processedOrderIds = new();

    // Same reasoning as InMemoryStockStore's _gate: the bus can hand
    // messages to this singleton concurrently, and HashSet isn't
    // thread-safe for concurrent writes.
    private readonly object _gate = new();

    public Task<bool> HasBeenProcessedAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_processedOrderIds.Contains(orderId));
        }
    }

    public Task MarkProcessedAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _processedOrderIds.Add(orderId);
        }

        return Task.CompletedTask;
    }
}
