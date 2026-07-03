namespace OrderFlow.Contracts;

// ============================================================================
// SHARED MESSAGE CONTRACTS
// ============================================================================
// These records are the "language" the two microservices speak over RabbitMQ.
// Both services reference THIS project, so the message shape can never drift
// apart between publisher and consumer.
//
// WHY records? A C# `record` is an immutable data holder with value equality
// and a one-line declaration. Python bridge: think of it as a frozen
// @dataclass. Messages should be immutable — once published, an event is a
// fact that happened; nobody should mutate it.
//
// NAMING: events are named in PAST TENSE ("OrderCreated", not "CreateOrder")
// because they describe something that already happened. Commands would be
// imperative; events are history.
// ============================================================================

/// <summary>One product line inside an order — just what Inventory needs.</summary>
public record OrderLine(string ProductId, int Quantity);

/// <summary>
/// Published by the Order Service right after an order is saved as Pending.
/// The Inventory Service subscribes to this and tries to reserve stock.
/// Note we only ship the data the consumer needs (no prices, no user email) —
/// keeping events slim reduces coupling between services.
/// </summary>
public record OrderCreated(
    Guid OrderId,
    Guid UserId,
    IReadOnlyList<OrderLine> Lines,
    DateTime CreatedAtUtc);

/// <summary>Published by Inventory when every line could be reserved.</summary>
public record StockReserved(Guid OrderId);

/// <summary>Published by Inventory when reservation failed (e.g. out of stock).</summary>
public record StockRejected(Guid OrderId, string Reason);
