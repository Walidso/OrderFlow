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
///
/// ===================== DEADLOCK PREVENTION (lines sorted) ==================
/// Two orders reserving the SAME products in different sequences — Order A:
/// APPLE-1 then MANGO-1; Order B: MANGO-1 then APPLE-1, running concurrently
/// — is the textbook circular-wait deadlock: A holds APPLE-1's row lock and
/// waits for MANGO-1 (held by B), while B holds MANGO-1's lock and waits for
/// APPLE-1 (held by A). Sorting every reservation's lines into the SAME
/// canonical order (by ProductId) before acquiring any locks makes that
/// cycle impossible — every caller always asks for row locks in the same
/// order, so there's nothing left to wait on circularly.
///
/// ================== AMBIENT TRANSACTION PARTICIPATION ======================
/// This method can run two ways: standalone (owns and commits/rolls back its
/// own transaction — what every existing test does), or nested inside a
/// transaction the CALLER already opened (what StockReservationCoordinator
/// does, so the stock update, the idempotency marker, and the outbox row
/// enqueueing the outbound event all commit — or roll back — together).
/// </summary>
public sealed class EfStockStore : IStockStore
{
    private readonly InventoryDbContext _db;

    public EfStockStore(InventoryDbContext db) => _db = db;

    public async Task<StockReservationResult> TryReserveAsync(
        IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default)
    {
        var orderedLines = lines.OrderBy(l => l.ProductId).ToList();

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            foreach (var line in orderedLines)
            {
                var rowsAffected = await _db.StockItems
                    .Where(s => s.ProductId == line.ProductId && s.AvailableQuantity >= line.Quantity)
                    .ExecuteUpdateAsync(
                        setters => setters.SetProperty(
                            s => s.AvailableQuantity, s => s.AvailableQuantity - line.Quantity),
                        cancellationToken);

                if (rowsAffected == 0)
                {
                    // Only roll back if we own the transaction. If we're
                    // nested inside the caller's, the caller decides what
                    // to do with a failed reservation (e.g. still commit
                    // the outbox row that records the rejection).
                    if (ownsTransaction) await transaction!.RollbackAsync(cancellationToken);
                    return await BuildRejectionAsync(line, cancellationToken);
                }
            }

            if (ownsTransaction) await transaction!.CommitAsync(cancellationToken);
            return StockReservationResult.Reserved();
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
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
