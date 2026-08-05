using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>
/// Фоновый оффсайт-бэкап базы знаний: прогон на старте (догнать правки, сделанные
/// руками, пока hub не был поднят), дальше по таймеру, и финальный — при остановке.
/// </summary>
public sealed class KbBackupService : BackgroundService
{
    private readonly IKbBackup _backup;
    private readonly KbBackupOptions _options;
    private readonly ILogger<KbBackupService> _logger;

    public KbBackupService(IKbBackup backup, IOptions<HubOptions> options, ILogger<KbBackupService> logger)
    {
        _backup = backup;
        _options = options.Value.KbBackup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        await RunSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafeAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!_options.Enabled) return;

        // Финальный прогон: закрыли hub — свежак уехал сразу. Токен не передаём (он уже
        // отменён остановкой); прогон ограничен shutdown-таймаутом хоста, и если не успел —
        // изменения просто уедут при следующем старте.
        await RunSafeAsync(CancellationToken.None);
    }

    private async Task RunSafeAsync(CancellationToken ct)
    {
        try
        {
            var result = await _backup.RunAsync(ct);
            switch (result.Outcome)
            {
                case KbBackupOutcome.NoChanges:
                    _logger.LogDebug("kb: изменений нет");
                    break;
                case KbBackupOutcome.Pushed:
                    _logger.LogInformation("kb: выгружено {Count} файл(ов)", result.ChangedFiles);
                    break;
                case KbBackupOutcome.CommittedNotPushed:
                    _logger.LogWarning("kb: закоммичено локально, push не прошёл: {Reason}", result.Message);
                    break;
                default:
                    _logger.LogWarning("kb: бэкап не прошёл: {Reason}", result.Message);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Штатная остановка hub.
        }
        catch (Exception ex)
        {
            // Unhandled из BackgroundService валит весь хост — ловим всё.
            _logger.LogWarning(ex, "kb: бэкап упал");
        }
    }
}
