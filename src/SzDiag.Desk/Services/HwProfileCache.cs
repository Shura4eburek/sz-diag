using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Services;

/// <summary>Профили железа живых СЗ. Снимается один раз на boot (железо меняют выключив машину) и
/// по одной СЗ за раз: слот синхронного exec у агента один и нужен оператору. «Занят», таймаут —
/// повтор не раньше <see cref="RetryAfter"/>. Читается и с потоков MCP (соседи) — под локом.</summary>
public sealed class HwProfileCache(IHubApiClient api, TimeProvider time)
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset? Boot, HwProfile Profile)> _profiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAt = new(StringComparer.Ordinal);
    private IReadOnlyList<SessionInfo> _live = Array.Empty<SessionInfo>();
    private bool _fetching;

    /// <summary>Список hub на последний удачный опрос.</summary>
    public IReadOnlyList<SessionInfo> Live
    {
        get { lock (_gate) return _live; }
    }

    /// <summary>Профиль прежнего boot остаётся, пока не снят новый: лучше старая строка, чем пустая.</summary>
    public HwProfile? Get(string sz)
    {
        lock (_gate) return _profiles.TryGetValue(sz, out var p) ? p.Profile : null;
    }

    /// <summary>Из UI-потока, на каждый удачный опрос hub. Возвращает снятие, если оно началось (тесты ждут).</summary>
    public Task Update(IReadOnlyList<SessionInfo> sessions)
    {
        SessionInfo? next;
        lock (_gate)
        {
            _live = sessions;
            if (_fetching) return Task.CompletedTask;
            var now = time.GetUtcNow();
            next = sessions.FirstOrDefault(s => s.Status == SessionStatus.Online && NeedsLocked(s, now));
            if (next is null) return Task.CompletedTask;
            _fetching = true;
        }
        return FetchAsync(next);
    }

    private bool NeedsLocked(SessionInfo s, DateTimeOffset now)
        => (!_profiles.TryGetValue(s.Sz, out var p) || p.Boot != s.BootTime)
           && (!_retryAt.TryGetValue(s.Sz, out var at) || now >= at);

    private async Task FetchAsync(SessionInfo s)
    {
        HwProfile? profile = null;
        try
        {
            var r = await api.ExecAsync(s.Sz, HwProfile.Script, 30).ConfigureAwait(false);
            if (r is { ExitCode: 0, TimedOut: false }) profile = HwProfile.Parse(CliXml.Decode(r.StdOut));
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or TaskCanceledException)
        {
            // Под нагрузкой exec глохнет — это не повод долбить агента каждые 2 с.
        }
        finally
        {
            lock (_gate)
            {
                if (profile is null) _retryAt[s.Sz] = time.GetUtcNow() + RetryAfter;
                else
                {
                    _profiles[s.Sz] = (s.BootTime, profile);
                    _retryAt.Remove(s.Sz);
                }
                _fetching = false;
            }
        }
    }
}
