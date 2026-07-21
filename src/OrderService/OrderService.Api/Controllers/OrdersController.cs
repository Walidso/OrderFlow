using System.Security.Claims;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OrderService.Api.Contracts;
using OrderService.Application.Orders.Commands.CreateOrder;
using OrderService.Application.Orders.Dtos;
using OrderService.Application.Orders.Queries.GetMyOrders;
using OrderService.Application.Orders.Queries.GetOrderById;

namespace OrderService.Api.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/orders")]
[Authorize] // <-- the protected part: no valid JWT, no entry (401)
public sealed class OrdersController : ControllerBase
{
    private readonly ISender _sender;

    public OrdersController(ISender sender) => _sender = sender;

    /// <summary>
    /// Reads the user id out of the validated JWT. The JWT middleware has
    /// already checked the signature/expiry by the time we get here, and it
    /// maps the token's "sub" claim to ClaimTypes.NameIdentifier.
    /// This is why clients never send a userId in the body — the token IS
    /// the identity.
    /// </summary>
    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("JWT is missing the 'sub' claim."));

    /// <summary>Create an order. Saved as Pending, then Inventory decides its fate.</summary>
    [HttpPost]
    [EnableRateLimiting("orders")] // see Program.cs — partitioned per user, not per IP
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create(
        CreateOrderRequest request, CancellationToken cancellationToken)
    {
        var items = request.Items
            .Select(i => new OrderItemInput(i.ProductId, i.ProductName, i.Quantity, i.UnitPrice))
            .ToList();

        var orderId = await _sender.Send(
            new CreateOrderCommand(CurrentUserId, items), cancellationToken);

        // 201 Created + Location header pointing at the new resource —
        // the REST-correct answer to a successful POST.
        return Created($"/api/v1/orders/{orderId}", new { id = orderId });
    }

    /// <summary>Get one of MY orders (poll this to watch Pending -> Confirmed/Rejected).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDto>> GetById(Guid id, CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetOrderByIdQuery(id, CurrentUserId), cancellationToken));

    /// <summary>List MY orders, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<OrderSummaryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrderSummaryDto>>> GetMyOrders(
        CancellationToken cancellationToken)
        => Ok(await _sender.Send(new GetMyOrdersQuery(CurrentUserId), cancellationToken));
}
