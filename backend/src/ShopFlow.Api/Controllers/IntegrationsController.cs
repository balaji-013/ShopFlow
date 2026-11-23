using System.Text;
using System.Text.Json;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopFlow.Application.Abstractions;
using ShopFlow.Application.Orders;
using ShopFlow.Application.Payments;
using ShopFlow.Application.Reports;

namespace ShopFlow.Api.Controllers;

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}")]
public class IntegrationsController(PaymentWebhookService webhooks) : ControllerBase
{
    /// <summary>
    /// Called by the payment provider, not by a browser: no JWT, no CORS. Authenticity comes from the HMAC signature over the RAW body
    /// (re-serialising parsed JSON would change bytes and break the signature - so we read the stream ourselves).
    /// </summary>
    [HttpPost("webhooks/payments"), AllowAnonymous]
    public async Task<IActionResult> Payments(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        await webhooks.HandleAsync(body, Request.Headers["X-Signature"].FirstOrDefault(), ct);
        return Ok();      // 2xx = "got it, stop retrying". Duplicates are also 200.
    }
}

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}/reports"), Authorize(Policy = "Admin")]
public class ReportsController(ReportService reports) : ControllerBase
{
    [HttpGet("top-products")]
    public Task<List<RankedProductRow>> TopProducts([FromQuery] int top = 3, CancellationToken ct = default) => reports.TopProductsPerCategoryAsync(top, ct);
}

/// <summary>
/// DEV/DEMO ONLY (returns 404 unless Development or Dev:SimulatorEnabled=true - never enable in a real deployment). Plays the role of the payment provider: builds a
/// signed "payment.succeeded" event and feeds it to the SAME webhook handler. ?duplicate=true sends it twice to show idempotency.
/// </summary>
[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}/dev"), Authorize]
public class DevController(OrderService orders, PaymentWebhookService webhooks, IWebhookSignatureVerifier signer, IWebHostEnvironment env, IConfiguration config) : ControllerBase
{
    [HttpPost("orders/{id:guid}/simulate-payment")]
    public async Task<IActionResult> SimulatePayment(Guid id, [FromQuery] bool duplicate, CancellationToken ct)
    {
        if (!env.IsDevelopment() && !config.GetValue("Dev:SimulatorEnabled", false)) return NotFound();
        var order = await orders.GetAsync(id, ct);   // also proves the caller may see this order
        var body = JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type = "payment.succeeded", data = new { orderId = order.Id, reference = $"pi_sim_{Guid.NewGuid():N}" } });
        var signature = signer.Sign(body, DateTimeOffset.UtcNow);
        await webhooks.HandleAsync(body, signature, ct);
        if (duplicate) await webhooks.HandleAsync(body, signature, ct);
        return Accepted();
    }
}
