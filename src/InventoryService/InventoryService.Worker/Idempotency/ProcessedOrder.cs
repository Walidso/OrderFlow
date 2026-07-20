namespace InventoryService.Worker.Idempotency;

/// <summary>
/// A persisted "we already decided this order's fate" marker. The row's
/// mere existence is the guard — see EfProcessedOrderStore.
/// </summary>
public class ProcessedOrder
{
    public Guid OrderId { get; private set; }
    public DateTime ProcessedOnUtc { get; private set; }

    private ProcessedOrder() { } // for EF Core only

    public static ProcessedOrder Create(Guid orderId, DateTime processedOnUtc) => new()
    {
        OrderId = orderId,
        ProcessedOnUtc = processedOnUtc
    };
}
