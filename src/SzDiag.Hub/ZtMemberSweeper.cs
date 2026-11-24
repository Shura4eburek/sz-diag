using Microsoft.Extensions.Options;

namespace SzDiag.Hub;

/// <summary>Сторож сервисной ZT-сети (T047). Сеть публичная — войти может кто угодно, знающий её
/// id; а headless-откат клиента удалить свой узел не может (токена контроллера у клиента нет).
/// Раз в минуту: член без активной СЗ, проживший дольше <see cref="HubZeroTierOptions.SweepAfter"/>,
/// удаляется. Узлы hub-машин и активных СЗ не трогаются.</summary>
public sealed class ZtMemberSweeper : BackgroundService
{
    private readonly HubZeroTierOptions _opts;
    private readonly SessionRegistry _registry;
    private readonly IZtControllerClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<ZtMemberSweeper> _logger;
    // Контроллер мог не отдать creationTime — тогда срок считаем от первого раза, когда увидели.
    private readonly Dictionary<string, DateTimeOffset> _firstSeen = new();

    public ZtMemberSweeper(IOptions<HubOptions> options, SessionRegistry registry, IZtControllerClient client,
        ILogger<ZtMemberSweeper> logger, TimeProvider? time = null)
    {
        _opts = options.Value.ZeroTier;
        _registry = registry;
        _client = client;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_opts.ControllerConfigured) return;
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
