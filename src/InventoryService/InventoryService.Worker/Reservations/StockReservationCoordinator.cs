using System.Text.Json;
using InventoryService.Worker.Idempotency;
using InventoryService.Worker.Outbox;
using InventoryService.Worker.Persistence;
using InventoryService.Worker.Stock;
using OrderFlow.Contracts;

namespace InventoryService.Worker.Reservations;

/// <summary>
/// Orchestrates the three writes a single OrderCreated must produce as ONE
/// atomic unit: the stock change, the idempotency marker, and the outbound
/// event (StockReserved/StockRejected) queued to the outbox.
///
/// ======================= WHY THIS CLASS EXISTS AT ALL =======================
/// The original design had OrderCreatedConsumer do these as three SEPARATE
/// steps: reserve stock (its own commit), publish the outcome directly via
/// MassTransit, THEN mark the order processed (a second, separate commit).
/// That left a real gap: crash between "stock reserved" and "marked
/// processed", and a redelivery would see the order as NOT processed yet
/// and reserve the SAME stock again.
///
/// The tempting quick fix — just move the "mark processed" write into the
/// SAME transaction as the stock update — only moves the gap, it doesn't
/// close it: crash between that commit and the (still separate) publish
/// call, and the order is now permanently stuck Pending, because a
/// redelivery sees "already processed" and skips straight past ever
/// publishing the outcome. Worse than the original bug, since at least that
/// one eventually reached a final order status.
///
/// The actual fix is the same idea as OrderService's outbox: don't publish
/// directly at all. Queue the outcome event in the SAME transaction as the
/// stock change and the idempotency marker, and let a separate background
/// dispatcher (OutboxDispatcherBackgroundService) relay it afterwards. Then
/// there is no window where any two of "stock changed", "marked processed",
/// and "the outcome will eventually be published" disagree with each other.
/// ============================================================================
///
/// One nuance a REJECTED reservation needs: EfStockStore may have already
/// applied (uncommitted) UPDATEs for earlier lines before the line that
/// failed. Those must NOT survive — so on rejection we roll back that
/// attempt's transaction entirely, and record the rejection itself (marker
/// + StockRejected event, no stock involved) as a separate write afterwards.
/// </summary>
public interface IStockReservationCoordinator
{
    Task<StockReservationResult> ReserveAsync(
        Guid orderId, IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default);
}

public sealed class StockReservationCoordinator : IStockReservationCoordinator
{
    private readonly InventoryDbContext _db;
    private readonly IStockStore _stock;

    public StockReservationCoordinator(InventoryDbContext db, IStockStore stock)
    {
        _db = db;
        _stock = stock;
    }

    public async Task<StockReservationResult> ReserveAsync(
        Guid orderId, IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default)
    {
        StockReservationResult result;

        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            // EfStockStore detects this ambient transaction and participates
            // in it instead of opening (and committing) its own.
            result = await _stock.TryReserveAsync(lines, cancellationToken);

            if (result.Success)
            {
                _db.ProcessedOrders.Add(ProcessedOrder.Create(orderId, DateTime.UtcNow));
                Enqueue(new StockReserved(orderId));

                await _db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }

            // Whatever partial stock changes this attempt made before the
            // failing line must not survive — roll back before recording
            // anything at all.
            await transaction.RollbackAsync(cancellationToken);
        }

        // A fresh, separate write: just the marker and the rejection event,
        // no stock involved. SaveChangesAsync wraps both in its own implicit
        // transaction, so they still commit — or fail — together.
        _db.ProcessedOrders.Add(ProcessedOrder.Create(orderId, DateTime.UtcNow));
        Enqueue(new StockRejected(orderId, result.FailureReason));
        await _db.SaveChangesAsync(cancellationToken);

        return result;
    }

    private void Enqueue<TEvent>(TEvent @event) where TEvent : class
    {
        var type = typeof(TEvent).AssemblyQualifiedName
            ?? throw new InvalidOperationException($"'{typeof(TEvent)}' has no assembly-qualified name.");
        var content = JsonSerializer.Serialize(@event);

        _db.OutboxMessages.Add(OutboxMessage.Create(type, content, DateTime.UtcNow));
    }
}
