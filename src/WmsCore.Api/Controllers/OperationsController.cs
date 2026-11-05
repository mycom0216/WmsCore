using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WmsCore.Api.Common;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;
using WmsCore.Api.Services;

namespace WmsCore.Api.Controllers;

public record LineDto([Required, StringLength(40)] string Sku, [Required, StringLength(40)] string LocationCode, [Range(1, 1_000_000)] int Quantity);
public record InboundRequest([Required, StringLength(60)] string ReferenceNo, [Required, MinLength(1), MaxLength(200)] List<LineDto> Lines);
public record OrderRequest([Required, StringLength(60)] string OrderNo, [Required, MinLength(1), MaxLength(200)] List<LineDto> Lines);
public record OrderResponse(int Id, string OrderNo, string Status, DateTime CreatedAt, List<LineDto> Lines);

[ApiController]
[Authorize(Roles = Roles.Admin)]
[Route("api/inbound")]
public class InboundController(StockService stock) : ControllerBase
{
    /// <summary>입고. 같은 ReferenceNo로 다시 보내도 재고는 한 번만 늘어난다(재시도 안전).</summary>
    [HttpPost]
    public async Task<IActionResult> Receive(InboundRequest req, CancellationToken ct)
    {
        await stock.ReceiveAsync(req.ReferenceNo, ToLines(req.Lines), User.Identity?.Name, ct);
        return Ok(new { req.ReferenceNo, status = "RECEIVED" });
    }

    internal static List<LineRequest> ToLines(IEnumerable<LineDto> l) => l.Select(x => new LineRequest(x.Sku, x.LocationCode, x.Quantity)).ToList();
}

[ApiController]
[Authorize]
[Route("api/orders")]
public class OrdersController(StockService stock, WmsDbContext db) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<OrderResponse>> Create(OrderRequest req, CancellationToken ct)
    {
        var order = await stock.CreateOrderAsync(req.OrderNo, InboundController.ToLines(req.Lines), User.Identity?.Name, ct);
        return CreatedAtAction(nameof(Get), new { id = order.Id }, await Load(order.Id, ct));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderResponse>> Get(int id, CancellationToken ct) =>
        await Load(id, ct) ?? throw DomainException.NotFound($"주문 {id}이(가) 없습니다.");

    [HttpPost("{id:int}/ship")]
    public async Task<ActionResult<OrderResponse>> Ship(int id, CancellationToken ct)
    {
        await stock.ShipAsync(id, ct);
        return (await Load(id, ct))!;
    }

    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<OrderResponse>> Cancel(int id, CancellationToken ct)
    {
        await stock.CancelAsync(id, User.Identity?.Name, ct);
        return (await Load(id, ct))!;
    }

    private async Task<OrderResponse?> Load(int id, CancellationToken ct)
    {
        var o = await db.Orders.AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null) return null;
        var products = await db.Products.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Sku, ct);
        var locations = await db.Locations.AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code, ct);
        return new(o.Id, o.OrderNo, o.Status.ToString(), o.CreatedAt,
            o.Lines.Select(l => new LineDto(products[l.ProductId], locations[l.LocationId], l.Quantity)).ToList());
    }
}

public record StockRow(string Sku, string LocationCode, int Quantity);
public record MovementRow(DateTime At, string Sku, string LocationCode, string Type, int Quantity, string Reference, string? By);

[ApiController]
[Authorize]
[Route("api")]
public class StocksController(WmsDbContext db) : ControllerBase
{
    [HttpGet("stocks")]
    public async Task<List<StockRow>> Stocks([FromQuery] string? sku, CancellationToken ct) =>
        await db.StockItems.AsNoTracking()
            .Where(s => sku == null || s.Product.Sku == sku)
            .OrderBy(s => s.Product.Sku).ThenBy(s => s.Location.Code)
            .Select(s => new StockRow(s.Product.Sku, s.Location.Code, s.Quantity))
            .ToListAsync(ct);

    /// <summary>수불 원장 조회. 재고가 왜 이렇게 됐는지 추적하는 용도.</summary>
    [HttpGet("movements")]
    public async Task<List<MovementRow>> Movements([FromQuery] string? sku, [FromQuery] int take = 50, CancellationToken ct = default) =>
        await db.Movements.AsNoTracking()
            .Join(db.Products, m => m.ProductId, p => p.Id, (m, p) => new { m, p.Sku })
            .Join(db.Locations, x => x.m.LocationId, l => l.Id, (x, l) => new { x.m, x.Sku, l.Code })
            .Where(x => sku == null || x.Sku == sku)
            .OrderByDescending(x => x.m.Id).Take(Math.Clamp(take, 1, 200))
            .Select(x => new MovementRow(x.m.CreatedAt, x.Sku, x.Code, x.m.Type.ToString(), x.m.Quantity, x.m.Reference, x.m.CreatedBy))
            .ToListAsync(ct);
}
