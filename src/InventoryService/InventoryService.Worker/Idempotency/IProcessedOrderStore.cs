namespace InventoryService.Worker.Idempotency;

/// <summary>
/// Tracks which orders have already had a reservation OUTCOME decided
/// (reserved or rejected), so a redelivered OrderCreated can't double-spend
/// stock.
///
/// WHY dedupe by OrderId and not MassTransit's transport MessageId: our
/// Order Service publishes through a transactional outbox
/// (OrderService.Infrastructure/Outbox). If the outbox relay crashes after a
/// successful publish but before marking its row processed, it republishes
/// on the next poll — as a BRAND NEW message with a new MessageId. Transport
/// dedupe would miss that entirely; OrderId is the actual business identity
/// of "the thing that must only happen once."
///
/// EfProcessedOrderStore is the real, persisted implementation (see README
/// "Persistent inventory" — a restart no longer forgets what's already been
/// decided). InMemoryProcessedOrderStore remains as a fast test double.
/// </summary>
public interface IProcessedOrderStore
{
    Task<bool> HasBeenProcessedAsync(Guid orderId, CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(Guid orderId, CancellationToken cancellationToken = default);
}
