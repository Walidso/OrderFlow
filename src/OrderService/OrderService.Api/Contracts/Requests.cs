namespace OrderService.Api.Contracts;

// ============================================================================
// API request contracts — the shapes clients POST to us.
// Kept separate from Application commands on purpose: the wire format can
// evolve (rename a JSON field, add versioning quirks) without touching the
// Application layer, and vice versa.
// ============================================================================

public record RegisterRequest(string Email, string Password);

public record LoginRequest(string Email, string Password);

public record CreateOrderItemRequest(
    string ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

public record CreateOrderRequest(List<CreateOrderItemRequest> Items);
