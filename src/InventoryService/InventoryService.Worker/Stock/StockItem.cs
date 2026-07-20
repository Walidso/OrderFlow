namespace InventoryService.Worker.Stock;

/// <summary>
/// One product's stock row. Replaces the in-memory Dictionary&lt;string,int&gt;
/// that used to be this service's entire "database" — see EfStockStore for
/// how ALL-OR-NOTHING reservation is enforced at the database level instead
/// of with an in-process lock.
/// </summary>
public class StockItem
{
    public string ProductId { get; private set; } = default!;
    public int AvailableQuantity { get; private set; }

    private StockItem() { } // for EF Core only

    public static StockItem Create(string productId, int availableQuantity) => new()
    {
        ProductId = productId,
        AvailableQuantity = availableQuantity
    };
}
