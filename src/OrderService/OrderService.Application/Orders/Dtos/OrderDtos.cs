namespace OrderService.Application.Orders.Dtos;

// ============================================================================
// DTOs = Data Transfer Objects: the shapes we send OUT of the API.
// We never return Domain entities directly, because:
//   1. Entities may expose internals we don't want on the wire.
//   2. Changing an entity would silently change the public API contract.
//   3. Serializers + EF lazy navigation properties = accidental data leaks.
// ============================================================================

/// <summary>Full order details, returned by GET /orders/{id}.</summary>
public record OrderDto(
    Guid Id,
    string Status,
    decimal Total,
    DateTime CreatedAtUtc,
    string? RejectionReason,
    IReadOnlyList<OrderItemDto> Items);

public record OrderItemDto(
    string ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);

/// <summary>Slim shape for lists — no items, cheaper to query and to read.</summary>
public record OrderSummaryDto(
    Guid Id,
    string Status,
    decimal Total,
    DateTime CreatedAtUtc);

/// <summary>Input shape for one order line in CreateOrderCommand.</summary>
public record OrderItemInput(
    string ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice);
