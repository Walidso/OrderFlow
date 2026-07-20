using InventoryService.Worker.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Worker.Idempotency;

/// <summary>
/// The real, persisted dedupe guard — a row in the same Postgres database
/// as stock. Unlike InMemoryProcessedOrderStore, a restart doesn't forget
/// which orders were already decided.
/// </summary>
public sealed class EfProcessedOrderStore : IProcessedOrderStore
{
    private readonly InventoryDbContext _db;

    public EfProcessedOrderStore(InventoryDbContext db) => _db = db;

    public Task<bool> HasBeenProcessedAsync(Guid orderId, CancellationToken cancellationToken = default)
        => _db.ProcessedOrders.AsNoTracking().AnyAsync(p => p.OrderId == orderId, cancellationToken);

    public async Task MarkProcessedAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        _db.ProcessedOrders.Add(ProcessedOrder.Create(orderId, DateTime.UtcNow));
        await _db.SaveChangesAsync(cancellationToken);
    }
}
