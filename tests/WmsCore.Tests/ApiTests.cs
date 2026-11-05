using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace WmsCore.Tests;

/// <summary>테스트마다 임시 SQLite 파일 DB를 새로 만든다. 실제 서버 파이프라인(JWT, 예외 처리, DB 트랜잭션)을 그대로 통과한다.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"wms-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Provider", "Sqlite");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(f); } catch { /* 정리 실패는 무시 */ }
    }
}

public class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ApiTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> LoginAsync(string user, string password)
    {
        var c = _factory.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { userName = user, password });
        res.EnsureSuccessStatusCode();
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>상품 1개·위치 1개를 만들고 입고까지 마친 상태를 준비한다.</summary>
    private async Task<(HttpClient admin, string sku, string loc)> ArrangeStockAsync(int qty)
    {
        var admin = await LoginAsync("admin", "Admin123!");
        var sku = "SKU-" + Uid();
        var loc = "A-" + Uid();
        (await admin.PostAsJsonAsync("/api/products", new { sku, name = "테스트 상품" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/locations", new { code = loc })).EnsureSuccessStatusCode();
        var inbound = await admin.PostAsJsonAsync("/api/inbound", new { referenceNo = "IN-" + Uid(), lines = new[] { new { sku, locationCode = loc, quantity = qty } } });
        inbound.EnsureSuccessStatusCode();
        return (admin, sku, loc);
    }

    private static async Task<int> StockOf(HttpClient c, string sku)
    {
        var rows = await c.GetFromJsonAsync<JsonElement>($"/api/stocks?sku={sku}");
        return rows.GetArrayLength() == 0 ? 0 : rows[0].GetProperty("quantity").GetInt32();
    }

    private static object Order(string orderNo, params (string sku, string loc, int qty)[] lines) =>
        new { orderNo, lines = lines.Select(l => new { sku = l.sku, locationCode = l.loc, quantity = l.qty }).ToArray() };

    [Fact]
    public async Task 토큰_없이는_401_운영자는_상품등록_403()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/products")).StatusCode);

        var op = await LoginAsync("operator", "Operator123!");
        var res = await op.PostAsJsonAsync("/api/products", new { sku = "X-" + Uid(), name = "n" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task 잘못된_비밀번호는_401()
    {
        var res = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task 입력값_검증_수량_0은_400()
    {
        var (admin, sku, loc) = await ArrangeStockAsync(5);
        var res = await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), (sku, loc, 0)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task 같은_입고전표를_두번_보내도_재고는_한번만_늘어난다()
    {
        var (admin, sku, loc) = await ArrangeStockAsync(10);
        var body = new { referenceNo = "IN-DUP-" + Uid(), lines = new[] { new { sku, locationCode = loc, quantity = 7 } } };

        (await admin.PostAsJsonAsync("/api/inbound", body)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/inbound", body)).EnsureSuccessStatusCode(); // 네트워크 재시도 상황

        Assert.Equal(17, await StockOf(admin, sku));
    }

    [Fact]
    public async Task 재고_부족이면_409이고_앞줄도_함께_롤백된다()
    {
        var (admin, skuA, locA) = await ArrangeStockAsync(10);
        var (_, skuB, locB) = await ArrangeStockAsync(1);

        // 첫 줄(A 3개)은 가능하지만 둘째 줄(B 5개)이 부족 → 주문 전체가 취소되어야 한다
        var res = await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), (skuA, locA, 3), (skuB, locB, 5)));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(10, await StockOf(admin, skuA));
        Assert.Equal(1, await StockOf(admin, skuB));
    }

    [Fact]
    public async Task 동시_주문_50건이_재고_10개를_두고_경쟁해도_정확히_10건만_성공한다()
    {
        var (admin, sku, loc) = await ArrangeStockAsync(10);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
        {
            var res = await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), (sku, loc, 1)));
            return res.StatusCode;
        }));

        Assert.Equal(10, results.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(40, results.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(0, await StockOf(admin, sku)); // 초과 출고 없음, 음수 재고 없음
    }

    [Fact]
    public async Task 같은_주문을_동시에_취소해도_재고는_한번만_복구된다()
    {
        var (admin, sku, loc) = await ArrangeStockAsync(10);
        var created = await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), (sku, loc, 4)));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(6, await StockOf(admin, sku));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => (await admin.PostAsync($"/api/orders/{id}/cancel", null)).StatusCode));

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(9, results.Count(s => s == HttpStatusCode.Conflict));
        Assert.Equal(10, await StockOf(admin, sku)); // 4개가 한 번만 돌아옴 (14가 되면 안 됨)
    }

    [Fact]
    public async Task 출하된_주문은_취소할_수_없다()
    {
        var (admin, sku, loc) = await ArrangeStockAsync(5);
        var created = await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), (sku, loc, 2)));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        (await admin.PostAsync($"/api/orders/{id}/ship", null)).EnsureSuccessStatusCode();
        var cancel = await admin.PostAsync($"/api/orders/{id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal(3, await StockOf(admin, sku));
    }

    [Fact]
    public async Task 수불원장에_입고와_출고가_기록된다()
    {
        var (admin, sku, loc) = await ArrangeStockAsync(10);
        (await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), (sku, loc, 3)))).EnsureSuccessStatusCode();

        var rows = await admin.GetFromJsonAsync<JsonElement>($"/api/movements?sku={sku}");
        var types = rows.EnumerateArray().Select(r => r.GetProperty("type").GetString()).ToList();

        Assert.Contains("Inbound", types);
        Assert.Contains("Allocate", types);
    }

    [Fact]
    public async Task 없는_상품은_404()
    {
        var admin = await LoginAsync("admin", "Admin123!");
        var loc = "A-" + Uid();
        (await admin.PostAsJsonAsync("/api/locations", new { code = loc })).EnsureSuccessStatusCode();
        var res = await admin.PostAsJsonAsync("/api/orders", Order("O-" + Uid(), ("NOPE-" + Uid(), loc, 1)));
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
