namespace SzDiag.Claude;

public enum SessionState { Stopped, Idle, Working, WaitingPermission, AnsweringPeer, Crashed, Archived }

/// <summary>Живой вопрос соседу не получил ответа: причина уходит спросившему Claude текстом.</summary>
public sealed class PeerAnswerException(string message) : Exception(message);

public sealed record SessionTimeouts(TimeSpan StopGrace, TimeSpan InterruptWait)
{
    public static SessionTimeouts Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
}

/// <param name="LaunchFor">Запись реестра (ключ, session_id для --resume, профиль) → параметры
/// запуска; null — claude не найден.</param>
public sealed record SessionDeps(
    SessionIndex Index,
    TranscriptStore Transcripts,
    TokenLedger Tokens,
    PermissionBroker Broker,
    Func<SessionRecord, ClaudeLaunch?> LaunchFor,
    Func<IClaudeProcess> ProcessFactory,
    TimeProvider Time,
    SessionTimeouts Timeouts,
    Action<string>? Log = null,
    LimitsLedger? Limits = null);

/// <summary>Одна сессия Claude (спека 2026-09-25: 1 СЗ = 1 сессия). Процесс поднимается при первом
/// сообщении, а не при открытии чата: до первого сообщения `claude -p` ничего не делает, а
/// поднятый впустую — это хуки SessionStart и память. Очередь сообщений — у сессии, не у `claude`:
/// так «■» может вернуть неотправленное в поле ввода.</summary>
public sealed class ClaudeSession
{
    private readonly object _gate = new();
    private readonly SessionDeps _d;
    private readonly List<ClaudeEvent> _history;
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _permissions = new(StringComparer.Ordinal);
    private IClaudeProcess? _process;
    private bool _stopping;

    /// <summary>total_cost_usd последнего result текущего процесса: он накопительный за жизнь
    /// процесса (живой прогон: 0.136 → 0.289 → 0.441), а в счётчик дня идёт только прирост.</summary>
    private decimal _processCost;
    private bool _archiving;
    private TaskCompletionSource<bool>? _interrupt;
    private Action<ClaudeEvent>? _listener;

    private sealed record PeerAsk(string From, string Question, TaskCompletionSource<string> Answer);

    /// <summary>Вопросы соседей — отдельной очередью и впереди очереди оператора: спрашивающий
    /// ждёт ограниченное время (решение плана части 4).</summary>
    private readonly List<PeerAsk> _peerQueue = new();
    private PeerAsk? _answering;
    private string? _lastText;

    internal ClaudeSession(string key, SessionDeps deps)
    {
        Key = key;
        _d = deps;
        _history = deps.Transcripts.Load(key).Where(e => e is not (ServiceEvent or ParseError)).ToList();
        State = deps.Index.Get(key)?.Archived == true ? SessionState.Archived : SessionState.Stopped;
    }

    public string Key { get; }

    public SessionState State { get; private set; }

    /// <summary>Токены этой сессии с запуска Desk.</summary>
    public TokenUsage Usage { get; private set; } = TokenUsage.Zero;

    public string? SessionId => _d.Index.Get(Key)?.SessionId;

    /// <summary>Профиль Claude, закреплённый за сессией; null — по умолчанию.</summary>
    public string? Profile => _d.Index.Get(Key)?.Profile;

    public IReadOnlyList<ClaudeEvent> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public IReadOnlyList<string> Queued
    {
        get { lock (_gate) return _queue.ToList(); }
    }

    public bool IsAnsweringPeer
    {
        get { lock (_gate) return _answering is not null; }
    }

    public int PeerQueued
    {
        get { lock (_gate) return _peerQueue.Count; }
    }

    internal static string PeerPrompt(string fromKey, string question) =>
        $"[вопрос от сессии {fromKey}] {question}\n\n" +
        "Это вопрос соседней сессии Desk, не оператора. Ответь по своей СЗ коротко и фактами: " +
        "весь финальный текст этого хода уйдёт спросившему. ask_peer в этом ходе недоступен.";

