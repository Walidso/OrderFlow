namespace OrderService.Domain.Entities;

/// <summary>
/// One line of an order (e.g. "3 x Watermelon at 25.00").
///
/// Notice the pattern used across this Domain layer:
///  - `private set` on every property  -> nobody outside can mutate state
///  - a private parameterless constructor -> only EF Core uses it (via
///    reflection) when it loads rows from the database
///  - a static factory method `Create(...)` -> the ONLY valid way for our
///    own code to build the object, so invariants are checked in one place.
///
/// Python bridge: this is like making all attributes "private" and exposing
/// a classmethod constructor, instead of letting callers poke obj.quantity.
/// </summary>
public class OrderItem
{
    public Guid Id { get; private set; }

    /// <summary>Foreign key to the parent order. EF fills this in for us.</summary>
    public Guid OrderId { get; private set; }

    public string ProductId { get; private set; } = string.Empty;
    public string ProductName { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }

    // EF Core needs a parameterless constructor to materialize entities.
    // Making it private keeps it out of reach for everyone else.
    private OrderItem() { }

    public static OrderItem Create(string productId, string productName, int quantity, decimal unitPrice)
    {
        // Defensive checks. FluentValidation already validates user input at
        // the Application boundary, but the Domain protects itself anyway —
        // "never trust your callers" is a great line to say in an interview.
        if (string.IsNullOrWhiteSpace(productId))
            throw new ArgumentException("ProductId is required.", nameof(productId));
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        if (unitPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "Unit price cannot be negative.");

        return new OrderItem
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ProductName = productName,
            Quantity = quantity,
            UnitPrice = unitPrice
        };
    }
}
