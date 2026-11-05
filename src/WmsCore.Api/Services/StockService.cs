using Microsoft.EntityFrameworkCore;
using WmsCore.Api.Common;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;

namespace WmsCore.Api.Services;

public record LineRequest(string Sku, string LocationCode, int Quantity);
public record ResolvedLine(int ProductId, int LocationId, string Sku, string LocationCode, int Quantity);

/// <summary>
/// 재고 증감 핵심 로직.
/// 동시성 전략: 읽고-검사하고-쓰는 대신 "조건부 UPDATE 한 문장"으로 처리한다.
///   UPDATE StockItems SET Quantity = Quantity - @q WHERE ProductId=@p AND LocationId=@l AND Quantity >= @q
/// 영향 행이 0이면 재고 부족. 락 대기 없이 DB가 원자적으로 판단하므로 초과 출고(oversell)가 생기지 않고,
/// SQLite/SQL Server 모두 같은 코드로 동작한다. 한 트랜잭션에서 여러 줄을 갱신할 땐 정렬된 순서로 처리해 데드락을 피한다.
/// </summary>
public class StockService(WmsDbContext db, TimeProvider clock)
{
    public async Task ReceiveAsync(string referenceNo, List<LineRequest> lines, string? user, CancellationToken ct)
    {
        var resolved = await ResolveAsync(lines, ct);

        if (await db.Receipts.AnyAsync(r => r.ReferenceNo == referenceNo, ct))
            return; // 이미 처리된 전표: 같은 요청을 다시 보내도 결과는 같다(멱등)

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Receipts.Add(new InboundReceipt { ReferenceNo = referenceNo, ReceivedAt = clock.GetUtcNow().UtcDateTime });
        try
        {
            await db.SaveChangesAsync(ct); // 쓰기를 가장 먼저: 동시에 같은 전표가 오면 유일 인덱스가 한쪽만 통과시킨다
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.Receipts.AnyAsync(r => r.ReferenceNo == referenceNo, ct)) return;
            throw;
        }

