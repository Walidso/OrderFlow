using OrderFlow.Contracts;

namespace InventoryService.Worker.Stock;

/// <summary>
/// The original in-memory stock store. No longer wired into Program.cs —
/// production now uses EfStockStore against a real Postgres database — but
/// kept as a fast, DB-less test double for consumer tests that don't care
/// about persistence, only about "did TryReserve get called correctly".
/// </summary>
public sealed class InMemoryStockStore : IStockStore
{
    // Seeded demo inventory for the fruit store. 🍎
    private readonly Dictionary<string, int> _stock = new()
    {
        ["APPLE-1"] = 100,   // plenty in stock — happy path
        ["BANANA-1"] = 50,
        ["MANGO-1"] = 3,     // scarce — order 4+ to see StockRejected
        ["DURIAN-1"] = 999   // the consumer throws on purpose to
                             // demonstrate retry + the _error queue
    };

    // The bus can hand us several messages concurrently, and Dictionary is
    // not thread-safe for writes. A plain lock (Python bridge:
    // `with threading.Lock():`) keeps check-then-decrement atomic —
    // ConcurrentDictionary alone would NOT be enough, because we must check
    // ALL lines before decrementing ANY of them. (EfStockStore replaces this
    // in-process lock with a database-enforced atomic update — see its
    // comments for why that matters once there's more than one replica.)
    private readonly object _gate = new();

    public Task<StockReservationResult> TryReserveAsync(
        IReadOnlyList<OrderLine> lines, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Pass 1: verify everything is available.
            foreach (var line in lines)
            {
                if (!_stock.TryGetValue(line.ProductId, out var available))
                    return Task.FromResult(StockReservationResult.Rejected($"Unknown product '{line.ProductId}'."));

                if (available < line.Quantity)
                    return Task.FromResult(StockReservationResult.Rejected(
                        $"Insufficient stock for '{line.ProductId}': " +
                        $"requested {line.Quantity}, available {available}."));
            }

            // Pass 2: only now mutate.
            foreach (var line in lines)
                _stock[line.ProductId] -= line.Quantity;

            return Task.FromResult(StockReservationResult.Reserved());
        }
    }

    public Task<IReadOnlyDictionary<string, int>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>(_stock));
        }
    }
}
