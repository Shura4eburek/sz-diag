using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Сторож сервисной ZT-сети (T047). Сеть публичная — войти может кто угодно, знающий её
/// id; а headless-откат клиента удалить свой узел не может (токена контроллера у клиента нет).
/// Раз в минуту: член без активной СЗ, проживший дольше <see cref="HubZeroTierOptions.SweepAfter"/>,
/// удаляется. Узлы hub-машин и активных СЗ не трогаются. Первый обход — после паузы на старте
/// (<see cref="MinStartupGrace"/>), и только когда аренда указателя наша или указатель выключен.</summary>
public sealed class ZtMemberSweeper : BackgroundService
{
    private readonly HubZeroTierOptions _opts;
    private readonly SessionRegistry _registry;
    private readonly IZtControllerClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<ZtMemberSweeper> _logger;
    // Контроллер мог не отдать creationTime — тогда срок считаем от первого раза, когда увидели.
    private readonly Dictionary<string, DateTimeOffset> _firstSeen = new();
    private readonly HubStatusTracker _status;
    private readonly DateTimeOffset _startedAt;
    private readonly TimeSpan _startupGrace;
    // Причина последнего пропуска — чтобы писать в лог только смену, а не каждую минуту.
    private string? _lastSkip;

    /// <summary>Минимальная пауза после старта hub: реестр сессий живёт в памяти и после рестарта
    /// пуст, пока агенты не перерегистрируются и не пришлют ReportAccess. Без паузы сторож снёс бы
    /// ZT-узлы живых клиентов — а по ним идёт управляющий канал.</summary>
    public static readonly TimeSpan MinStartupGrace = TimeSpan.FromMinutes(5);

    public ZtMemberSweeper(IOptions<HubOptions> options, SessionRegistry registry, IZtControllerClient client,
        HubStatusTracker status, ILogger<ZtMemberSweeper> logger, TimeProvider? time = null,
        TimeSpan? startupGrace = null)
    {
        _opts = options.Value.ZeroTier;
        _registry = registry;
        _client = client;
        _status = status;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _startedAt = _time.GetUtcNow();
        _startupGrace = startupGrace ?? (_opts.SweepAfter > MinStartupGrace ? _opts.SweepAfter : MinStartupGrace);
    }

    /// <summary>Можно ли сейчас удалять узлы. Нельзя во время паузы после старта и когда указатель
    /// держит другой hub: у него свой реестр, его клиенты для нас — «без СЗ». Unreachable/Misconfigured
    /// тоже пропускаем: кто держит аренду — неизвестно. Off — указатель выключен, hub один.</summary>
    public bool ShouldSweep(DateTimeOffset now, out string? skipReason)
    {
        if (now - _startedAt < _startupGrace)
        {
            skipReason = $"пауза после старта hub ({_startupGrace.TotalMinutes:0} мин) — ждём перерегистрации агентов";
            return false;
        }
        var sp = _status.SignpostSnapshot();
        if (sp.State == SzDiag.Contracts.Signpost.SignpostStates.Published
            || sp.State == SzDiag.Contracts.Signpost.SignpostStates.Off)
        {
            skipReason = null;
            return true;
        }
        skipReason = sp.State == SzDiag.Contracts.Signpost.SignpostStates.OtherHolder
            ? $"аренду указателя держит hub «{sp.Holder?.HubName}» — его клиентов не трогаем"
            : $"состояние указателя «{sp.State}» — неясно, чья аренда";
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.ControllerConfigured) return;
        var wait = _startedAt + _startupGrace - _time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            try { await Task.Delay(wait, _time, ct); }
            catch (OperationCanceledException) { return; }
        }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), _time);
        do
        {
            try
            {
                var removed = await SweepOnceAsync(ct);
                if (removed.Count > 0)
                    _logger.LogInformation("zt: из сервисной сети удалены узлы без СЗ: {Nodes}", string.Join(", ", removed));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("zt: сторож сети не прошёл: {Error}", ex.Message);
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task<IReadOnlyList<string>> SweepOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (!ShouldSweep(now, out var skip))
        {
            if (skip != _lastSkip)
            {
                _lastSkip = skip;
                _logger.LogInformation("zt: сторож сети пропускает обход: {Reason}", skip);
            }
            return [];
        }
        _lastSkip = null;
        var active = _registry.GetActive()
            .Select(s => s.ZtNodeId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = new List<string>();

        var ids = await _client.ListMemberIdsAsync(ct);
        foreach (var gone in _firstSeen.Keys.Except(ids).ToList()) _firstSeen.Remove(gone);

        foreach (var id in ids)
        {
            if (active.Contains(id)) continue;
            var m = await _client.GetMemberAsync(id, ct);
            if (m is null) continue;
            if (IsHub(m)) continue;

            var since = m.CreatedAt ?? (_firstSeen.TryGetValue(id, out var seen) ? seen : _firstSeen[id] = now);
            if (now - since < _opts.SweepAfter) continue;

            if (await _client.DeleteMemberAsync(id, ct))
            {
                removed.Add(id);
                _firstSeen.Remove(id);
            }
        }
        return removed;
    }

    private bool IsHub(ZtMember m) =>
        m.Name.StartsWith("hub-", StringComparison.OrdinalIgnoreCase)
        || m.IpAssignments.Any(ip => _opts.HubAddresses.Contains(ip));
}
