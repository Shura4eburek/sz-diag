using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Фоновый сторож thread pool starvation (бэклог п.50, СЗ 160306): раньше hub
/// молчал, пока не переваливал за тысячи потоков и переставал отвечать вовсе — единственной
/// уликой был `Get-Process` руками. Сторож пишет явную строку в лог/консоль (Tee уносит её
/// в `logs\hub-<дата>.log`) при первом переходе за порог, чтобы залипание было видно ДО
/// того, как перестанут отвечать HTTP-запросы, а не после.</summary>
public sealed class ThreadPoolWatchdog : BackgroundService
{
    private readonly HubOptions _options;
    private bool _alreadyWarned;

    public ThreadPoolWatchdog(IOptions<HubOptions> options) => _options = options.Value;

    /// <summary>Чистая проверка — вынесена ради тестируемости без реального ThreadPool.</summary>
    public static bool IsStarving(int threadCount, int threshold) => threadCount >= threshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.ThreadPoolWarnThreshold <= 0) return; // рубильник выключен

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var snapshot = HealthApi.Snapshot();
            var starving = IsStarving(snapshot.ThreadCount, _options.ThreadPoolWarnThreshold);
            if (starving && !_alreadyWarned)
            {
                Console.WriteLine(
                    $"[hub] THREAD POOL STARVATION: потоков {snapshot.ThreadCount} " +
                    $"(порог {_options.ThreadPoolWarnThreshold}), свободных worker {snapshot.AvailableWorkerThreads}/" +
                    $"{snapshot.MaxWorkerThreads}, свободных IO {snapshot.AvailableCompletionPortThreads}/" +
                    $"{snapshot.MaxCompletionPortThreads}, в очереди {snapshot.PendingWorkItemCount}. " +
                    "Похоже на sync-over-async где-то в коде — dotnet-dump collect + clrstack -all.");
                _alreadyWarned = true;
            }
            else if (!starving)
            {
                _alreadyWarned = false; // снова предупредит, если проблема вернётся
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
