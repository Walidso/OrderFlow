namespace OrderService.Domain.Entities;

/// <summary>
/// The lifecycle of an order.
/// Pending   -> just created, waiting for the Inventory Service to answer.
/// Confirmed -> Inventory reserved the stock (StockReserved event received).
/// Rejected  -> Inventory could not reserve (StockRejected event received).
///
/// Python bridge: like an Enum from Python's `enum` module.
/// In the database we store this as TEXT ("Pending") instead of an int,
/// so anyone reading the table with plain SQL understands it instantly.
/// </summary>
public enum OrderStatus
{
    Pending,
    Confirmed,
    Rejected
}
