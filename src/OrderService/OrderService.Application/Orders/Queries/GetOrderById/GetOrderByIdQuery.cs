using MediatR;
using OrderService.Application.Orders.Dtos;

namespace OrderService.Application.Orders.Queries.GetOrderById;

/// <summary>
/// CQRS "Query" = read-only, never changes state.
/// UserId is included so users can only read THEIR OWN orders —
/// authorization at the data level, not just [Authorize] on the controller.
/// </summary>
public record GetOrderByIdQuery(Guid OrderId, Guid UserId) : IRequest<OrderDto>;
