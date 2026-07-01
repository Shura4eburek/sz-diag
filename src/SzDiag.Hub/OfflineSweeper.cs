using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Фоново метит офлайн сессии с протухшим heartbeat.</summary>
public sealed class OfflineSweeper : BackgroundService
{
    private readonly SessionRegistry _registry;
    private readonly HubOptions _options;

    public OfflineSweeper(SessionRegistry registry, IOptions<HubOptions> options)
    {
        _registry = registry;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            _registry.MarkStaleOffline(_options.HeartbeatTimeout);
        }
    }
}
