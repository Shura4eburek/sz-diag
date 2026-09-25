using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SzDiag.Contracts;

namespace SzDiag.Hub;

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
        // using — Process.GetCurrentProcess() возвращает новый handle на каждый вызов
        // (review W2 Minor): не освобождать его на эндпоинте здоровья, который может
        // дёргаться часто, накопило бы утечку native-хэндлов.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        return new HealthzResponse(
            self.Threads.Count,
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
