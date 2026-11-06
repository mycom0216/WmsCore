using System.Net.Http.Json;
using System.Text.Json;
using WmsCore.Api.Common;

namespace WmsCore.Api.Services;

public record HistoryPoint(DateOnly Date, double Quantity);
public record AiForecastRequest(string Sku, List<HistoryPoint> History, int Horizon, int LeadTimeDays, int CurrentStock, double ServiceLevel);
public record DayForecast(DateOnly Date, double Quantity);
public record ReorderPlan(double SafetyStock, double ReorderPoint, bool ShouldReorder, int SuggestedOrderQty);
public record AiForecastResponse(string Sku, string Model, List<DayForecast> Forecast, Dictionary<string, double> BacktestWape, double ResidualStd, ReorderPlan Reorder);

/// <summary>Python AI 서비스(/forecast) 호출 클라이언트. 장애를 업무 예외(503/422)로 바꿔 상위에서 일관되게 응답한다.</summary>
public class ForecastClient(HttpClient http, ILogger<ForecastClient> log)
{
    // 파이썬 서비스는 snake_case(lead_time_days)를 쓴다.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public async Task<AiForecastResponse> ForecastAsync(AiForecastRequest req, CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await http.PostAsJsonAsync("/forecast", req, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "AI 서비스 호출 실패");
            throw new DomainException(503, "AI 서비스를 사용할 수 없습니다", "수요 예측 서비스에 연결하지 못했습니다. 잠시 후 다시 시도해 주세요.");
        }

        using (res)
        {
            if (res.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
                throw new DomainException(422, "예측할 수 없는 데이터", "출고 이력이 부족하거나 형식이 올바르지 않습니다(최소 60일 필요).");
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("AI 서비스 응답 오류 {Status}", (int)res.StatusCode);
                throw new DomainException(503, "AI 서비스 오류", "수요 예측 서비스가 오류를 반환했습니다.");
            }
            return (await res.Content.ReadFromJsonAsync<AiForecastResponse>(Json, ct))!;
        }
    }
}
