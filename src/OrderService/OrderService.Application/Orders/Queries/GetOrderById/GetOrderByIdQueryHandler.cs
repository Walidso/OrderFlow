using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Application.Abstractions;
using OrderService.Application.Common.Exceptions;
using OrderService.Application.Orders.Dtos;

namespace OrderService.Application.Orders.Queries.GetOrderById;

public sealed class GetOrderByIdQueryHandler : IRequestHandler<GetOrderByIdQuery, OrderDto>
{
    private readonly IApplicationDbContext _db;

    public GetOrderByIdQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<OrderDto> Handle(GetOrderByIdQuery request, CancellationToken cancellationToken)
    {
        var order = await _db.Orders
            // Include = eager loading. EF issues a JOIN so Items arrive with
            // the order in ONE round trip. Without it, Items would be empty.
            // Python bridge: like selectinload/joinedload in SQLAlchemy.
            .Include(o => o.Items)
            // AsNoTracking = "read-only, don't watch these objects for
            // changes". Faster and less memory for queries — a small but
            // classic CQRS win on the read side.
            .AsNoTracking()
            .FirstOrDefaultAsync(
                o => o.Id == request.OrderId && o.UserId == request.UserId,
                cancellationToken);

        // SECURITY: an order that exists but belongs to someone else also
        // returns 404 (not 403). Returning 403 would confirm the ID exists —
        // a small information leak.
        if (order is null)
            throw new NotFoundException($"Order '{request.OrderId}' was not found.");

        return new OrderDto(
            order.Id,
            order.Status.ToString(),
            order.Total,
            order.CreatedAtUtc,
            order.RejectionReason,
            order.Items
                .Select(i => new OrderItemDto(i.ProductId, i.ProductName, i.Quantity, i.UnitPrice))
                .ToList());
    }
}
