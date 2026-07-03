using OrderFlow.Contracts;

namespace InventoryService.Worker.Stock;

/// <summary>
/// Where stock lives. In-memory for this demo (an honest, documented scope
/// decision — see README "future improvements": a real service would use
/// its own database. Microservices rule: services NEVER share a database,
/// which is why Inventory doesn't just read Order Service's Postgres.)
/// </summary>
public interface IStockStore
{
    /// <summary>
    /// Try to reserve every line ALL-OR-NOTHING. Partial reservations would
    /// leave stock in limbo when the rest of the order fails.
    /// </summary>
    bool TryReserve(IReadOnlyList<OrderLine> lines, out string failureReason);

    /// <summary>Current stock levels — exposed on GET /stock for demos.</summary>
    IReadOnlyDictionary<string, int> Snapshot();
}