        foreach (var l in resolved)
        {
            await AddStockAsync(l.ProductId, l.LocationId, l.Quantity, ct);
            AddMovement(l, MovementType.Inbound, referenceNo, user);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<Order> CreateOrderAsync(string orderNo, List<LineRequest> lines, string? user, CancellationToken ct)
    {
        var resolved = await ResolveAsync(lines, ct);

        if (await db.Orders.AnyAsync(o => o.OrderNo == orderNo, ct))
            throw DomainException.Conflict("중복 주문", $"주문번호 {orderNo}는 이미 존재합니다.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = new Order
        {
            OrderNo = orderNo,
            Status = OrderStatus.Created,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
            Lines = resolved.Select(l => new OrderLine { ProductId = l.ProductId, LocationId = l.LocationId, Quantity = l.Quantity }).ToList(),
        };
        db.Orders.Add(order);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (await db.Orders.AnyAsync(o => o.OrderNo == orderNo, ct))
                throw DomainException.Conflict("중복 주문", $"주문번호 {orderNo}는 이미 존재합니다.");
            throw;
        }

        foreach (var l in resolved)
        {
            var affected = await db.StockItems
                .Where(s => s.ProductId == l.ProductId && s.LocationId == l.LocationId && s.Quantity >= l.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - l.Quantity), ct);
            if (affected == 0)
                throw DomainException.Conflict("재고 부족", $"{l.Sku} @ {l.LocationCode}: 요청 {l.Quantity}개를 출고할 재고가 없습니다."); // using 해제 시 전체 롤백
            AddMovement(l, MovementType.Allocate, orderNo, user);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return order;
    }

    public Task ShipAsync(int orderId, CancellationToken ct) => TransitionAsync(orderId, OrderStatus.Shipped, null, ct);

    public Task CancelAsync(int orderId, string? user, CancellationToken ct) => TransitionAsync(orderId, OrderStatus.Cancelled, user, ct);

    /// <summary>Created 상태일 때만 전이. 조건부 UPDATE라서 동시에 두 번 취소해도 재고는 한 번만 복구된다.</summary>
    private async Task TransitionAsync(int orderId, OrderStatus target, string? user, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var affected = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Created)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, target), ct);
        if (affected == 0)
        {
            var current = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (OrderStatus?)o.Status).FirstOrDefaultAsync(ct);
            throw current is null
                ? DomainException.NotFound($"주문 {orderId}이(가) 없습니다.")
                : DomainException.Conflict("상태 변경 불가", $"현재 상태({current})에서는 {target}(으)로 바꿀 수 없습니다.");
        }

        if (target == OrderStatus.Cancelled)
        {
            var order = await db.Orders.AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == orderId, ct);
            foreach (var l in order.Lines.OrderBy(l => l.ProductId).ThenBy(l => l.LocationId))
            {
                await AddStockAsync(l.ProductId, l.LocationId, l.Quantity, ct);
                db.Movements.Add(new StockMovement
                {
                    ProductId = l.ProductId, LocationId = l.LocationId, Type = MovementType.Release, Quantity = l.Quantity,
                    Reference = order.OrderNo, CreatedBy = user, CreatedAt = clock.GetUtcNow().UtcDateTime,
                });
            }
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>있으면 증가, 없으면 생성. 동시에 처음 입고되어 INSERT가 충돌하면 UPDATE로 다시 시도한다.</summary>
    private async Task AddStockAsync(int productId, int locationId, int qty, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var affected = await db.StockItems
                .Where(s => s.ProductId == productId && s.LocationId == locationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + qty), ct);
            if (affected == 1) return;

            try
            {
                db.StockItems.Add(new StockItem { ProductId = productId, LocationId = locationId, Quantity = qty });
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // 다른 요청이 먼저 만들었다 → 루프를 돌아 UPDATE
            }
        }
        throw new InvalidOperationException("재고 반영에 반복 실패했습니다.");
    }

    private void AddMovement(ResolvedLine l, MovementType type, string reference, string? user) =>
        db.Movements.Add(new StockMovement
        {
            ProductId = l.ProductId, LocationId = l.LocationId, Type = type, Quantity = l.Quantity,
            Reference = reference, CreatedBy = user, CreatedAt = clock.GetUtcNow().UtcDateTime,
        });

    /// <summary>SKU/위치 코드를 ID로 바꾸고, 같은 (SKU, 위치) 줄은 합친 뒤 정렬한다(데드락 방지용 고정 순서).</summary>
    private async Task<List<ResolvedLine>> ResolveAsync(List<LineRequest> lines, CancellationToken ct)
    {
        var skus = lines.Select(l => l.Sku).Distinct().ToList();
        var codes = lines.Select(l => l.LocationCode).Distinct().ToList();
        var products = await db.Products.AsNoTracking().Where(p => skus.Contains(p.Sku)).ToDictionaryAsync(p => p.Sku, p => p.Id, ct);
        var locations = await db.Locations.AsNoTracking().Where(x => codes.Contains(x.Code)).ToDictionaryAsync(x => x.Code, x => x.Id, ct);

        foreach (var l in lines)
        {
            if (!products.ContainsKey(l.Sku)) throw DomainException.NotFound($"상품 {l.Sku}이(가) 없습니다.");
            if (!locations.ContainsKey(l.LocationCode)) throw DomainException.NotFound($"위치 {l.LocationCode}이(가) 없습니다.");
        }

        return lines
            .GroupBy(l => (l.Sku, l.LocationCode))
            .Select(g => new ResolvedLine(products[g.Key.Sku], locations[g.Key.LocationCode], g.Key.Sku, g.Key.LocationCode, g.Sum(x => x.Quantity)))
            .OrderBy(l => l.ProductId).ThenBy(l => l.LocationId)
            .ToList();
    }
}
