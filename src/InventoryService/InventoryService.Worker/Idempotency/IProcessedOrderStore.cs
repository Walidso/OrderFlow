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
/// In-memory here for the same honest reason InMemoryStockStore is: this
/// demo's Inventory Service owns no database. A real deployment would use a
/// persisted "inbox" table (or the stock reservation table itself, keyed on
/// OrderId) so this survives a restart.
/// </summary>
public interface IProcessedOrderStore
{
    bool HasBeenProcessed(Guid orderId);

    void MarkProcessed(Guid orderId);
}
