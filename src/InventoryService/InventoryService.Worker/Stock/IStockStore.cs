using OrderFlow.Contracts;

namespace InventoryService.Worker.Stock;

public sealed record StockReservationResult(bool Success, string FailureReason)
{
    public static StockReservationResult Reserved() => new(true, string.Empty);
    public static StockReservationResult Rejected(string reason) => new(false, reason);
}

/// <summary>
/// Where stock lives. Now a real, persisted store (EfStockStore) instead of
/// the in-memory dictionary this used to be — see README "Persistent
/// inventory" for why, and EfStockStore for how ALL-OR-NOTHING reservation
/// stays safe across multiple replicas of this service.
/// </summary>
public interface IStockStore
{
    /// <summary>
    /// Try to reserve every line ALL-OR-NOTHING. Partial reservations would
    /// leave stock in limbo when the rest of the order fails.
    /// </summary>
    Task<StockReservationResult> TryReserveAsync(
        IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default);

    /// <summary>Current stock levels — exposed on GET /stock for demos.</summary>
    Task<IReadOnlyDictionary<string, int>> SnapshotAsync(CancellationToken cancellationToken = default);
}