    /// <summary>Живой вопрос соседа: встаёт в очередь, уходит в stdin, когда сессия свободна.
    /// Ответ — финальный текст хода. Отмена снимает вопрос, пока он не отправлен.</summary>
    internal Task<string> AskAsync(string fromKey, string question, CancellationToken ct)
    {
        var ask = new PeerAsk(fromKey, question, new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            if (State == SessionState.Archived) throw new PeerAnswerException("сессия соседа в архиве — СЗ закрыта");
            if (State == SessionState.Crashed) throw new PeerAnswerException("сессия соседа упала — оператор её ещё не перезапустил");
            // Процесс не поднимаем ради соседа: остановленная сессия может быть открыта в
            // терминале (два писателя в один разговор), а в окне остановки/архива новый процесс
            // остался бы сиротой за «архивной» сессией (ревью части 4, I-1 и I-3).
            if (_stopping || _archiving) throw new PeerAnswerException("сессия соседа останавливается — спроси без live (выжимка kb)");
            if (_process is not { IsRunning: true })
                throw new PeerAnswerException("сессия соседа не запущена (остановлена или открыта в терминале) — спроси без live (выжимка kb)");
            _peerQueue.Add(ask);
        }
        var reg = ct.Register(() =>
        {
            lock (_gate) _peerQueue.Remove(ask);
            ask.Answer.TrySetCanceled(ct);
            Changed?.Invoke();
        });
        ask.Answer.Task.ContinueWith(_ => reg.Dispose(), TaskScheduler.Default);
        Changed?.Invoke();
        _ = PumpQueueAsync();
        return ask.Answer.Task;
    }

    private void FailPeersLocked(string reason)
    {
        _answering?.Answer.TrySetException(new PeerAnswerException(reason));
        _answering = null;
        foreach (var a in _peerQueue) a.Answer.TrySetException(new PeerAnswerException(reason));
        _peerQueue.Clear();
    }

    /// <summary>Состояние или очередь изменились. Может прийти с потока процесса — UI маршалит сам.</summary>
    public event Action? Changed;

    /// <summary>Ход прерван «■» или процесс остановлен/упал: живые вопросы, которые эта сессия
    /// задала соседям, больше некому ждать (ревью части 4, I-2). Вызывается вне лока сессии.</summary>
    public event Action? Halted;

    /// <summary>История на момент подписки + всё, что придёт после, без дыр между ними.
    /// Слушатель вызывается под локом сессии: он должен только поставить работу в очередь UI.</summary>
    public IReadOnlyList<ClaudeEvent> Attach(Action<ClaudeEvent> listener)
    {
        lock (_gate)
        {
            _listener += listener;
            return _history.ToList();
        }
    }

    public void Detach(Action<ClaudeEvent> listener)
    {
        lock (_gate) _listener -= listener;
    }

    public Task SendAsync(string text)
    {
        lock (_gate)
        {
            if (State == SessionState.Archived) SetArchivedLocked(false);
            _queue.Enqueue(text);
        }
        Changed?.Invoke();
        return PumpQueueAsync();
    }

