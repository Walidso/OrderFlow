using InventoryService.Worker.Persistence;
using Microsoft.EntityFrameworkCore;
using OrderFlow.Contracts;

namespace InventoryService.Worker.Stock;

/// <summary>
/// The real, persisted stock store. Replaces InMemoryStockStore's in-process
/// `lock` — which only ever protected a single instance's memory — with a
/// database-enforced guarantee that works correctly even if this service
/// runs as multiple replicas.
///
/// ==================== HOW THE RACE IS ACTUALLY PREVENTED ===================
/// Each line is reserved with ONE conditional UPDATE:
///
///     UPDATE "StockItems"
///     SET "AvailableQuantity" = "AvailableQuantity" - @qty
///     WHERE "ProductId" = @id AND "AvailableQuantity" >= @qty
///
/// There's no separate "read, then decide, then write" — the check and the
/// write are the SAME statement, so there's no window for another
/// transaction to interleave. If two requests race for the last 3 units of
/// MANGO-1, Postgres's row-level locking serializes their UPDATEs: whichever
/// commits first wins, and the second one re-evaluates its WHERE clause
/// against the now-lower quantity and (correctly) affects zero rows.
///
/// Wrapping every line's UPDATE in one transaction is what makes the whole
/// reservation ALL-OR-NOTHING: the moment any line's UPDATE affects zero
/// rows, we roll back — undoing any earlier lines this same attempt already
/// decremented — instead of leaving a half-reserved order.
/// ============================================================================
/// </summary>
public sealed class EfStockStore : IStockStore
{
    private readonly InventoryDbContext _db;

    public EfStockStore(InventoryDbContext db) => _db = db;

    public async Task<StockReservationResult> TryReserveAsync(
        IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var line in lines)
        {
            var rowsAffected = await _db.StockItems
                .Where(s => s.ProductId == line.ProductId && s.AvailableQuantity >= line.Quantity)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        s => s.AvailableQuantity, s => s.AvailableQuantity - line.Quantity),
                    cancellationToken);

            if (rowsAffected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return await BuildRejectionAsync(line, cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return StockReservationResult.Reserved();
    }

    private async Task<StockReservationResult> BuildRejectionAsync(
        OrderLine line, CancellationToken cancellationToken)
    {
        var current = await _db.StockItems
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProductId == line.ProductId, cancellationToken);

        return current is null
            ? StockReservationResult.Rejected($"Unknown product '{line.ProductId}'.")
            : StockReservationResult.Rejected(
                $"Insufficient stock for '{line.ProductId}': " +
                $"requested {line.Quantity}, available {current.AvailableQuantity}.");
    }

    public async Task<IReadOnlyDictionary<string, int>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var items = await _db.StockItems.AsNoTracking().ToListAsync(cancellationToken);
        return items.ToDictionary(s => s.ProductId, s => s.AvailableQuantity);
    }
}
