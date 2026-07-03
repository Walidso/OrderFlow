using MediatR;
using OrderService.Application.Orders.Dtos;

namespace OrderService.Application.Orders.Commands.CreateOrder;

/// <summary>
/// CQRS "Command" = an intent to CHANGE state ("please create an order").
/// It implements IRequest&lt;Guid&gt;, meaning: when handled, it returns the
/// new order's Id.
///
/// Note: UserId is NOT supplied by the client body — the controller extracts
/// it from the validated JWT. Never trust a client to say who they are.
/// </summary>
public record CreateOrderCommand(
    Guid UserId,
    IReadOnlyList<OrderItemInput> Items) : IRequest<Guid>;
