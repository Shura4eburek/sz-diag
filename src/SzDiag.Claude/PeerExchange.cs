namespace SzDiag.Claude;

/// <summary>Сведения о соседях — извне: ядро не знает, что ключ — это СЗ, где kb и какое у машины железо.</summary>
public interface IPeerDirectory
{
    /// <summary>Другие активные сессии (без спрашивающего): профиль одной строкой и похожесть на него.</summary>
    IReadOnlyList<PeerInfo> Peers(string askerKey);

    /// <summary>Выжимка того, что по ключу уже записано (без токенов, без отвлечения соседа); null — ничего нет.</summary>
    string? Summary(string key);
}

/// <param name="Similar">Чем похож на спрашивающего; null — ничем.</param>
public sealed record PeerInfo(string Key, string Profile, string? Similar);

public sealed record PeerLimits(int MaxLivePerHour, TimeSpan Timeout)
{
    public static PeerLimits Default { get; } = new(20, TimeSpan.FromMinutes(5));
}

public sealed record PeerReply(bool Ok, string Text)
{
    public static PeerReply Fail(string why) => new(false, why);
}

/// <summary>Правила обмена между сессиями (спека, «ask_peer»): выжимка — даром; живой вопрос —
/// глубина 1, лимит в час, таймаут; встречный вопрос запрещён — иначе обе сессии висели бы до
/// таймаута, ожидая друг друга.</summary>
public sealed class PeerExchange(SessionManager sessions, IPeerDirectory directory, PeerLimits limits, TimeProvider time)
{
    public const string LiveHint = "Если этого мало — ask_peer с live: true: вопрос уйдёт сессии соседа.";

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _asked = new(StringComparer.Ordinal);
    private readonly List<(string From, string To)> _active = new();

    /// <summary>Пары «кто кого сейчас спрашивает» — фиолетовая метка в списке СЗ.</summary>
    public IReadOnlyList<(string From, string To)> Active
    {
        get { lock (_gate) return _active.ToList(); }
    }

    /// <summary>Пара появилась или ушла. Может прийти с любого потока.</summary>
    public event Action? Changed;

    public string ListPeers(string asker)
    {
        var peers = directory.Peers(asker);
        return peers.Count == 0
            ? "других активных сессий Desk нет"
            : string.Join("\n", peers.Select(p => p.Similar is null ? $"{p.Key} · {p.Profile}" : $"{p.Key} · {p.Profile} · похоже: {p.Similar}"));
    }

    public async Task<PeerReply> AskAsync(string asker, string key, string question, bool live, CancellationToken ct)
    {
        if (key == asker) return PeerReply.Fail("это твоя собственная сессия");
        if (!live)
            return directory.Summary(key) is { } summary
                ? new PeerReply(true, $"{summary}\n\n{LiveHint}")
                : PeerReply.Fail($"по {key} в kb ничего не записано — спроси живьём (live: true), если у него есть сессия");

        if (sessions.Peek(asker) is { IsAnsweringPeer: true })
            return PeerReply.Fail("глубина 1: ты сейчас сам отвечаешь соседу — ask_peer в этом ходе недоступен");
        if (directory.Peers(asker).All(p => p.Key != key) || sessions.Get(key) is not { } target)
            return PeerReply.Fail($"у {key} нет активной сессии Desk — спроси без live (выжимка kb)");

        lock (_gate)
        {
            if (_active.Any(a => a.From == key))
                return PeerReply.Fail($"сессия {key} сама ждёт ответа соседа — встречный вопрос повесил бы обе");
            if (_active.Any(a => a.From == asker))
                return PeerReply.Fail("у тебя уже есть вопрос соседу без ответа — дождись его");
            var now = time.GetUtcNow();
            if (!_asked.TryGetValue(asker, out var times)) _asked[asker] = times = new Queue<DateTimeOffset>();
            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1)) times.Dequeue();
            if (times.Count >= limits.MaxLivePerHour)
            {
                var wait = (int)Math.Ceiling((times.Peek().AddHours(1) - now).TotalMinutes);
                return PeerReply.Fail($"лимит {limits.MaxLivePerHour} живых вопросов в час исчерпан — следующий через {wait} мин");
            }
            times.Enqueue(now);
            _active.Add((asker, key));
        }
        Changed?.Invoke();

        using var timeout = new CancellationTokenSource(limits.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return new PeerReply(true, await target.AskAsync(asker, question, linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return PeerReply.Fail($"сосед {key} не ответил за {limits.Timeout.TotalMinutes:0.#} мин — вопрос снят");
        }
        catch (OperationCanceledException)
        {
            return PeerReply.Fail("вопрос отменён");
        }
        catch (PeerAnswerException e)
        {
            return PeerReply.Fail(e.Message);
        }
        finally
        {
            lock (_gate) _active.Remove((asker, key));
            Changed?.Invoke();
        }
    }
}
