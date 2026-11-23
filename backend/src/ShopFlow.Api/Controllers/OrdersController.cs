using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopFlow.Application.Common;
using ShopFlow.Application.Orders;
using ShopFlow.Domain.Entities;

namespace ShopFlow.Api.Controllers;

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}/orders"), Authorize]
public class OrdersController(OrderService orders) : ControllerBase
{
    /// <summary>
    /// POST is not idempotent by nature, so the client sends an Idempotency-Key (a UUID made when the checkout page opens).
    /// Double click, flaky mobile network, automatic retry => same key => ONE order. A replay returns the original response.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Place(PlaceOrderRequest req, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct)
    {
        var placed = await orders.PlaceAsync(req, key, ct);
        Response.Headers["Idempotent-Replayed"] = placed.Replayed ? "true" : "false";
        return placed.Replayed ? Ok(placed.Order) : CreatedAtAction(nameof(Get), new { id = placed.Order.Id, version = "1" }, placed.Order);
    }

    [HttpGet("{id:guid}")]
    public Task<OrderDto> Get(Guid id, CancellationToken ct) => orders.GetAsync(id, ct);

    [HttpGet]
    public Task<CursorPage<OrderDto>> List([FromQuery] string? cursor, [FromQuery] int limit = 20, [FromQuery] string scope = "mine", CancellationToken ct = default)
        => orders.ListAsync(cursor, limit, scope == "all", ct);

    [HttpPost("{id:guid}/payment-intents")]
    public async Task<IActionResult> Pay(Guid id, CancellationToken ct)
    {
        var intent = await orders.StartPaymentAsync(id, ct);
        return Ok(new { reference = intent.Reference, provider = intent.Provider });
    }

    [HttpPost("{id:guid}/cancel")]
    public Task<OrderDto> Cancel(Guid id, CancellationToken ct) => orders.CancelAsync(id, ct);

    [HttpPost("{id:guid}/ship"), Authorize(Policy = "Admin")]
    public Task<OrderDto> Ship(Guid id, CancellationToken ct) => orders.AdvanceAsync(id, OrderStatus.Shipped, ct);

    [HttpPost("{id:guid}/deliver"), Authorize(Policy = "Admin")]
    public Task<OrderDto> Deliver(Guid id, CancellationToken ct) => orders.AdvanceAsync(id, OrderStatus.Delivered, ct);
}
