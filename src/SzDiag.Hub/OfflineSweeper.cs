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
            var stale = _registry.MarkStaleOffline(_options.HeartbeatTimeout);

            // Признак 2 планового обесточивания (бэклог п.130): heartbeat пропал у нескольких
            // СЗ разом в одном цикле — похоже на свет в помещении, а не на дефект одной машины.
            if (stale.Count >= _options.MassOfflineMinSessions)
                _registry.RecordMassOfflineEvent();

            foreach (var sz in stale)
                _journal.Machine(sz, "зв'язок втрачено (heartbeat не приходить)");
        }
    }
}
