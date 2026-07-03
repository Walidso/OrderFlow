namespace OrderService.Domain.Entities;

/// <summary>
/// The Order aggregate root — the heart of this service's business rules.
///
/// "Aggregate root" (a Domain-Driven Design term) simply means: the outside
/// world talks to Order, and Order manages its own OrderItems. Nobody adds
/// an OrderItem directly to the database; they go through this class.
/// </summary>
public class Order
{
    // Backing field for the items collection. We expose it as
    // IReadOnlyCollection so callers can enumerate but never Add/Remove.
    // EF Core is smart enough to find "_items" by convention and use it.
    private readonly List<OrderItem> _items = new();

    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; }
    public string? RejectionReason { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    /// <summary>
    /// Computed, not stored. Storing a total AND the items would create two
    /// sources of truth that can drift apart — a classic interview talking
    /// point. EF ignores this property because it has no setter.
    /// Python bridge: like an @property that sums a list comprehension.
    /// </summary>
    public decimal Total => _items.Sum(i => i.UnitPrice * i.Quantity);

    private Order() { } // for EF Core only

    public static Order Create(Guid userId, IEnumerable<OrderItem> items)
    {
        var itemList = items.ToList();
        if (itemList.Count == 0)
            throw new ArgumentException("An order must contain at least one item.", nameof(items));

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = OrderStatus.Pending,       // every order starts Pending
            CreatedAtUtc = DateTime.UtcNow      // ALWAYS UtcNow — Postgres
                                                // 'timestamptz' requires UTC,
                                                // and servers in different
                                                // timezones must agree.
        };
        order._items.AddRange(itemList);
        return order;
    }

    /// <summary>Called when the Inventory Service says stock was reserved.</summary>
    public void MarkConfirmed()
    {
        // Idempotency guard: messaging systems deliver "at least once", so
        // the same event can arrive twice. Handling a duplicate must be a
        // no-op, not a crash. (Same reason you completed messages manually
        // in Azure Service Bus — to control exactly-once *processing*.)
        if (Status != OrderStatus.Pending) return;
        Status = OrderStatus.Confirmed;
    }

    /// <summary>Called when the Inventory Service rejected the reservation.</summary>
    public void MarkRejected(string reason)
    {
        if (Status != OrderStatus.Pending) return;
        Status = OrderStatus.Rejected;
        RejectionReason = reason;
    }
}
