using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace WmsCore.Api.Common;

/// <summary>업무 규칙 위반. HTTP 상태 코드와 함께 ProblemDetails로 변환된다.</summary>
public class DomainException(int status, string title, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Title { get; } = title;

    public static DomainException NotFound(string detail) => new(404, "대상을 찾을 수 없습니다", detail);
    public static DomainException Conflict(string title, string detail) => new(409, title, detail);
}

public class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        ProblemDetails problem;
        if (ex is DomainException de)
        {
            problem = new() { Status = de.Status, Title = de.Title, Detail = de.Message };
        }
        else
        {
            log.LogError(ex, "처리되지 않은 예외");
            problem = new() { Status = 500, Title = "서버 오류", Detail = "잠시 후 다시 시도해 주세요." }; // 내부 정보는 노출하지 않는다
        }
        ctx.Response.StatusCode = problem.Status!.Value;
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
