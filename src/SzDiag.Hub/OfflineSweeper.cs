using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Фоново метит офлайн сессии с протухшим heartbeat.</summary>
public sealed class OfflineSweeper : BackgroundService
{
    private readonly SessionRegistry _registry;
    private readonly HubOptions _options;
    private readonly JournalWriter _journal;

    public OfflineSweeper(SessionRegistry registry, IOptions<HubOptions> options,
        JournalWriter journal)
    {
        _registry = registry;
        _options = options.Value;
        _journal = journal;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Потеря связи — тоже факт диагностики: под нагрузкой heartbeat лагает, и потом
            // важно знать, когда именно машина замолчала (вырубоном это само по себе не является).
            foreach (var sz in _registry.MarkStaleOffline(_options.HeartbeatTimeout))
                _journal.Machine(sz, "зв'язок втрачено (heartbeat не приходить)");
        }
    }
}
