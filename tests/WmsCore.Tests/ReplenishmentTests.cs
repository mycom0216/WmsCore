using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WmsCore.Api.Data;
using WmsCore.Api.Domain;
using WmsCore.Api.Services;

namespace WmsCore.Tests;

/// <summary>AI 서비스 자리에 가짜 HTTP 핸들러를 끼운 팩토리. 요청 내용을 캡처하고 원하는 응답/장애를 흉내 낸다.</summary>
public class AiFakeFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"wms-ai-{Guid.NewGuid():N}.db");
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handler { get; set; } = _ => throw new InvalidOperationException("핸들러 미설정");
    public string? LastBody { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Provider", "Sqlite");
        var liveUrl = Environment.GetEnvironmentVariable("WMS_AI_URL"); // 실제 파이썬 서비스로 붙이는 연동 테스트용
        builder.ConfigureServices(s =>
        {
            if (liveUrl is not null)
                s.AddHttpClient<ForecastClient>(c => c.BaseAddress = new Uri(liveUrl));
            else
                s.AddHttpClient<ForecastClient>(c => c.BaseAddress = new Uri("http://fake-ai"))
                 .ConfigurePrimaryHttpMessageHandler(() => new FakeHandler(this));
        });
    }

    private sealed class FakeHandler(AiFakeFactory f) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            f.LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return await f.Handler(req);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var x in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" }) try { File.Delete(x); } catch { }
    }
}

public class LiveOnlyFactAttribute : FactAttribute
{
    public LiveOnlyFactAttribute() { if (Environment.GetEnvironmentVariable("WMS_AI_URL") is null) Skip = "WMS_AI_URL 환경 변수가 없어 건너뜀 (실제 AI 서비스 필요)"; }
}

public class ReplenishmentTests : IClassFixture<AiFakeFactory>
{
    private readonly AiFakeFactory _f;
    public ReplenishmentTests(AiFakeFactory f) => _f = f;

    private async Task<HttpClient> AdminAsync()
    {
        var c = _f.CreateClient();
        var res = await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "Admin123!" });
        var token = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    private static string Uid() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>상품·위치·현재고를 만들고, 과거 N일간 매일 `perDay`개씩 출고된 원장을 직접 심는다(서버 시간으로는 과거를 만들 수 없어서).</summary>
    private async Task<(HttpClient admin, string sku)> ArrangeAsync(int daysOfHistory, int perDay, int stock = 80)
    {
        var admin = await AdminAsync();
        var sku = "SKU-" + Uid(); var loc = "L-" + Uid();
        (await admin.PostAsJsonAsync("/api/products", new { sku, name = "상품" })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/locations", new { code = loc })).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync("/api/inbound", new { referenceNo = "IN-" + Uid(), lines = new[] { new { sku, locationCode = loc, quantity = stock } } })).EnsureSuccessStatusCode();

        using var scope = _f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
        var p = db.Products.Single(x => x.Sku == sku); var l = db.Locations.Single(x => x.Code == loc);
        var today = DateTime.UtcNow.Date;
        for (var i = 1; i <= daysOfHistory; i++)
            db.Movements.Add(new StockMovement { ProductId = p.Id, LocationId = l.Id, Type = MovementType.Allocate, Quantity = perDay, Reference = "SEED", CreatedAt = today.AddDays(-i).AddHours(10) });
        await db.SaveChangesAsync();
        return (admin, sku);
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task 일별_출고량은_할당에서_복구를_뺀_값이다()
    {
        var (admin, sku) = await ArrangeAsync(0, 0);
        using (var scope = _f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WmsDbContext>();
            var p = db.Products.Single(x => x.Sku == sku); var l = db.Locations.First();
            var day = DateTime.UtcNow.Date.AddDays(-2).AddHours(9);
            db.Movements.AddRange(
                new StockMovement { ProductId = p.Id, LocationId = l.Id, Type = MovementType.Allocate, Quantity = 10, Reference = "A", CreatedAt = day },
                new StockMovement { ProductId = p.Id, LocationId = l.Id, Type = MovementType.Allocate, Quantity = 5, Reference = "B", CreatedAt = day.AddHours(3) },
                new StockMovement { ProductId = p.Id, LocationId = l.Id, Type = MovementType.Release, Quantity = 4, Reference = "A", CreatedAt = day.AddHours(5) });
            await db.SaveChangesAsync();
        }

        var rows = await admin.GetFromJsonAsync<JsonElement>($"/api/products/{sku}/daily-outbound");

        Assert.Equal(1, rows.GetArrayLength());
        Assert.Equal(11, rows[0].GetProperty("quantity").GetInt32()); // 10 + 5 − 4
    }

    [Fact]
    public async Task 보충_제안은_이력과_현재고를_AI에_보내고_응답을_돌려준다()
    {
        var (admin, sku) = await ArrangeAsync(70, 6, stock: 80);
        _f.Handler = _ => Task.FromResult(Ok("""
            {"sku":"x","model":"gbr","forecast":[{"date":"2030-01-01","quantity":6.5}],
             "backtest_wape":{"gbr":0.12,"naive":0.25},"residual_std":2.5,
             "reorder":{"safety_stock":8.2,"reorder_point":38.2,"should_reorder":false,"suggested_order_qty":40}}
            """));

        var res = await admin.GetAsync($"/api/products/{sku}/replenishment?horizon=7&leadTimeDays=4&serviceLevel=0.9");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("gbr", body.GetProperty("model").GetString());
        Assert.Equal(40, body.GetProperty("reorder").GetProperty("suggestedOrderQty").GetInt32());

        // AI 서비스가 받은 요청: snake_case, 현재고 80, 이력 70일 + 오늘(0) 채움, 파라미터 전달
        var sent = JsonDocument.Parse(_f.LastBody!).RootElement;
        Assert.Equal(80, sent.GetProperty("current_stock").GetInt32());
        Assert.Equal(7, sent.GetProperty("horizon").GetInt32());
        Assert.Equal(4, sent.GetProperty("lead_time_days").GetInt32());
        Assert.Equal(0.9, sent.GetProperty("service_level").GetDouble());
        var history = sent.GetProperty("history");
        Assert.Equal(71, history.GetArrayLength());
        Assert.Equal(0, history[70].GetProperty("quantity").GetDouble());
    }

    [Fact]
    public async Task AI_서비스가_죽어_있으면_503()
    {
        var (admin, sku) = await ArrangeAsync(70, 6);
        _f.Handler = _ => throw new HttpRequestException("connection refused");

        var res = await admin.GetAsync($"/api/products/{sku}/replenishment");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task 이력이_부족해_AI가_422를_주면_422로_전달한다()
    {
        var (admin, sku) = await ArrangeAsync(10, 6);
        _f.Handler = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));

        var res = await admin.GetAsync($"/api/products/{sku}/replenishment");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task 없는_상품은_404()
    {
        var admin = await AdminAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/products/NOPE/replenishment")).StatusCode);
    }

    [LiveOnlyFact]
    public async Task 실제_AI_서비스와_연동()
    {
        var (admin, sku) = await ArrangeAsync(120, 8, stock: 30);

        var res = await admin.GetAsync($"/api/products/{sku}/replenishment?horizon=7");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(7, body.GetProperty("forecast").GetArrayLength());
        Assert.True(body.GetProperty("reorder").GetProperty("suggestedOrderQty").GetInt32() >= 0);
    }
}
