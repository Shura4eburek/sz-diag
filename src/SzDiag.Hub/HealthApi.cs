using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SzDiag.Hub;

/// <summary>Снимок ThreadPool в момент запроса — то, чем на живой заявке (СЗ 160306, бэклог
/// п.50) пришлось считать вручную через `Get-Process` (3674 потока при здоровых 26 сразу
/// после рестарта).</summary>
public sealed record HealthzResponse(
    int ThreadCount,
    int AvailableWorkerThreads,
    int MaxWorkerThreads,
    int AvailableCompletionPortThreads,
    int MaxCompletionPortThreads,
    long PendingWorkItemCount,
    DateTimeOffset At);

/// <summary>`/healthz` — минимальный путь, который обязан ответить даже когда весь остальной
/// hub захлебнулся в thread pool starvation: без токена, без обращения к SessionRegistry/
/// SQLite/SignalR — только счётчики самого рантайма. Раньше отличить «hub жив, но в
/// starvation» от «hub умер» можно было только подсчётом потоков руками (бэклог п.50).</summary>
public static class HealthApi
{
    public static HealthzResponse Snapshot()
    {
        ThreadPool.GetAvailableThreads(out var availWorker, out var availIo);
        ThreadPool.GetMaxThreads(out var maxWorker, out var maxIo);
        return new HealthzResponse(
            System.Diagnostics.Process.GetCurrentProcess().Threads.Count,
            availWorker, maxWorker,
            availIo, maxIo,
            ThreadPool.PendingWorkItemCount,
            DateTimeOffset.UtcNow);
    }

    public static void MapHealthApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/healthz", () => Results.Ok(Snapshot()));
    }
}
