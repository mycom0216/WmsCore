using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WmsCore.Api.Common;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;

namespace WmsCore.Api.Controllers;

public record ProductCreate([Required, StringLength(40)] string Sku, [Required, StringLength(200)] string Name);
public record LocationCreate([Required, StringLength(40)] string Code);
public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int Size, int Total);

[ApiController]
[Authorize]
[Route("api/products")]
public class ProductsController(WmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<PagedResult<Product>> List([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int size = 20, CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        size = Math.Clamp(size, 1, 100); // 한 번에 가져올 수 있는 개수를 제한
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => p.Sku.Contains(q) || p.Name.Contains(q));
        var total = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new(items, page, size, total);
    }

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<Product>> Create(ProductCreate req, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(p => p.Sku == req.Sku, ct))
            throw DomainException.Conflict("중복 SKU", $"SKU {req.Sku}는 이미 있습니다.");
        var p = new Product { Sku = req.Sku, Name = req.Name };
        db.Products.Add(p);
        await db.SaveChangesAsync(ct);
        return Created($"/api/products/{p.Id}", p);
    }
}

[ApiController]
[Authorize]
[Route("api/locations")]
public class LocationsController(WmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<List<Location>> List(CancellationToken ct) => await db.Locations.AsNoTracking().OrderBy(l => l.Code).ToListAsync(ct);

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<ActionResult<Location>> Create(LocationCreate req, CancellationToken ct)
    {
        if (await db.Locations.AnyAsync(l => l.Code == req.Code, ct))
            throw DomainException.Conflict("중복 위치", $"위치 {req.Code}는 이미 있습니다.");
        var l = new Location { Code = req.Code };
        db.Locations.Add(l);
        await db.SaveChangesAsync(ct);
        return Created($"/api/locations/{l.Id}", l);
    }
}
