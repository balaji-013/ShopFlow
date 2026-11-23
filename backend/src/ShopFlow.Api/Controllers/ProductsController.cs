using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopFlow.Application.Catalog;
using ShopFlow.Application.Common;

namespace ShopFlow.Api.Controllers;

[ApiController, ApiVersion(1), Route("api/v{version:apiVersion}"), Authorize]
public class ProductsController(ProductService products) : ControllerBase
{
    /// <summary>GET /products?search=lap&amp;categoryId=..&amp;cursor=..&amp;limit=20 - filtering, cursor pagination, capped page size.</summary>
    [HttpGet("products")]
    public Task<CursorPage<ProductDto>> List([FromQuery] ProductQuery q, CancellationToken ct) => products.ListAsync(q, ct);

    [HttpGet("categories")]
    public Task<IReadOnlyList<CategoryDto>> Categories(CancellationToken ct) => products.CategoriesAsync(ct);

    [HttpGet("products/{id:guid}")]
    public Task<ProductDto> Get(Guid id, CancellationToken ct) => products.GetAsync(id, ct);

    // REST semantics: POST creates (201 + Location), PUT replaces (idempotent), PATCH changes part, DELETE removes.
    [HttpPost("products"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ProductDto>> Create(CreateProductRequest req, CancellationToken ct)
    {
        var p = await products.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = p.Id, version = "1" }, p);
    }

    [HttpPut("products/{id:guid}"), Authorize(Policy = "Admin")]
    public Task<ProductDto> Update(Guid id, UpdateProductRequest req, CancellationToken ct) => products.UpdateAsync(id, req, ct);

    [HttpPost("products/{id:guid}/stock-adjustments"), Authorize(Policy = "Admin")]
    public Task<ProductDto> AdjustStock(Guid id, AdjustStockRequest req, CancellationToken ct) => products.AdjustStockAsync(id, req, ct);
}