    /// <summary>Отправить следующее из очереди, если сессия свободна.</summary>
    private async Task PumpQueueAsync()
    {
        IClaudeProcess? process = null;
        var text = "";
        bool send;
        lock (_gate)
        {
            send = (_peerQueue.Count > 0 || _queue.Count > 0) && _interrupt is null
                   && State is SessionState.Stopped or SessionState.Idle
                   && (_process is { IsRunning: true } || TryStartLocked());
            if (send)
            {
                process = _process!;
                _lastText = null;
                if (_peerQueue.Count > 0)
                {
                    var ask = _peerQueue[0];
                    _peerQueue.RemoveAt(0);
                    _answering = ask;
                    text = PeerPrompt(ask.From, ask.Question);
                    AddLocked(new PeerQuestion(ask.From, ask.Question) { At = _d.Time.GetUtcNow() });
                    State = SessionState.AnsweringPeer;
                }
                else
                {
                    text = _queue.Dequeue();
                    AddLocked(new DeskUserMessage(text) { At = _d.Time.GetUtcNow() });
                    State = SessionState.Working;
                }
            }
        }
        Changed?.Invoke();
        if (!send) return;

        try
        {
            await process!.WriteLineAsync(ClaudeInput.UserMessage(text)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            lock (_gate) CrashLocked(null, new[] { $"запись в claude не удалась: {ex.Message}" });
            Changed?.Invoke();
        }
    }

    private bool TryStartLocked()
    {
        var launch = _d.LaunchFor(_d.Index.Get(Key) ?? new SessionRecord(Key, null, _d.Time.GetUtcNow(), false));
        if (launch is null)
        {
            CrashLocked(null, new[] { "claude не найден: укажи ClaudePath в appsettings.json Desk или добавь claude.exe в PATH" });
            return false;
        }
        var p = _d.ProcessFactory();
        p.OutputLine += OnLine;
        p.Exited += code => OnExited(p, code);
        try
        {
            p.Start(launch);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            CrashLocked(null, new[] { $"claude не запустился: {ex.Message}" });
            return false;
        }
        _process = p;
        _stopping = false;
        _processCost = 0m;
        return true;
    }

    private void OnLine(string line)
    {
        var events = StreamJsonParser.Parse(line, _d.Time.GetUtcNow());
        // Служебные строки (хуки SessionStart — десятки КБ на запуск) в журнал не пишем.
        if (events.Any(e => e is not (ServiceEvent or ParseError or RateLimitUpdate))) _d.Transcripts.Append(Key, line);

        var turnEnded = false;
        lock (_gate)
        {
            foreach (var e in events)
            {
                switch (e)
                {
                    case ServiceEvent:
                        continue;
                    case RateLimitUpdate rl:
                        // Лимиты — на аккаунт профиля; в ленту не идут.
                        _d.Limits?.Update(Profile ?? "claude", rl.Info);
                        continue;
                    case ParseError pe:
                        _d.Log?.Invoke($"сессия {Key}: битая строка stream-json пропущена ({pe.Message})");
                        continue;
                    case SystemInit init:
                        RememberSessionIdLocked(init.SessionId);
                        break;
                    case AssistantText { ParentToolUseId: null } t:
                        _lastText = t.Text;
                        break;
                    case TurnResult r:
                        Usage = Usage.Add(r.Usage);
                        _d.Tokens.Add(r.Usage, Math.Max(0m, r.CostUsd - _processCost));
                        _processCost = Math.Max(_processCost, r.CostUsd);
                        if (_answering is { } a)
                        {
                            if (r.Interrupted) a.Answer.TrySetException(new PeerAnswerException("сосед прервал ход — ответа нет"));
                            else if (r.IsError) a.Answer.TrySetException(new PeerAnswerException($"ход соседа завершился ошибкой: {r.Text}"));
                            else a.Answer.TrySetResult(r.Text is { Length: > 0 } rt ? rt : _lastText ?? "");
                            _answering = null;
                        }
                        State = SessionState.Idle;
                        turnEnded = true;
                        break;
                }
                AddLocked(e);
            }
            if (turnEnded) _interrupt?.TrySetResult(true);
        }
        Changed?.Invoke();
        if (turnEnded) _ = PumpQueueAsync();
    }

    private void OnExited(IClaudeProcess p, int? code)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(p, _process)) return;
            _process = null;
            FailPeersLocked(_stopping ? "сессия соседа остановлена" : "сессия соседа упала");
            if (_stopping)
            {
                if (State != SessionState.Archived) State = SessionState.Stopped;
            }
            else
            {
                CrashLocked(code, p.StderrTail.TakeLast(20).ToList());
            }
            _interrupt?.TrySetResult(false);
        }
        _d.Broker.DenyAll(Key, "сессия остановлена");
        Halted?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>«■»: прервать текущий ход. Возвращает неотправленную очередь — её место в поле ввода.
    /// Если `claude` не закрыл ход за <see cref="SessionTimeouts.InterruptWait"/> (инструмент
    /// завис), процесс останавливается: окно не должно висеть вместе с ним.</summary>
    public async Task<IReadOnlyList<string>> InterruptAsync()
    {
        IClaudeProcess? process = null;
        TaskCompletionSource<bool>? done = null;
        List<string> back;
        bool active;
        lock (_gate)
        {
            back = _queue.ToList();
            _queue.Clear();
            active = _process is not null && State is SessionState.Working or SessionState.WaitingPermission or SessionState.AnsweringPeer;
            if (active)
            {
                process = _process;
                done = _interrupt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        Changed?.Invoke();
        if (!active) return back;
        Halted?.Invoke();

        // Висящий запрос разрешения держит ход — сначала отказ, потом interrupt.
        _d.Broker.DenyAll(Key, "ход прерван оператором");
        try
        {
            await process!.WriteLineAsync(ClaudeInput.Interrupt($"int-{Guid.NewGuid():N}")).ConfigureAwait(false);
            var wait = _d.Timeouts.InterruptWait;
            if (await Task.WhenAny(done!.Task, Task.Delay(wait)).ConfigureAwait(false) != done.Task)
            {
                Note($"ход не остановился за {wait.TotalSeconds:N0} с — процесс остановлен, разговор продолжится через --resume");
                await StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _interrupt = null;
        }
        // Пока шёл interrupt, очередь стояла (PumpQueueAsync ждёт _interrupt == null): то, что
        // оператор отправил после «■», уходит сейчас, а не при следующей отправке.
        _ = PumpQueueAsync();
        return back;
    }

    /// <summary>Штатная остановка процесса (закрытие Desk, архив, «открыть в терминале»). Не падение.</summary>
    public async Task StopAsync()
    {
        IClaudeProcess? p;
        lock (_gate)
        {
            p = _process;
            _stopping = true;
        }
        _d.Broker.DenyAll(Key, "сессия остановлена");
        Halted?.Invoke();
        if (p is not null) await p.StopAsync(_d.Timeouts.StopGrace).ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_process, p)) _process = null;
            FailPeersLocked("сессия соседа остановлена");
            if (State != SessionState.Archived) State = SessionState.Stopped;
        }
        Changed?.Invoke();
    }

    /// <summary>СЗ закрыта: процесс останавливается, сессия читается как архив; новое сообщение
    /// продолжит её через --resume.</summary>
    public async Task ArchiveAsync()
    {
        lock (_gate)
        {
            if (_archiving || State == SessionState.Archived) return;
            _archiving = true;
        }
        try
        {
            await StopAsync().ConfigureAwait(false);
            lock (_gate)
            {
                SetArchivedLocked(true);
                AddLocked(new DeskNote("СЗ закрыта — сессия в архиве; новое сообщение продолжит её через --resume")
                    { At = _d.Time.GetUtcNow() });
            }
            Changed?.Invoke();
        }
        finally
        {
            lock (_gate) _archiving = false;
        }
    }

    /// <summary>Перед «открыть в терминале»: двух писателей в одну сессию быть не должно.</summary>
    public async Task DetachAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Note("сессия открыта в терминале; пока он открыт, здесь не пиши — у разговора будет два писателя");
    }

    /// <summary>После падения: снять «упала» и отправить то, что ждёт в очереди.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (State == SessionState.Crashed) State = SessionState.Stopped;
        }
        Changed?.Invoke();
        _ = PumpQueueAsync();
    }

    public void Note(string text)
    {
        lock (_gate) AddLocked(new DeskNote(text) { At = _d.Time.GetUtcNow() });
        Changed?.Invoke();
    }

    internal void OnPermissionAsked(PendingPermission p)
    {
        lock (_gate)
        {
            _permissions.Add(p.RequestId);
            AddLocked(new PermissionAsked(p.RequestId, p.ToolName, p.Input, p.ToolUseId) { At = p.At });
            if (State is SessionState.Working or SessionState.AnsweringPeer) State = SessionState.WaitingPermission;
        }
        Changed?.Invoke();
    }

    internal void OnPermissionAnswered(string requestId, bool allowed)
    {
        lock (_gate)
        {
            if (!_permissions.Remove(requestId)) return;
            AddLocked(new PermissionAnswered(requestId, allowed) { At = _d.Time.GetUtcNow() });
            if (State == SessionState.WaitingPermission && _permissions.Count == 0)
                State = _answering is null ? SessionState.Working : SessionState.AnsweringPeer;
        }
        Changed?.Invoke();
    }

    private void AddLocked(ClaudeEvent e)
    {
        _history.Add(e);
        if (DeskLines.Serialize(e) is { } line) _d.Transcripts.Append(Key, line);
        _listener?.Invoke(e);
    }

    private void CrashLocked(int? code, IReadOnlyList<string> tail)
    {
        FailPeersLocked("сессия соседа упала");
        AddLocked(new ProcessCrashed(code, tail) { At = _d.Time.GetUtcNow() });
        State = SessionState.Crashed;
    }

    private void RememberSessionIdLocked(string sessionId)
    {
        if (sessionId.Length == 0) return;
        var r = _d.Index.Get(Key);
        if (r?.SessionId is not null) return;   // при --resume id не меняется (спайк)
        _d.Index.Put((r ?? new SessionRecord(Key, null, _d.Time.GetUtcNow(), false)) with { SessionId = sessionId });
    }

    private void SetArchivedLocked(bool archived)
    {
        var r = _d.Index.Get(Key) ?? new SessionRecord(Key, null, _d.Time.GetUtcNow(), false);
        _d.Index.Put(r with { Archived = archived });
        State = archived ? SessionState.Archived : SessionState.Stopped;
    }
}
