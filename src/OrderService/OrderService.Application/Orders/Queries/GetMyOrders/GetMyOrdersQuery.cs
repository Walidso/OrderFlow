using MediatR;
using OrderService.Application.Orders.Dtos;

namespace OrderService.Application.Orders.Queries.GetMyOrders;

public record GetMyOrdersQuery(Guid UserId) : IRequest<IReadOnlyList<OrderSummaryDto>>;
