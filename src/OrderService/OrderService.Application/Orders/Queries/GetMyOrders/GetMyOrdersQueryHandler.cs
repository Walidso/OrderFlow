using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Application.Abstractions;
using OrderService.Application.Orders.Dtos;

namespace OrderService.Application.Orders.Queries.GetMyOrders;

public sealed class GetMyOrdersQueryHandler
    : IRequestHandler<GetMyOrdersQuery, IReadOnlyList<OrderSummaryDto>>
{
    private readonly IApplicationDbContext _db;

    public GetMyOrdersQueryHandler(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<OrderSummaryDto>> Handle(
        GetMyOrdersQuery request, CancellationToken cancellationToken)
    {
        // Deliberately NO Include(o => o.Items): the summary doesn't need
        // items, so we don't pay for the JOIN. CQRS lets each read model be
        // exactly as big as its screen needs.
        return await _db.Orders
            .AsNoTracking()
            .Where(o => o.UserId == request.UserId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .Select(o => new OrderSummaryDto(
                o.Id,
                o.Status.ToString(),
                o.Items.Sum(i => i.UnitPrice * i.Quantity), // translated to SQL SUM
                o.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        // LINQ note (your Module 1 in action!): Where/OrderBy/Select here are
        // DEFERRED — nothing hits the database until ToListAsync runs, and
        // EF translates the whole chain into one SQL statement.
    }
}
