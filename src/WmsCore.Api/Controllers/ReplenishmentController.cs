using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using WmsCore.Api.Common;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;
using WmsCore.Api.Services;

namespace WmsCore.Api.Controllers;

public record DailyOutbound(DateOnly Date, int Quantity);

[ApiController]
[Authorize]
[Route("api/products/{sku}")]
public class ReplenishmentController(WmsDbContext db, ForecastClient ai, TimeProvider clock) : ControllerBase
{
    /// <summary>일별 순출고량 = 그날 할당(Allocate) − 취소 복구(Release). 출고 이력이 없는 날은 포함하지 않는다.</summary>
    [HttpGet("daily-outbound")]
    public async Task<List<DailyOutbound>> DailyOutbound(string sku, [FromQuery] int days = 180, CancellationToken ct = default) =>
        await LoadDailyAsync(sku, Math.Clamp(days, 1, 730), ct);

    /// <summary>
    /// 출고 이력을 AI 서비스에 보내 수요를 예측하고 발주점·발주량을 받는다.
    /// 현재고는 모든 위치의 합계를 사용한다.
    /// </summary>
    [HttpGet("replenishment")]
    public async Task<AiForecastResponse> Replenishment(string sku, [FromQuery] int horizon = 14, [FromQuery] int leadTimeDays = 5,
        [FromQuery] double serviceLevel = 0.95, CancellationToken ct = default)
    {
        var history = await LoadDailyAsync(sku, 365, ct);
        var currentStock = await db.StockItems.Where(s => s.Product.Sku == sku).SumAsync(s => (int?)s.Quantity, ct) ?? 0;

        var points = history.Select(h => new HistoryPoint(h.Date, h.Quantity)).ToList();
        // 오늘까지 이력이 이어지도록 마지막 날을 0으로 채워 보낸다(최근 며칠 출고가 없었다는 정보도 예측에 중요하다).
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (points.Count > 0 && points[^1].Date < today) points.Add(new HistoryPoint(today, 0));

        return await ai.ForecastAsync(new AiForecastRequest(sku, points, horizon, leadTimeDays, currentStock, serviceLevel), ct);
    }

    private async Task<List<DailyOutbound>> LoadDailyAsync(string sku, int days, CancellationToken ct)
    {
        if (!await db.Products.AnyAsync(p => p.Sku == sku, ct)) throw DomainException.NotFound($"상품 {sku}이(가) 없습니다.");

        var since = clock.GetUtcNow().UtcDateTime.Date.AddDays(-(days - 1));
        var rows = await db.Movements.AsNoTracking()
            .Where(m => m.Type != MovementType.Inbound && m.CreatedAt >= since)
            .Join(db.Products.Where(p => p.Sku == sku), m => m.ProductId, p => p.Id, (m, _) => new { m.CreatedAt, m.Type, m.Quantity })
            .ToListAsync(ct);

        // 날짜별 합산은 메모리에서 한다: 한 상품의 최대 730일치 원장이라 양이 작고, DB별 날짜 함수 번역 차이를 피할 수 있다.
        return rows
            .GroupBy(r => DateOnly.FromDateTime(r.CreatedAt))
            .Select(g => new DailyOutbound(g.Key, g.Sum(r => r.Type == MovementType.Allocate ? r.Quantity : -r.Quantity)))
            .OrderBy(d => d.Date)
            .ToList();
    }
}
