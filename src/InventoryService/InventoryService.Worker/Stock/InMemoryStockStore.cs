using OrderFlow.Contracts;

namespace InventoryService.Worker.Stock;

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
    // ALL lines before decrementing ANY of them.
    private readonly object _gate = new();

    public bool TryReserve(IReadOnlyList<OrderLine> lines, out string failureReason)
    {
        lock (_gate)
        {
            // Pass 1: verify everything is available.
            foreach (var line in lines)
            {
                if (!_stock.TryGetValue(line.ProductId, out var available))
                {
                    failureReason = $"Unknown product '{line.ProductId}'.";
                    return false;
                }
                if (available < line.Quantity)
                {
                    failureReason =
                        $"Insufficient stock for '{line.ProductId}': " +
                        $"requested {line.Quantity}, available {available}.";
                    return false;
                }
            }

            // Pass 2: only now mutate.
            foreach (var line in lines)
                _stock[line.ProductId] -= line.Quantity;

            failureReason = string.Empty;
            return true;
        }
    }

    public IReadOnlyDictionary<string, int> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, int>(_stock);
        }
    }
}
