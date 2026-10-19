# SzDiag Desk, часть 4 — `ask_peer`, `peers()` и похожие СЗ (`≈`) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Сессии Claude разных СЗ могут спрашивать друг друга. `peers()` показывает, какие СЗ ещё в работе и чем их железо похоже на твоё. `ask_peer(key, question)` отдаёт выжимку kb соседа: это бесплатно и не отвлекает его. `ask_peer(..., live: true)` задаёт вопрос живой сессии соседа и возвращает её ответ. В списке СЗ появляются строка железа (CPU · плата · память), бейдж `≈<СЗ>` для похожей машины в работе и фиолетовая метка `💬<СЗ>` у пары, которая сейчас переписывается.

**Architecture:** В ядре `SzDiag.Claude` у `ClaudeSession` появляются входящие вопросы соседей. Они стоят в своей очереди, раньше очереди оператора. Есть новое состояние `AnsweringPeer`. Ответом считается финальный текст хода. `PeerExchange` держит правила обмена: глубина 1, лимит в час, таймаут, запрет встречного вопроса, отмена. Данные о СЗ ядро получает через `IPeerDirectory`. `DeskMcpServer` добавляет инструменты `peers` и `ask_peer`. В Desk `HwProfileCache` снимает короткий профиль железа синхронным exec: один раз на boot и по одной СЗ за раз. `SzPeerDirectory` собирает список соседей и выжимку kb. `MainViewModel` рисует строку железа и бейджи `≈` и `💬`.

**Tech Stack:** .NET 8, ModelContextProtocol.AspNetCore 2.2.0, Avalonia 11.3.22, CommunityToolkit.Mvvm 8.4.2, xunit 2.5.3.

**Spec:** [docs/superpowers/specs/2026-09-25-desk-gui-design.md](../specs/2026-09-25-desk-gui-design.md) — разделы «`ask_peer`», «Похожие СЗ», «Ошибки», этап 6. Части 1–3 уже в ветке `feat/cloudflare-tunnel-access`: [1](2026-09-25-desk-part1-window.md), [2](2026-09-25-desk-part2-sessions.md), [3](2026-09-25-desk-part3-inspector.md).

**Решения плана поверх спеки** (задача 7 вносит их в спеку):
- **Похожесть определяется не по `hw passport`.** `szcli hw passport` снимает только видеокарту (`GpuPassport.ScriptFor` знает одну область, `gpu`): CPU, платы и памяти в нём нет. Поэтому Desk снимает **свой короткий профиль** (CPU, плата, число планок / объём / частота / партномер) синхронным exec. Профиль снимается один раз на `BootTime`: чтобы поменять железо, машину выключают, и boot меняется. За раз снимается одна СЗ. После «агент занят» или таймаута повтор не раньше чем через 5 минут: единственный слот синхронного exec у агента нужен оператору.
- **Память похожа по профилю, а не только по частоте.** Совпадать должны число планок, объём и частота (`2×32 ГБ @ 6000`). Одна частота 6000 встречается почти в каждой сборке, и бейдж загорался бы на всех. Совпадение профиля — это как раз почерк 159873/160176/161432 из CLAUDE.md.
- **Вопрос соседа идёт впереди очереди оператора.** Спрашивающий ждёт не больше 5 минут, а оператор, поставивший три сообщения в очередь, отнял бы у него всё время.
- **Встречный вопрос запрещён.** Если сессия B сама ждёт ответа, живой вопрос к ней отклоняется. Иначе A ждёт B, B ждёт A, и обе висят до таймаута. У одного спрашивающего одновременно не больше одного живого вопроса.
- **Выжимка kb доступна по любой СЗ, у которой есть папка в kb**, в том числе закрытой: прошлая похожая заявка — ровно то, что стоит спросить. Живой вопрос возможен только к активной сессии Desk. Номер СЗ проверяется на `^\d{6}$`: ключ приходит от Claude и превращается в путь на диске.
- **`peers` и `ask_peer` разрешены заранее** (`--allowedTools`). Они ничего не меняют ни на клиенте, ни в kb, а живые вопросы ограничены лимитами. Карточка разрешения на каждый вопрос соседу была бы шумом.

## Global Constraints

- Целевой фреймворк — **net8.0**; файлы `*.csproj`/`*.ps1` — **UTF-8 с BOM**.
- Комментарии и пользовательский текст — **на русском**, комментарий объясняет «почему».
- `SzDiag.Claude` не знает про СЗ: ключ сессии — строка, всё про СЗ (kb, железо, живость) приходит через `IPeerDirectory` из Desk.
- Лимиты `ask_peer` — из конфига Desk: `PeerLivePerHour` = **20**, `PeerTimeoutMinutes` = **5**; глубина — **1**.
- Ошибка `ask_peer` — **ошибка инструмента** (`CallToolResult.IsError = true`) с причиной текстом, а не исключение протокола.
- Синхронный exec к клиенту ради профиля — **один раз на `BootTime`**, по одной СЗ за раз, повтор после неудачи — не раньше 5 минут.
- Признаки живости — только `LastHeartbeat`/`BootTime`.
- Тема — токены `Tokens.axaml` (фиолетовый — `Violet`); новые цвета не заводить.
- Правки со слешами — инструментом Write/Edit, **не** bash-heredoc.
- `dotnet` из PATH бокса — только рантайм 10: тесты гонять через `"C:\Program Files\dotnet\dotnet.exe"`.

## Review Focus

- **Встречный вопрос A→B и B→A**: обе сессии не должны висеть 5 минут. Второй вопрос отклоняется сразу с причиной. → `PeerExchangeTests.CounterQuestion_Rejected` (задача 2).
- **Спрашивающего прервали «■», пока вопрос стоит в очереди соседа**: сосед не должен потом отвечать в пустоту и тратить на это токены. → `ClaudeSessionPeerTests.Cancelled_BeforeSent_RemovedFromQueue` (задача 1), `PeerExchangeTests.Timeout_FailsAndDropsQueuedQuestion` (задача 2).
- **Сосед упал или его остановили посреди ответа**: спрашивающий не должен ждать до таймаута. → `ClaudeSessionPeerTests.ProcessExit_FailsPendingAnswer` (задача 1).
- **Ключ от Claude вида `..\..\secrets`**: выжимка kb не должна читать файлы вне `kb/СЗ/<6 цифр>/`. → `SzPeerDirectoryTests.Summary_RejectsNonSzKey` (задача 5).
- **Агент занят или под нагрузкой**: снятие профиля не должно каждые 2 с опроса дёргать единственный слот синхронного exec. → `HwProfileCacheTests.Busy_NotRetriedFor5Minutes` (задача 4).

---

## Карта файлов

**Новое:**
- `src/SzDiag.Claude/PeerExchange.cs` (`IPeerDirectory`, `PeerInfo`, `PeerLimits`, `PeerReply`, `PeerAnswerException`, `PeerExchange`)
- `tests/SzDiag.Claude.Tests/ClaudeSessionPeerTests.cs`, `PeerExchangeTests.cs`, `FakePeerDirectory.cs`, `DeskMcpPeerToolsTests.cs`
- `src/SzDiag.Desk/Services/HwProfile.cs`, `HwProfileCache.cs`, `SzPeerDirectory.cs`
- `tests/SzDiag.Desk.Tests/HwProfileTests.cs`, `HwProfileCacheTests.cs`, `SzPeerDirectoryTests.cs`, `MainViewModelPeerTests.cs`, `FakePeerDirectory.cs`
- `docs/live-checklist-2026-09-25-desk-part4.md`

**Изменения:** `src/SzDiag.Claude/{ClaudeEvents,ClaudeSession,DeskLines,DeskMcpServer,ClaudeLaunch}.cs`, `tests/SzDiag.Claude.Tests/ClaudeLaunchTests.cs`, `src/SzDiag.Desk/Services/{DeskClaudeHost,DeskOptions,SzBriefing,ChatServices}.cs`, `src/SzDiag.Desk/App.axaml.cs`, `src/SzDiag.Desk/ViewModels/{MainViewModel,SzItemViewModel,ChatViewModel,FeedBuilder,FeedItems,ToolSummary}.cs`, `src/SzDiag.Desk/Views/{SzListView,ChatView}.axaml`, `src/SzDiag.Desk/Views/Converters.cs`, `tests/SzDiag.Desk.Tests/{ChatHarness,FeedBuilderTests}.cs`, спека, `CLAUDE.md`, `docs/dev-knowledge-base.md`.

---

### Task 1: Ядро — вопрос соседа в `ClaudeSession`

**Files:**
- Modify: `src/SzDiag.Claude/ClaudeEvents.cs`, `src/SzDiag.Claude/ClaudeSession.cs`, `src/SzDiag.Claude/DeskLines.cs`
- Create: `tests/SzDiag.Claude.Tests/ClaudeSessionPeerTests.cs`

**Interfaces:**
- Produces: `SessionState.AnsweringPeer` (между `WaitingPermission` и `Crashed`); `record PeerQuestion(string FromKey, string Text) : ClaudeEvent` (журнал — `desk_peer_question`); `class PeerAnswerException(string message) : Exception`; у `ClaudeSession`: `internal Task<string> AskAsync(string fromKey, string question, CancellationToken ct)`, `public bool IsAnsweringPeer`, `public int PeerQueued`, `internal static string PeerPrompt(string fromKey, string question)`.

- [ ] **Step 1: Write the failing tests**

`tests/SzDiag.Claude.Tests/ClaudeSessionPeerTests.cs`:

```csharp
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class ClaudeSessionPeerTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text => Fixture.Line("simple-turn.jsonl", e => e is AssistantText);
    private static string Result => Fixture.Line("simple-turn.jsonl", e => e is TurnResult);
    private static string Aborted => Fixture.Line("interrupt-turn.jsonl", e => e is TurnResult { Interrupted: true });
    private static string ResultText => Fixture.Events("simple-turn.jsonl").OfType<TurnResult>().First().Text!;

    private static string UserText(string line)
        => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString()!;

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Idle_SendsPrefixedQuestion_AnswerIsTurnResultText()
    {
        var s = _h.Manager.Create("161501");
        var answer = s.AskAsync("161432", "какой BIOS на плате?", default);

        Assert.Equal(SessionState.AnsweringPeer, s.State);
        Assert.True(s.IsAnsweringPeer);
        Assert.StartsWith("[вопрос от сессии 161432] какой BIOS на плате?", UserText(Assert.Single(_h.Last.Written)));
        var q = Assert.IsType<PeerQuestion>(Assert.Single(s.History));
        Assert.Equal("161432", q.FromKey);

        _h.Last.Emit(Text);
        _h.Last.Emit(Result);
        Assert.Equal(ResultText, await answer);
        Assert.Equal(SessionState.Idle, s.State);
        Assert.False(s.IsAnsweringPeer);
    }

    [Fact]
    public async Task Working_WaitsForTurnEnd_AheadOfOperatorQueue()
    {
        // Решение плана: спрашивающий ждёт не больше 5 минут — очередь оператора его не задерживает.
        var s = _h.Manager.Create("161501");
        await s.SendAsync("первое");
        await s.SendAsync("второе");
        var answer = s.AskAsync("161432", "вопрос", default);
        Assert.Single(_h.Last.Written);
        Assert.Equal(1, s.PeerQueued);

        _h.Last.Emit(Result);
        await WaitUntil(() => _h.Last.Written.Count == 2);
        Assert.StartsWith("[вопрос от сессии 161432]", UserText(_h.Last.Written[1]));
        Assert.Equal(new[] { "второе" }, s.Queued);   // очередь оператора не тронута и в поле ввода не уйдёт
        Assert.False(answer.IsCompleted);
    }

    [Fact]
    public async Task Interrupted_Fails()
    {
        var s = _h.Manager.Create("161501");
        var answer = s.AskAsync("161432", "вопрос", default);
        _h.Last.Emit(Aborted);
        var ex = await Assert.ThrowsAsync<PeerAnswerException>(() => answer);
        Assert.Contains("прервал", ex.Message);
    }

    [Fact]
    public async Task ProcessExit_FailsPendingAnswer()
    {
        var s = _h.Manager.Create("161501");
        var answer = s.AskAsync("161432", "вопрос", default);
        _h.Last.Exit(1);
        await Assert.ThrowsAsync<PeerAnswerException>(() => answer);
    }

    [Fact]
    public async Task Stop_FailsQueuedQuestions()
    {
        var s = _h.Manager.Create("161501");
        await s.SendAsync("первое");
        var answer = s.AskAsync("161432", "вопрос", default);
        await s.StopAsync();
        await Assert.ThrowsAsync<PeerAnswerException>(() => answer);
        Assert.Equal(0, s.PeerQueued);
    }

    [Fact]
    public async Task Cancelled_BeforeSent_RemovedFromQueue()
    {
        var s = _h.Manager.Create("161501");
        await s.SendAsync("первое");
        using var cts = new CancellationTokenSource();
        var answer = s.AskAsync("161432", "вопрос", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);

        _h.Last.Emit(Result);
        await Task.Delay(100);
        Assert.Single(_h.Last.Written);   // сосед не отвечает в пустоту
        Assert.Equal(0, s.PeerQueued);
    }

    [Fact]
    public async Task PermissionDuringAnswer_ReturnsToAnsweringPeer()
    {
        var s = _h.Manager.Create("161501");
        _ = s.AskAsync("161432", "вопрос", default);
        var perm = _h.Broker.AskAsync("161501", "Bash", JsonDocument.Parse("{}").RootElement, null, default);
        Assert.Equal(SessionState.WaitingPermission, s.State);

        _h.Broker.Resolve(_h.Broker.Pending("161501").Single().RequestId, true);
        await perm;
        Assert.Equal(SessionState.AnsweringPeer, s.State);
    }

    [Fact]
    public async Task Archived_Rejects()
    {
        var s = _h.Manager.Create("161501");
        await s.ArchiveAsync();
        var ex = await Assert.ThrowsAsync<PeerAnswerException>(() => s.AskAsync("161432", "вопрос", default));
        Assert.Contains("архив", ex.Message);
    }

    [Fact]
    public void PeerQuestion_SurvivesDeskRestart()
    {
        var s = _h.Manager.Create("161501");
        _ = s.AskAsync("161432", "вопрос", default);
        var again = _h.New().Get("161501")!;
        var q = Assert.IsType<PeerQuestion>(again.History.First());
        Assert.Equal(("161432", "вопрос"), (q.FromKey, q.Text));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter FullyQualifiedName~ClaudeSessionPeerTests`
Expected: FAIL — ошибки компиляции `AskAsync`/`PeerQuestion`/`AnsweringPeer`/`PeerAnswerException` не найдены.

- [ ] **Step 3: Implement**

`ClaudeEvents.cs` — в конец:

```csharp
/// <summary>Вопрос соседней сессии (`ask_peer` с live): в ленте — «💬 от &lt;ключ&gt;: …».</summary>
public sealed record PeerQuestion(string FromKey, string Text) : ClaudeEvent;
```

`DeskLines.cs` — в `Serialize`: `PeerQuestion q => new { type = "desk_peer_question", from = q.FromKey, text = q.Text, timestamp = at },`; в `Parse`: `"desk_peer_question" => new PeerQuestion(Json.Str(root, "from") ?? "", Json.Str(root, "text") ?? ""),`.

`ClaudeSession.cs`:

```csharp
public enum SessionState { Stopped, Idle, Working, WaitingPermission, AnsweringPeer, Crashed, Archived }

/// <summary>Живой вопрос соседу не получил ответа: причина уходит спросившему Claude текстом.</summary>
public sealed class PeerAnswerException(string message) : Exception(message);
```

Поля и члены класса:

```csharp
    private sealed record PeerAsk(string From, string Question, TaskCompletionSource<string> Answer);

    /// <summary>Вопросы соседей — отдельной очередью и впереди очереди оператора: спрашивающий
    /// ждёт ограниченное время (решение плана части 4).</summary>
    private readonly List<PeerAsk> _peerQueue = new();
    private PeerAsk? _answering;
    private string? _lastText;

    public bool IsAnsweringPeer { get { lock (_gate) return _answering is not null; } }

    public int PeerQueued { get { lock (_gate) return _peerQueue.Count; } }

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
```

`PumpQueueAsync` — выбор следующего (замена тела блока `lock`):

```csharp
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
```

`OnLine` — в `switch` добавить ветку и расширить `TurnResult`:

```csharp
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
```

`OnExited` — внутри `lock`, после `_process = null;`: `FailPeersLocked(_stopping ? "сессия соседа остановлена" : "сессия соседа упала");`.
`StopAsync` — во втором `lock` (после остановки процесса): `FailPeersLocked("сессия соседа остановлена");`.
`CrashLocked` — первой строкой: `FailPeersLocked("сессия соседа упала");`.
`InterruptAsync` — `active = _process is not null && State is SessionState.Working or SessionState.WaitingPermission or SessionState.AnsweringPeer;`.
`OnPermissionAsked` — `if (State is SessionState.Working or SessionState.AnsweringPeer) State = SessionState.WaitingPermission;`.
`OnPermissionAnswered` — `if (State == SessionState.WaitingPermission && _permissions.Count == 0) State = _answering is null ? SessionState.Working : SessionState.AnsweringPeer;`.

- [ ] **Step 4: Run to verify they pass**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: PASS, все тесты проекта (старые `ClaudeSessionTests` — без изменений).

- [ ] **Step 5: Commit**

```bash
git status --short
git add src/SzDiag.Claude/ClaudeEvents.cs src/SzDiag.Claude/ClaudeSession.cs src/SzDiag.Claude/DeskLines.cs tests/SzDiag.Claude.Tests/ClaudeSessionPeerTests.cs
git commit -m "feat(claude): вопрос соседней сессии — очередь, AnsweringPeer, ответ ходом"
```

---

### Task 2: Ядро — `PeerExchange`: выжимка, живой вопрос и лимиты

**Files:**
- Create: `src/SzDiag.Claude/PeerExchange.cs`, `tests/SzDiag.Claude.Tests/FakePeerDirectory.cs`, `tests/SzDiag.Claude.Tests/PeerExchangeTests.cs`

**Interfaces:**
- Consumes: `ClaudeSession.AskAsync/IsAnsweringPeer`, `PeerAnswerException` (задача 1); `SessionManager.Get/Peek`.
- Produces:
  - `interface IPeerDirectory { IReadOnlyList<PeerInfo> Peers(string askerKey); string? Summary(string key); }`
  - `record PeerInfo(string Key, string Profile, string? Similar)`
  - `record PeerLimits(int MaxLivePerHour, TimeSpan Timeout)` + `static PeerLimits Default` (20, 5 мин)
  - `record PeerReply(bool Ok, string Text)`
  - `class PeerExchange(SessionManager sessions, IPeerDirectory directory, PeerLimits limits, TimeProvider time)`: `string ListPeers(string asker)`, `Task<PeerReply> AskAsync(string asker, string key, string question, bool live, CancellationToken ct)`, `IReadOnlyList<(string From, string To)> Active`, `event Action? Changed`, `const string LiveHint`.

- [ ] **Step 1: Write the failing tests**

`tests/SzDiag.Claude.Tests/FakePeerDirectory.cs`:

```csharp
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

internal sealed class FakePeerDirectory : IPeerDirectory
{
    public List<PeerInfo> All { get; } = new();
    public Dictionary<string, string> Summaries { get; } = new();

    public IReadOnlyList<PeerInfo> Peers(string askerKey) => All.Where(p => p.Key != askerKey).ToList();
    public string? Summary(string key) => Summaries.GetValueOrDefault(key);
}
```

`tests/SzDiag.Claude.Tests/PeerExchangeTests.cs`:

```csharp
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class PeerExchangeTests : IDisposable
{
    private readonly SessionHarness _h = new();
    private readonly FakePeerDirectory _dir = new();

    public void Dispose() => _h.Dispose();

    private static string Result => Fixture.Line("simple-turn.jsonl", e => e is TurnResult);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset At = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => At;
    }

    private PeerExchange New(PeerLimits? limits = null, TimeProvider? time = null)
        => new(_h.Manager, _dir, limits ?? PeerLimits.Default, time ?? TimeProvider.System);

    private void Peer(string key) => _dir.All.Add(new PeerInfo(key, "Ryzen 7 9800X3D · online", null));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task NotLive_ReturnsSummaryWithHint_NoQuestionSent()
    {
        _dir.Summaries["161501"] = "СЗ 161501 · вырубоны на EXPO 6000";
        var r = await New().AskAsync("161432", "161501", "что нашли?", live: false, default);
        Assert.True(r.Ok);
        Assert.StartsWith("СЗ 161501 · вырубоны на EXPO 6000", r.Text);
        Assert.Contains("live", r.Text);
        Assert.Empty(_h.Processes);
    }

    [Fact]
    public async Task NotLive_NothingInKb_Fails()
    {
        var r = await New().AskAsync("161432", "161501", "что нашли?", live: false, default);
        Assert.False(r.Ok);
        Assert.Contains("kb", r.Text);
    }

    [Fact]
    public async Task Self_Rejected()
    {
        var r = await New().AskAsync("161432", "161432", "?", live: false, default);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Live_AnswerReturned_PairActiveWhileInFlight()
    {
        Peer("161501");
        _h.Manager.Create("161501");
        var ex = New();
        var changes = 0;
        ex.Changed += () => changes++;

        var reply = ex.AskAsync("161432", "161501", "какой BIOS?", live: true, default);
        await WaitUntil(() => _h.Processes.Count == 1);
        Assert.Equal(new[] { ("161432", "161501") }, ex.Active);

        _h.Last.Emit(Result);
        var r = await reply;
        Assert.True(r.Ok);
        Assert.Empty(ex.Active);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Live_NoActiveSession_Fails()
    {
        var r = await New().AskAsync("161432", "161501", "?", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("нет активной сессии", r.Text);
    }

    [Fact]
    public async Task Depth1_AnsweringAskerCannotAsk()
    {
        Peer("161501");
        Peer("161600");
        _h.Manager.Create("161600");
        var asker = _h.Manager.Create("161501");
        _ = asker.AskAsync("161600", "вопрос", default);   // 161501 сейчас отвечает соседу

        var r = await New().AskAsync("161501", "161600", "встречный?", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("глубина 1", r.Text);
    }

    [Fact]
    public async Task CounterQuestion_Rejected()
    {
        Peer("161432");
        Peer("161501");
        _h.Manager.Create("161432");
        var b = _h.Manager.Create("161501");
        await b.SendAsync("работа оператора");   // B занят — вопрос A ждёт в очереди
        var ex = New();
        _ = ex.AskAsync("161432", "161501", "?", live: true, default);

        var r = await ex.AskAsync("161501", "161432", "встречный", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("сама ждёт", r.Text);
    }

    [Fact]
    public async Task OneLiveQuestionPerAsker()
    {
        Peer("161501");
        Peer("161600");
        _h.Manager.Create("161600");
        await _h.Manager.Create("161501").SendAsync("занят");
        var ex = New();
        _ = ex.AskAsync("161432", "161501", "?", live: true, default);

        var r = await ex.AskAsync("161432", "161600", "?", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("уже есть вопрос", r.Text);
    }

    [Fact]
    public async Task RateLimit_PerHour()
    {
        Peer("161501");
        _h.Manager.Create("161501");
        var clock = new Clock();
        var ex = New(new PeerLimits(2, TimeSpan.FromMinutes(5)), clock);
        for (var i = 0; i < 2; i++)
        {
            var reply = ex.AskAsync("161432", "161501", $"вопрос {i}", live: true, default);
            await WaitUntil(() => _h.Manager.Get("161501")!.IsAnsweringPeer);
            _h.Last.Emit(Result);
            Assert.True((await reply).Ok);
        }

        var over = await ex.AskAsync("161432", "161501", "третий", live: true, default);
        Assert.False(over.Ok);
        Assert.Contains("лимит 2", over.Text);

        clock.At += TimeSpan.FromHours(1);
        var again = ex.AskAsync("161432", "161501", "через час", live: true, default);
        await WaitUntil(() => _h.Manager.Get("161501")!.IsAnsweringPeer);
        _h.Last.Emit(Result);
        Assert.True((await again).Ok);
    }

    [Fact]
    public async Task Timeout_FailsAndDropsQueuedQuestion()
    {
        Peer("161501");
        var b = _h.Manager.Create("161501");
        await b.SendAsync("долгая работа");
        var r = await New(new PeerLimits(20, TimeSpan.FromMilliseconds(200))).AskAsync("161432", "161501", "?", live: true, default);

        Assert.False(r.Ok);
        Assert.Contains("не ответил", r.Text);
        Assert.Equal(0, b.PeerQueued);
    }

    [Fact]
    public void ListPeers_FormatsSimilarity()
    {
        _dir.All.Add(new PeerInfo("161501", "Ryzen 7 9800X3D · ASUS TUF GAMING B650M-PLUS", "CPU Ryzen 7 9800X3D, память 2×32 ГБ @ 6000"));
        var text = New().ListPeers("161432");
        Assert.Contains("161501 · Ryzen 7 9800X3D", text);
        Assert.Contains("похоже: CPU Ryzen 7 9800X3D", text);
        Assert.Contains("нет", New().ListPeers("161501"));   // кроме себя — никого
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter FullyQualifiedName~PeerExchangeTests`
Expected: FAIL — `PeerExchange`/`IPeerDirectory`/`PeerInfo` не найдены.

- [ ] **Step 3: Implement**

`src/SzDiag.Claude/PeerExchange.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify they pass**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git status --short
git add src/SzDiag.Claude/PeerExchange.cs tests/SzDiag.Claude.Tests/FakePeerDirectory.cs tests/SzDiag.Claude.Tests/PeerExchangeTests.cs
git commit -m "feat(claude): PeerExchange — выжимка соседа, живой вопрос, лимиты"
```

---

### Task 3: MCP-инструменты `peers`/`ask_peer` и разрешение на них

**Files:**
- Modify: `src/SzDiag.Claude/DeskMcpServer.cs`, `src/SzDiag.Claude/ClaudeLaunch.cs`, `tests/SzDiag.Claude.Tests/ClaudeLaunchTests.cs`
- Create: `tests/SzDiag.Claude.Tests/DeskMcpPeerToolsTests.cs`

**Interfaces:**
- Consumes: `PeerExchange` (задача 2).
- Produces: `DeskMcpServer.StartAsync(PermissionBroker broker, PeerExchange? peers = null, CancellationToken ct = default)`; инструменты `peers()` и `ask_peer(key, question, live = false)`; `ClaudeLaunch.PeerTools = "mcp__desk__peers,mcp__desk__ask_peer"` и пара аргументов `--allowedTools <PeerTools>`.

- [ ] **Step 1: Write the failing tests**

`tests/SzDiag.Claude.Tests/DeskMcpPeerToolsTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class DeskMcpPeerToolsTests : IAsyncLifetime
{
    private readonly SessionHarness _h = new();
    private readonly FakePeerDirectory _dir = new();
    private readonly DeskMcpServer _server = new();
    private readonly HttpClient _http = new();

    public Task InitializeAsync()
        => _server.StartAsync(_h.Broker, new PeerExchange(_h.Manager, _dir, PeerLimits.Default, TimeProvider.System));

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
        _h.Dispose();
    }

    private async Task<JsonElement> CallAsync(string key, string tool, string args)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.EndpointFor(key));
        req.Headers.Add(DeskMcpServer.TokenHeader, _server.Token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Content = new StringContent(
            $$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{tool}}","arguments":{{args}}}}""",
            Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        return JsonDocument.Parse(data).RootElement.GetProperty("result").Clone();
    }

    private static string Text(JsonElement result) => result.GetProperty("content")[0].GetProperty("text").GetString()!;
    private static bool IsError(JsonElement result) => result.TryGetProperty("isError", out var e) && e.GetBoolean();

    [Fact]
    public async Task Peers_ListsOthersForCallingKey()
    {
        _dir.All.Add(new PeerInfo("161501", "Ryzen 7 9800X3D", "CPU Ryzen 7 9800X3D"));
        var r = await CallAsync("161432", "peers", "{}");
        Assert.False(IsError(r));
        Assert.Contains("161501 · Ryzen 7 9800X3D · похоже: CPU Ryzen 7 9800X3D", Text(r));
    }

    [Fact]
    public async Task AskPeer_Summary()
    {
        _dir.Summaries["161501"] = "СЗ 161501 · память меняли";
        var r = await CallAsync("161432", "ask_peer", """{"key":"161501","question":"что нашли?"}""");
        Assert.False(IsError(r));
        Assert.StartsWith("СЗ 161501 · память меняли", Text(r));
    }

    [Fact]
    public async Task AskPeer_Failure_IsToolError()
    {
        // Спека, «Ошибки»: лимит/таймаут/глубина — ошибка инструмента с причиной, Claude решает сам.
        var r = await CallAsync("161432", "ask_peer", """{"key":"161501","question":"?","live":true}""");
        Assert.True(IsError(r));
        Assert.Contains("нет активной сессии", Text(r));
    }
}
```

`ClaudeLaunchTests.cs` — новый тест:

```csharp
    [Fact]
    public void Arguments_PeerToolsPreAllowed()
    {
        // Решение плана части 4: обмен между сессиями ничего не меняет, а лимиты держит Desk —
        // карточка разрешения на каждый вопрос соседу была бы шумом.
        Assert.Equal("mcp__desk__peers,mcp__desk__ask_peer", After(L().Arguments(), "--allowedTools"));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter "FullyQualifiedName~DeskMcpPeerToolsTests|FullyQualifiedName~ClaudeLaunchTests"`
Expected: FAIL — `StartAsync` не принимает `PeerExchange`, нет `--allowedTools`.

- [ ] **Step 3: Implement**

`ClaudeLaunch.cs`:

```csharp
    /// <summary>Инструменты обмена между сессиями — без карточки разрешения: они ничего не меняют,
    /// а лимиты живых вопросов держит Desk (решение плана части 4). Одной строкой через запятую:
    /// флаг вариадический и иначе проглотил бы следующие аргументы.</summary>
    public const string PeerTools = "mcp__desk__peers,mcp__desk__ask_peer";
```

В `Arguments()` после пары `--permission-prompt-tool`: `"--allowedTools", PeerTools,`.

`DeskMcpServer.cs`:

```csharp
using ModelContextProtocol.Protocol;
```

```csharp
/// <summary>Обмен между сессиями может быть выключен (тесты разрешений, ядро без Desk).</summary>
public sealed record PeerHolder(PeerExchange? Exchange);
```

`StartAsync(PermissionBroker broker, PeerExchange? peers = null, CancellationToken ct = default)`, в регистрацию: `builder.Services.AddSingleton(new PeerHolder(peers));`.

`DeskTools`:

```csharp
[McpServerToolType]
public sealed class DeskTools(PermissionBroker broker, PeerHolder peers, IHttpContextAccessor http)
{
    private const string Off = "обмен между сессиями в этом Desk выключен";

    private string Key => http.HttpContext?.Request.RouteValues["key"] as string ?? "";

    [McpServerTool(Name = "permission_prompt"), Description("Запрос разрешения на инструмент у оператора SzDiag Desk")]
    public async Task<string> PermissionPrompt(string tool_name, JsonElement input, string? tool_use_id = null,
        CancellationToken ct = default)
    {
        var verdict = await broker.AskAsync(Key, tool_name, input, tool_use_id, ct).ConfigureAwait(false);
        return verdict.ToJson();
    }

    [McpServerTool(Name = "peers"), Description("Другие СЗ в работе с сессией Desk: железо одной строкой и чем похожи на твою машину")]
    public string Peers() => peers.Exchange?.ListPeers(Key) ?? Off;

    [McpServerTool(Name = "ask_peer"), Description(
        "Спросить соседнюю СЗ. Без live — выжимка её kb (діагностика + хвост журнала): даром и без отвлечения соседа. " +
        "live=true — вопрос уходит сессии соседа, ответ — её финальный текст хода (лимит в час, таймаут, глубина 1).")]
    public async Task<CallToolResult> AskPeer(string key, string question, bool live = false, CancellationToken ct = default)
    {
        var r = peers.Exchange is { } ex
            ? await ex.AskAsync(Key, key, question, live, ct).ConfigureAwait(false)
            : PeerReply.Fail(Off);
        return new CallToolResult { IsError = !r.Ok, Content = [new TextContentBlock { Text = r.Text }] };
    }
}
```

Если `CallToolResult.Content` в 2.2.0 не принимает collection expression, собрать `new List<ContentBlock> { new TextContentBlock { Text = r.Text } }`. Имена типов сверены по `ModelContextProtocol.Core.xml` 2.2.0: `CallToolResult.Content`, `CallToolResult.IsError`, `TextContentBlock`.

- [ ] **Step 4: Run to verify they pass**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: PASS, включая старые `DeskMcpServerTests` (они зовут `StartAsync(_broker)`).

- [ ] **Step 5: Commit**

```bash
git status --short
git add src/SzDiag.Claude/DeskMcpServer.cs src/SzDiag.Claude/ClaudeLaunch.cs tests/SzDiag.Claude.Tests/ClaudeLaunchTests.cs tests/SzDiag.Claude.Tests/DeskMcpPeerToolsTests.cs
git commit -m "feat(claude): MCP-инструменты peers и ask_peer, разрешены заранее"
```

---

### Task 4: Desk — профиль железа и его кэш

**Files:**
- Create: `src/SzDiag.Desk/Services/HwProfile.cs`, `src/SzDiag.Desk/Services/HwProfileCache.cs`, `tests/SzDiag.Desk.Tests/HwProfileTests.cs`, `tests/SzDiag.Desk.Tests/HwProfileCacheTests.cs`

**Interfaces:**
- Produces:
  - `record HwProfile(string Cpu, string BoardVendor, string Board, int Modules, int MemoryGb, int MemoryMhz, string MemoryParts)`: `static string Script`, `static HwProfile? Parse(string stdout)`, `string CpuShort`, `string BoardShort`, `string MemoryText`, `string Line`, `static IReadOnlyList<string> Similarity(HwProfile a, HwProfile b)`.
  - `HwProfileCache(IHubApiClient api, TimeProvider time)`: `static TimeSpan RetryAfter` (5 мин), `Task Update(IReadOnlyList<SessionInfo> sessions)` (звать из UI-потока), `HwProfile? Get(string sz)`, `IReadOnlyList<SessionInfo> Live`.

- [ ] **Step 1: Write the failing tests**

`tests/SzDiag.Desk.Tests/HwProfileTests.cs`:

```csharp
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HwProfileTests
{
    // Ровно то, что печатает HwProfile.Script (161432: 9800X3D + TUF B650M-PLUS + 2×32 DDR5-6000).
    internal const string Tuf9800 =
        "cpu=AMD Ryzen 7 9800X3D 8-Core Processor\n" +
        "board=ASUSTeK COMPUTER INC.|TUF GAMING B650M-PLUS\n" +
        "mem=2|64|6000|KF560C36-32";

    [Fact]
    public void Parse_Line()
    {
        var p = HwProfile.Parse(Tuf9800)!;
        Assert.Equal("Ryzen 7 9800X3D · ASUS TUF GAMING B650M-PLUS · 2×32 ГБ @ 6000", p.Line);
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i5-12400F", "i5-12400F")]
    [InlineData("AMD Ryzen 5 7500F 6-Core Processor", "Ryzen 5 7500F")]
    [InlineData("AMD Ryzen 7 5700G with Radeon Graphics", "Ryzen 7 5700G")]
    public void CpuShort(string name, string expected)
        => Assert.Equal(expected, new HwProfile(name, "", "", 0, 0, 0, "").CpuShort);

    [Fact]
    public void Parse_Garbage_Null() => Assert.Null(HwProfile.Parse("агент уже выполняет команду"));

    [Fact]
    public void Similarity_IdenticalBuilds_AllThree()
    {
        var a = HwProfile.Parse(Tuf9800)!;
        Assert.Equal(new[] { "CPU Ryzen 7 9800X3D", "плата ASUS TUF GAMING B650M-PLUS", "память 2×32 ГБ @ 6000" },
            HwProfile.Similarity(a, a with { MemoryParts = "другой" }));
    }

    [Fact]
    public void Similarity_MemoryNeedsWholeProfile_NotJustFrequency()
    {
        // Решение плана: одна частота 6000 есть почти в каждой сборке — бейдж горел бы у всех.
        var a = HwProfile.Parse(Tuf9800)!;
        var b = new HwProfile("AMD Ryzen 5 7500F 6-Core Processor", "Micro-Star International Co., Ltd.", "MAG B850 TOMAHAWK", 2, 32, 6000, "x");
        Assert.Empty(HwProfile.Similarity(a, b));
        Assert.Equal(new[] { "память 2×16 ГБ @ 6000" }, HwProfile.Similarity(b, b with { Cpu = "i5-12400F", Board = "другая" }));
    }

    [Fact]
    public void Similarity_EmptyFieldsNeverMatch()
    {
        var empty = new HwProfile("", "", "", 0, 0, 0, "");
        Assert.Empty(HwProfile.Similarity(empty, empty));
    }
}
```

`tests/SzDiag.Desk.Tests/HwProfileCacheTests.cs`:

```csharp
using SzDiag.Contracts;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HwProfileCacheTests
{
    private static readonly DateTimeOffset Boot = new(2026, 9, 25, 8, 0, 0, TimeSpan.Zero);
    private readonly ManualClock _clock = new();
    private readonly List<string> _calls = new();

    private static SessionInfo S(string sz, SessionStatus st = SessionStatus.Online, DateTimeOffset? boot = null)
        => new(sz, "10.0.0.5", "PC", st, Boot, Boot, BootTime: boot ?? Boot);

    private HwProfileCache New(Func<string, ExecResult?> exec)
        => new(new FakeHubApi { Exec = (sz, _) => { _calls.Add(sz); return exec(sz); } }, _clock);

    private static ExecResult Ok() => new("r", 0, HwProfileTests.Tuf9800, "");

    [Fact]
    public async Task OnlineFetchedOncePerBoot()
    {
        var c = New(_ => Ok());
        await c.Update(new[] { S("161432") });
        await c.Update(new[] { S("161432") });
        Assert.Equal(new[] { "161432" }, _calls);
        Assert.Equal("Ryzen 7 9800X3D", c.Get("161432")!.CpuShort);

        await c.Update(new[] { S("161432", boot: Boot.AddHours(1)) });   // свап железа = новый boot
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task OfflineNotFetched()
    {
        var c = New(_ => Ok());
        await c.Update(new[] { S("161432", SessionStatus.Offline) });
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task OneSzPerUpdate()
    {
        var c = New(_ => Ok());
        await c.Update(new[] { S("161432"), S("161501") });
        Assert.Single(_calls);
        await c.Update(new[] { S("161432"), S("161501") });
        Assert.Equal(new[] { "161432", "161501" }, _calls);
    }

    [Fact]
    public async Task Busy_NotRetriedFor5Minutes()
    {
        var c = New(_ => new ExecResult("r", -1, "", "агент уже выполняет команду"));
        await c.Update(new[] { S("161432") });
        await c.Update(new[] { S("161432") });
        Assert.Single(_calls);
        Assert.Null(c.Get("161432"));

        _clock.Advance(HwProfileCache.RetryAfter);
        await c.Update(new[] { S("161432") });
        Assert.Equal(2, _calls.Count);
    }

    [Fact]
    public async Task Timeout_TreatedAsBusy()
    {
        var c = New(_ => throw new TaskCanceledException());
        await c.Update(new[] { S("161432") });
        await c.Update(new[] { S("161432") });
        Assert.Single(_calls);
    }
}
```

Если в `SessionStatus` нет `Offline`, взять значение «не на связи» из `src/SzDiag.Contracts/SessionInfo.cs`.

- [ ] **Step 2: Run to verify they fail**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~HwProfile"`
Expected: FAIL — `HwProfile`/`HwProfileCache` не найдены.

- [ ] **Step 3: Implement**

`src/SzDiag.Desk/Services/HwProfile.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace SzDiag.Desk.Services;

/// <summary>Короткий профиль железа для строки на карточке СЗ и для «похожих СЗ». `hw passport`
/// для этого не годится: он снимает только видеокарту (решение плана части 4).</summary>
public sealed record HwProfile(string Cpu, string BoardVendor, string Board, int Modules, int MemoryGb, int MemoryMhz,
    string MemoryParts)
{
    /// <summary>Синхронный exec, без путей и слешей: одна строка на поле, разделитель `|`.</summary>
    public const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        $c = Get-CimInstance Win32_Processor | Select-Object -First 1
        $b = Get-CimInstance Win32_BaseBoard | Select-Object -First 1
        $m = @(Get-CimInstance Win32_PhysicalMemory)
        'cpu=' + $c.Name
        'board=' + $b.Manufacturer + '|' + $b.Product
        'mem=' + $m.Count + '|' + [math]::Round(($m | Measure-Object Capacity -Sum).Sum / 1GB) + '|' + ($m | Select-Object -First 1).ConfiguredClockSpeed + '|' + (($m | ForEach-Object { "$($_.PartNumber)".Trim() } | Sort-Object -Unique) -join '/')
        """;

    public static HwProfile? Parse(string stdout)
    {
        var kv = stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains('='))
            .Select(l => (Key: l[..l.IndexOf('=')], Value: l[(l.IndexOf('=') + 1)..].Trim()))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Value);
        if (!kv.ContainsKey("cpu") && !kv.ContainsKey("board")) return null;

        var board = (kv.GetValueOrDefault("board") ?? "").Split('|');
        var mem = (kv.GetValueOrDefault("mem") ?? "").Split('|');
        int N(int i) => i < mem.Length && int.TryParse(mem[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new HwProfile(kv.GetValueOrDefault("cpu") ?? "", board[0].Trim(), board.Length > 1 ? board[1].Trim() : "",
            N(0), N(1), N(2), mem.Length > 3 ? mem[3].Trim() : "");
    }

    private static readonly Regex CpuNoise = new(
        @"\((R|TM)\)|\bAMD\b|\bIntel\b|\bCore\b(?=(?:\s|\(TM\))+i\d)|\b\d+-Core\b|\bProcessor\b|\bCPU\b|with Radeon.*$|@.*$",
        RegexOptions.IgnoreCase);

    public string CpuShort => Regex.Replace(CpuNoise.Replace(Cpu, " "), @"\s+", " ").Trim();

    public string BoardShort => $"{VendorShort(BoardVendor)} {Board}".Trim();

    public string MemoryText => Modules > 0 && MemoryGb > 0 && MemoryMhz > 0
        ? $"{Modules}×{MemoryGb / Modules} ГБ @ {MemoryMhz}"
        : "";

    public string Line => string.Join(" · ", new[] { CpuShort, BoardShort, MemoryText }.Where(s => s.Length > 0));

    private static string VendorShort(string v) => v switch
    {
        _ when v.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase) => "ASUS",
        _ when v.StartsWith("Micro-Star", StringComparison.OrdinalIgnoreCase) => "MSI",
        _ when v.StartsWith("Gigabyte", StringComparison.OrdinalIgnoreCase) => "Gigabyte",
        _ => v.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "",
    };

    /// <summary>Чем b похож на a. Память — только целым профилем (планки × объём @ частота): одна
    /// частота есть почти в каждой сборке (решение плана части 4). Пустые поля не совпадают.</summary>
    public static IReadOnlyList<string> Similarity(HwProfile a, HwProfile b)
    {
        var r = new List<string>();
        if (Same(a.CpuShort, b.CpuShort)) r.Add($"CPU {a.CpuShort}");
        if (a.Board.Length > 0 && Same(a.BoardShort, b.BoardShort)) r.Add($"плата {a.BoardShort}");
        if (Same(a.MemoryText, b.MemoryText)) r.Add($"память {a.MemoryText}");
        return r;
    }

    private static bool Same(string x, string y) => x.Length > 0 && string.Equals(
        Regex.Replace(x, @"\s+", " ").Trim(), Regex.Replace(y, @"\s+", " ").Trim(), StringComparison.OrdinalIgnoreCase);
}
```

`src/SzDiag.Desk/Services/HwProfileCache.cs`:

```csharp
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
```

- [ ] **Step 4: Run to verify they pass**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~HwProfile"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git status --short
git add src/SzDiag.Desk/Services/HwProfile.cs src/SzDiag.Desk/Services/HwProfileCache.cs tests/SzDiag.Desk.Tests/HwProfileTests.cs tests/SzDiag.Desk.Tests/HwProfileCacheTests.cs
git commit -m "feat(desk): профиль железа СЗ раз на boot — строка и похожесть"
```

---

### Task 5: Desk — каталог соседей, вводная, конфиг, сборка

**Files:**
- Create: `src/SzDiag.Desk/Services/SzPeerDirectory.cs`, `tests/SzDiag.Desk.Tests/SzPeerDirectoryTests.cs`
- Modify: `src/SzDiag.Desk/Services/{SzBriefing,DeskOptions,DeskClaudeHost,ChatServices}.cs`, `src/SzDiag.Desk/App.axaml.cs`

**Interfaces:**
- Consumes: `IPeerDirectory`, `PeerInfo`, `PeerExchange`, `PeerLimits` (задача 2), `DeskMcpServer.StartAsync(broker, peers)` (задача 3), `HwProfileCache`, `HwProfile` (задача 4).
- Produces: `SzPeerDirectory(KbPaths kb, HwProfileCache hw, SessionManager sessions) : IPeerDirectory` (`FindingsTailChars` = 3000, `JournalTailLines` = 30); `DeskOptions.PeerLivePerHour` (20), `PeerTimeoutMinutes` (5); `DeskClaudeHost.StartAsync(DeskOptions o, string baseDir, Func<SessionManager, IPeerDirectory>? peers = null)` и свойство `PeerExchange? Peers`; `ChatServices(..., PeerExchange? Peers = null)` — последний необязательный параметр.

- [ ] **Step 1: Write the failing tests**

`tests/SzDiag.Desk.Tests/SzPeerDirectoryTests.cs`:

```csharp
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Kb;

namespace SzDiag.Desk.Tests;

public class SzPeerDirectoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ChatHarness _h = new();
    private readonly KbPaths _kb;

    public SzPeerDirectoryTests() => _kb = new KbPaths(Path.Combine(_h.Dir, "kb"));

    public void Dispose() => _h.Dispose();

    private static SessionInfo S(string sz) => new(sz, "10.0.0.5", "PC", SessionStatus.Online, Now, Now, BootTime: Now);

    private async Task<SzPeerDirectory> New(params string[] live)
    {
        var hw = new HwProfileCache(new FakeHubApi { Exec = (_, _) => new ExecResult("r", 0, HwProfileTests.Tuf9800, "") },
            new ManualClock());
        foreach (var _ in live) await hw.Update(live.Select(S).ToList());
        return new SzPeerDirectory(_kb, hw, _h.Services.Sessions);
    }

    [Fact]
    public async Task Peers_OthersWithSession_WithSimilarity()
    {
        _h.Services.Sessions.Create("161432");
        _h.Services.Sessions.Create("161501");
        var dir = await New("161432", "161501", "161600");   // у 161600 сессии Desk нет

        var peer = Assert.Single(dir.Peers("161432"));
        Assert.Equal("161501", peer.Key);
        Assert.Contains("Ryzen 7 9800X3D", peer.Profile);
        Assert.Contains("память 2×32 ГБ @ 6000", peer.Similar);
    }

    [Fact]
    public async Task Summary_FindingsAndJournalTail()
    {
        Directory.CreateDirectory(_kb.SzDir("161501"));
        File.WriteAllText(_kb.Findings("161501"), "EXPO 6000 не держится");
        File.WriteAllLines(_kb.Journal("161501"), Enumerable.Range(1, 40).Select(i => $"строка {i}"));
        var text = (await New()).Summary("161501")!;

        Assert.Contains("EXPO 6000 не держится", text);
        Assert.Contains("строка 40", text);
        Assert.DoesNotContain("строка 10\n", text);   // из журнала — только последние 30
    }

    [Fact]
    public async Task Summary_Nothing_Null() => Assert.Null((await New()).Summary("161501"));

    [Theory]
    [InlineData("..\\..\\secrets")]
    [InlineData("16150")]
    [InlineData("161501\\..\\161432")]
    public async Task Summary_RejectsNonSzKey(string key)
    {
        // Ключ приходит от Claude и становится путём на диске.
        Directory.CreateDirectory(_kb.SzDir("161432"));
        File.WriteAllText(_kb.Findings("161432"), "секрет");
        Assert.Null((await New()).Summary(key));
    }

    [Fact]
    public void Briefing_MentionsPeerTools()
    {
        var b = SzBriefing.For("161432", null);
        Assert.Contains("peers()", b);
        Assert.Contains("ask_peer", b);
    }

    [Fact]
    public void Options_PeerLimitsDefaults()
    {
        var o = new DeskOptions();
        Assert.Equal(20, o.PeerLivePerHour);
        Assert.Equal(5, o.PeerTimeoutMinutes);
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~SzPeerDirectoryTests`
Expected: FAIL — `SzPeerDirectory`, `PeerLivePerHour` не найдены.

- [ ] **Step 3: Implement**

`src/SzDiag.Desk/Services/SzPeerDirectory.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;
using SzDiag.Claude;
using SzDiag.Kb;

namespace SzDiag.Desk.Services;

/// <summary>Соседи для `peers()`/`ask_peer`: живые СЗ из hub с сессией Desk, их железо и похожесть;
/// выжимка — хвосты `діагностика.md` и `журнал.md` из kb. Зовётся с потоков MCP.</summary>
public sealed partial class SzPeerDirectory(KbPaths kb, HwProfileCache hw, SessionManager sessions) : IPeerDirectory
{
    public const int FindingsTailChars = 3000;
    public const int JournalTailLines = 30;

    [GeneratedRegex(@"^\d{6}$")]
    private static partial Regex SzKey();

    public IReadOnlyList<PeerInfo> Peers(string askerKey)
    {
        var mine = hw.Get(askerKey);
        var withSession = sessions.Records.Where(r => !r.Archived).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        return hw.Live
            .Where(s => s.Sz != askerKey && withSession.Contains(s.Sz))
            .Select(s =>
            {
                var p = hw.Get(s.Sz);
                var similar = mine is not null && p is not null ? HwProfile.Similarity(mine, p) : Array.Empty<string>();
                var state = sessions.Peek(s.Sz)?.State.ToString() ?? "Stopped";
                return new PeerInfo(s.Sz, $"{p?.Line ?? "железо ещё не снято"} · {s.Status} · сессия {state}",
                    similar.Count > 0 ? string.Join(", ", similar) : null);
            })
            .ToList();
    }

    /// <summary>Выжимка по любой СЗ, у которой есть папка в kb, в том числе закрытой: прошлая
    /// похожая заявка — ровно то, что стоит спросить (решение плана части 4).</summary>
    public string? Summary(string key)
    {
        if (!SzKey().IsMatch(key)) return null;
        var findings = Read(kb.Findings(key));
        var journal = Read(kb.Journal(key));
        if (string.IsNullOrWhiteSpace(findings) && string.IsNullOrWhiteSpace(journal)) return null;

        var sb = new StringBuilder($"СЗ {key}");
        if (hw.Get(key) is { } p) sb.Append(" · ").Append(p.Line);
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(findings))
            sb.AppendLine("== діагностика.md ==")
              .AppendLine(findings.Length <= FindingsTailChars ? findings.Trim() : "…" + findings[^FindingsTailChars..].Trim());
        if (!string.IsNullOrWhiteSpace(journal))
            sb.AppendLine($"== журнал.md (последние {JournalTailLines} строк) ==")
              .AppendLine(string.Join("\n", journal.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(JournalTailLines)));
        return sb.ToString().TrimEnd();
    }

    /// <summary>Журнал в этот момент может дописывать hub — читаем с общим доступом на запись.</summary>
    private static string? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
```

`SzBriefing.For` — в конец текста вводной добавить строку:

```
            Соседние заявки: peers() — кто ещё в работе и чем его железо похоже на твоё; ask_peer(key, question) — выжимка kb соседа (даром), ask_peer(…, live: true) — вопрос его сессии (лимиты — в ответе инструмента). Сравнение «две одинаковые сборки» (как на 161432) — через ask_peer, а не через чужую машину.
```

`DeskOptions`:

```csharp
    /// <summary>Живых вопросов соседям в час на одну сессию (спека, «ask_peer»).</summary>
    public int PeerLivePerHour { get; set; } = 20;

    /// <summary>Сколько ждать ответа живой сессии соседа.</summary>
    public int PeerTimeoutMinutes { get; set; } = 5;
```

`ChatServices` — последний параметр `PeerExchange? Peers = null` (с `<param>`: «обмен между сессиями; null — выключен»).

`DeskClaudeHost`:
- поле `private readonly DeskOptions _o;` в конструкторе;
- `public PeerExchange? Peers { get; private set; }`;
- `StartAsync(DeskOptions o, string baseDir, Func<SessionManager, IPeerDirectory>? peers = null)`: перед `Mcp.StartAsync` —
  `if (peers is not null) host.Peers = new PeerExchange(host.Sessions, peers(host.Sessions), new PeerLimits(o.PeerLivePerHour, TimeSpan.FromMinutes(o.PeerTimeoutMinutes)), TimeProvider.System);`
  и `await host.Mcp.StartAsync(host.Broker, host.Peers).ConfigureAwait(false);`;
- `Services(...)` — передать `Peers` последним аргументом `ChatServices`.

`App.axaml.cs` — `kbRoot`/`KbPaths` и `HwProfileCache` создаются до ядра:

```csharp
            var kbRoot = Path.IsPathRooted(opts.KbRoot) ? opts.KbRoot : Path.Combine(AppContext.BaseDirectory, opts.KbRoot);
            var kb = new KbPaths(kbRoot);
            var hw = new HwProfileCache(api, TimeProvider.System);
            var claude = Task.Run(() => DeskClaudeHost.StartAsync(opts, AppContext.BaseDirectory,
                sessions => new SzPeerDirectory(kb, hw, sessions))).GetAwaiter().GetResult();
```

`DeskTools` получает тот же `kb`, `MainViewModel` — `hw` (параметр появится в задаче 6; до неё строку не трогать).

- [ ] **Step 4: Run to verify they pass**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: PASS, весь проект (включая `DeskClaudeHostTests`).

- [ ] **Step 5: Commit**

```bash
git status --short
git add src/SzDiag.Desk/Services tests/SzDiag.Desk.Tests/SzPeerDirectoryTests.cs src/SzDiag.Desk/App.axaml.cs
git commit -m "feat(desk): каталог соседей из kb и профилей, лимиты ask_peer в конфиге"
```

---

### Task 6: Desk UI — строка железа, `≈`, `💬`, лента и состояние

**Files:**
- Modify: `src/SzDiag.Desk/ViewModels/{MainViewModel,SzItemViewModel,ChatViewModel,FeedBuilder,FeedItems,ToolSummary}.cs`, `src/SzDiag.Desk/Views/{SzListView,ChatView}.axaml`, `src/SzDiag.Desk/Views/Converters.cs`, `src/SzDiag.Desk/App.axaml.cs`, `tests/SzDiag.Desk.Tests/{ChatHarness,FeedBuilderTests}.cs`
- Create: `tests/SzDiag.Desk.Tests/MainViewModelPeerTests.cs`, `tests/SzDiag.Desk.Tests/FakePeerDirectory.cs`

**Interfaces:**
- Consumes: `HwProfileCache`, `HwProfile.Similarity` (задача 4); `PeerExchange.Active/Changed`, `ChatServices.Peers` (задачи 2, 5); `PeerQuestion`, `SessionState.AnsweringPeer` (задача 1).
- Produces: `MainViewModel(..., FreezeProbe? freeze = null, HwProfileCache? hw = null)`; у `SzItemViewModel`: `HwLine`/`HasHwLine`, `SimilarText`/`SimilarTip`/`HasSimilar`, `PeerText`/`HasPeer`; `PeerFeedItem(string from, string text)` с `Title`; в `ChatHarness` — `FakePeerDirectory Directory` и `PeerExchange Peers`.

- [ ] **Step 1: Write the failing tests**

`tests/SzDiag.Desk.Tests/FakePeerDirectory.cs`:

```csharp
using SzDiag.Claude;

namespace SzDiag.Desk.Tests;

internal sealed class FakePeerDirectory : IPeerDirectory
{
    public List<PeerInfo> All { get; } = new();
    public IReadOnlyList<PeerInfo> Peers(string askerKey) => All.Where(p => p.Key != askerKey).ToList();
    public string? Summary(string key) => null;
}
```

`ChatHarness` — поля `public FakePeerDirectory Directory { get; } = new();` и `public PeerExchange Peers { get; }`. В конструкторе, после `sessions`: `Peers = new PeerExchange(sessions, Directory, PeerLimits.Default, TimeProvider.System);` и `Peers` — последним аргументом `ChatServices`.

`tests/SzDiag.Desk.Tests/MainViewModelPeerTests.cs`:

```csharp
using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class MainViewModelPeerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ChatHarness _h = new();
    private readonly ManualClock _clock = new() { Now = Now };

    public void Dispose() => _h.Dispose();

    private static SessionInfo S(string sz) => new(sz, "10.0.0.5", "PC-" + sz, SessionStatus.Online, Now, Now, BootTime: Now);
    private static HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = Now };

    private const string Other = "cpu=Intel(R) Core(TM) i5-12400F\nboard=Gigabyte Technology Co., Ltd.|B760M DS3H\nmem=2|32|3200|x";

    private MainViewModel New(Dictionary<string, string> hw)
    {
        var cache = new HwProfileCache(new FakeHubApi { Exec = (sz, _) => new ExecResult("r", 0, hw[sz], "") }, _clock);
        return new MainViewModel(new HubPoller(new FakeHubApi(), _clock), new DeskUiState(), _clock, _h.Services, hw: cache);
    }

    private static void ApplyTimes(MainViewModel vm, HubSnapshot snap, int n)
    {
        for (var i = 0; i < n; i++) vm.Apply(snap);   // профиль снимается по одной СЗ за опрос
    }

    [Fact]
    public void HwLine_AndSimilarBadge()
    {
        var vm = New(new() { ["161432"] = HwProfileTests.Tuf9800, ["161501"] = HwProfileTests.Tuf9800, ["161600"] = Other });
        ApplyTimes(vm, Snap(S("161432"), S("161501"), S("161600")), 4);

        var a = vm.Items.Single(i => i.Sz == "161432");
        Assert.Equal("Ryzen 7 9800X3D · ASUS TUF GAMING B650M-PLUS · 2×32 ГБ @ 6000", a.HwLine);
        Assert.Equal("≈161501", a.SimilarText);
        Assert.Contains("CPU Ryzen 7 9800X3D", a.SimilarTip);
        Assert.Equal("≈161432", vm.Items.Single(i => i.Sz == "161501").SimilarText);
        Assert.False(vm.Items.Single(i => i.Sz == "161600").HasSimilar);
    }

    [Fact]
    public async Task PeerExchange_MarksBothCards()
    {
        var vm = New(new() { ["161432"] = Other, ["161501"] = Other });
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161432");
        _h.Services.Sessions.Create("161501");
        _h.Directory.All.Add(new PeerInfo("161501", "", null));

        var reply = _h.Peers.AskAsync("161432", "161501", "?", live: true, default);
        vm.Apply(Snap(S("161432"), S("161501")));   // точки сессий обновляет опрос (чаты не открыты)
        Assert.Equal("💬161501", vm.Items.Single(i => i.Sz == "161432").PeerText);
        Assert.Equal("💬161432", vm.Items.Single(i => i.Sz == "161501").PeerText);
        Assert.Equal(SessionState.AnsweringPeer, vm.Items.Single(i => i.Sz == "161501").SessionState);

        _h.Last.Exit(1);   // сосед упал — пара снимается
        Assert.False((await reply).Ok);
        Assert.False(vm.Items.Single(i => i.Sz == "161432").HasPeer);
    }
}
```

`FeedBuilderTests.cs` — новые тесты (в стиле файла):

```csharp
    [Fact]
    public void PeerQuestion_VioletCard()
    {
        var b = new FeedBuilder((_, _) => { }, () => { });
        b.Add(new PeerQuestion("161501", "какой BIOS?"));
        Assert.Equal("💬 от 161501: какой BIOS?", Assert.IsType<PeerFeedItem>(Assert.Single(b.Items)).Title);
    }

    [Fact]
    public void AskPeer_ToolSummary()
    {
        var input = System.Text.Json.JsonDocument.Parse("""{"key":"161501","question":"какой BIOS?","live":true}""").RootElement;
        Assert.Equal("161501 (живьём): какой BIOS?", ToolSummary.For("mcp__desk__ask_peer", input));
    }
```

(в `using` файла — `SzDiag.Claude`, если его там нет.)

- [ ] **Step 2: Run to verify they fail**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~MainViewModelPeerTests|FullyQualifiedName~FeedBuilderTests"`
Expected: FAIL — `hw:`/`HwLine`/`SimilarText`/`PeerText`/`PeerFeedItem` не найдены.

- [ ] **Step 3: Implement**

`SzItemViewModel`:

```csharp
    /// <summary>CPU · плата · память — из профиля железа (снимается раз на boot).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHwLine))] private string _hwLine = "";
    public bool HasHwLine => HwLine.Length > 0;

    /// <summary>`≈&lt;СЗ&gt;` — похожая машина в работе (подсказка человеку; Claude видит то же через peers()).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSimilar))] private string _similarText = "";
    [ObservableProperty] private string _similarTip = "";
    public bool HasSimilar => SimilarText.Length > 0;

    /// <summary>`💬&lt;СЗ&gt;` — сессии переписываются прямо сейчас (фиолетовая метка спеки).</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPeer))] private string _peerText = "";
    public bool HasPeer => PeerText.Length > 0;
```

`MainViewModel`:
- параметр конструктора `HwProfileCache? hw = null` → поле `_hw`; при `chat?.Peers is { } peers` — `peers.Changed += () => chat.Ui(RefreshSessionBadges);`;
- в `Apply` после блока `_freeze` (до раннего `return`):

```csharp
        if (_hw is not null)
        {
            if (s.SessionsOkAt is not null && !s.IsStale) _ = _hw.Update(s.Sessions);
            RefreshHardware();
        }
```

- новый метод:

```csharp
    /// <summary>Строка железа и `≈`: похожая — живая СЗ из списка, у которой совпал CPU, плата или
    /// профиль памяти целиком.</summary>
    private void RefreshHardware()
    {
        var profiles = Items.Select(i => (i.Sz, P: _hw!.Get(i.Sz))).Where(x => x.P is not null).ToList();
        foreach (var item in Items)
        {
            var mine = _hw!.Get(item.Sz);
            item.HwLine = mine?.Line ?? "";
            var similar = mine is null
                ? new List<(string Sz, IReadOnlyList<string> Why)>()
                : profiles.Where(x => x.Sz != item.Sz)
                    .Select(x => (x.Sz, Why: HwProfile.Similarity(mine, x.P!)))
                    .Where(x => x.Why.Count > 0).ToList();
            item.SimilarText = similar.Count switch
            {
                0 => "",
                1 => $"≈{similar[0].Sz}",
                _ => $"≈{similar[0].Sz} +{similar.Count - 1}",
            };
            item.SimilarTip = string.Join("\n", similar.Select(x => $"{x.Sz}: {string.Join(", ", x.Why)}"));
        }
    }
```

- в `RefreshSessionBadges` после строки с `SessionState`:

```csharp
        var active = _chat.Peers?.Active ?? Array.Empty<(string From, string To)>();
        foreach (var item in Items)
        {
            var other = active.Where(a => a.From == item.Sz).Select(a => a.To)
                .Concat(active.Where(a => a.To == item.Sz).Select(a => a.From)).FirstOrDefault();
            item.PeerText = other is null ? "" : $"💬{other}";
        }
```

- в `ArchiveClosed`: `if (session.State is SessionState.Working or SessionState.WaitingPermission or SessionState.AnsweringPeer) continue;`.

`ChatViewModel`: `CanStop => State is SessionState.Working or SessionState.WaitingPermission or SessionState.AnsweringPeer;`, в `StateText` — `SessionState.AnsweringPeer => "отвечает на вопрос соседней сессии…",`.

`Converters`: в `SessionStateToBrush` — `SessionState.AnsweringPeer => Res("Violet"),`; в `SessionStateToText` — `SessionState.AnsweringPeer => "Claude отвечает соседней сессии",`.

`FeedItems.cs`:

```csharp
/// <summary>Вопрос соседней сессии: ответ на него — обычные реплики этого хода.</summary>
public sealed class PeerFeedItem(string from, string text) : FeedItemViewModel
{
    public string Title { get; } = $"💬 от {from}: {text}";
}
```

`FeedBuilder.Add` — ветка: `case PeerQuestion q: Items.Add(new PeerFeedItem(q.FromKey, q.Text) { At = q.At }); break;`.

`ToolSummary.For` — перед `var raw = tool switch`:

```csharp
        if (tool == "mcp__desk__ask_peer")
        {
            var live = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("live", out var l) && l.ValueKind == JsonValueKind.True;
            var text = $"{Prop(input, "key")}{(live ? " (живьём)" : "")}: {Prop(input, "question")}";
            return text.Length <= Max ? text : text[..(Max - 1)] + "…";
        }
```

`ChatView.axaml` — шаблон рядом с `NoteFeedItem`:

```xml
          <DataTemplate DataType="vm:PeerFeedItem">
            <Border BorderBrush="{StaticResource Violet}" BorderThickness="1" CornerRadius="8" Padding="10,6"
                    HorizontalAlignment="Left" MaxWidth="640">
              <SelectableTextBlock Text="{Binding Title}" Foreground="{StaticResource Violet}" TextWrapping="Wrap" />
            </Border>
          </DataTemplate>
```

`SzListView.axaml` — `ColumnDefinitions="Auto,*,Auto,Auto,Auto,Auto,Auto"`; под `Subtitle`:

```xml
              <TextBlock Text="{Binding HwLine}" Classes="secondary" FontSize="10"
                         TextTrimming="CharacterEllipsis" IsVisible="{Binding HasHwLine}" />
```

после `FrozenBadge` (колонка 3):

```xml
            <TextBlock Grid.Column="4" Text="{Binding SimilarText}" FontSize="10" Margin="6,0,0,0"
                       VerticalAlignment="Center" IsVisible="{Binding HasSimilar}"
                       Foreground="{StaticResource Accent}" ToolTip.Tip="{Binding SimilarTip}" />
            <TextBlock Grid.Column="5" Text="{Binding PeerText}" FontSize="10" Margin="6,0,0,0"
                       VerticalAlignment="Center" IsVisible="{Binding HasPeer}"
                       Foreground="{StaticResource Violet}" ToolTip.Tip="сессии переписываются (ask_peer)" />
```

точка сессии — `Grid.Column="6"`.

`App.axaml.cs` — `new MainViewModel(..., inspector, new FreezeProbe(szcli), hw)`.

- [ ] **Step 4: Run to verify they pass**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: PASS, весь проект (дымовые тесты окна и инспектора — тоже).

- [ ] **Step 5: Commit**

```bash
git status --short
git add src/SzDiag.Desk tests/SzDiag.Desk.Tests
git commit -m "feat(desk): строка железа, бейджи ≈ и 💬, вопрос соседа в ленте"
```

---

### Task 7: Документация, спека, живой чек-лист

**Files:**
- Modify: `docs/superpowers/specs/2026-09-25-desk-gui-design.md`, `CLAUDE.md`, `docs/dev-knowledge-base.md`
- Create: `docs/live-checklist-2026-09-25-desk-part4.md`

- [ ] **Step 1: Спека**
  - Строка статуса: «этапы 1–6 реализованы (планы частей 1–4)».
  - Раздел «`ask_peer`»: вопрос соседа идёт **впереди** очереди оператора. Встречный вопрос отклоняется. У спрашивающего не больше одного живого вопроса одновременно. Выжимка kb — по любой СЗ с папкой в kb (номер `^\d{6}$`), живой вопрос — только к активной сессии. Вместо «флага спросить живьём?» — подсказка текстом в конце выжимки. `peers`/`ask_peer` разрешены заранее через `--allowedTools`. Ошибки возвращаются как `CallToolResult.IsError`.
  - «Похожие СЗ»: источник — собственный профиль Desk (`HwProfile.Script`, синхронный exec раз на `BootTime`, по одной СЗ, повтор через 5 мин), а не `hw passport`: тот снимает только GPU. Память похожа только целым профилем «планки × объём @ частота».
  - Таблица «Источники данных GUI»: строка «профиль железа | синхронный exec `HwProfile.Script` | раз на boot, по одной СЗ за опрос».
  - «Внешний вид» / карточка СЗ: `💬<СЗ>` (фиолетовый) — пара переписывается; состояние `AnsweringPeer` — фиолетовая точка.

- [ ] **Step 2: CLAUDE.md** — в описание `SzDiag.Desk` добавить: «Сессии соседних СЗ спрашивают друг друга: `peers()` / `ask_peer(key, question[, live])` через MCP Desk. Выжимка kb — даром. Живой вопрос — до 20 в час, ответ до 5 мин, глубина 1, встречный запрещён. На карточке — строка железа (CPU · плата · память), `≈<СЗ>` для похожей машины в работе, `💬<СЗ>` для переписывающейся пары». В описание `SzDiag.Claude` — `PeerExchange`/`IPeerDirectory`.

- [ ] **Step 3: dev-knowledge-base** — в раздел Desk/MCP: таблица инструментов `desk` (`permission_prompt`, `peers`, `ask_peer` — аргументы, ошибки, лимиты, `PeerLivePerHour`/`PeerTimeoutMinutes`), состояние `AnsweringPeer`, строка журнала `desk_peer_question`, рецепт «сравнить две одинаковые сборки через `ask_peer`».

- [ ] **Step 4: Живой чек-лист** `docs/live-checklist-2026-09-25-desk-part4.md` (по образцу части 3):
  1. Две онлайн-СЗ. В течение пары опросов под номерами появилась строка железа. Строка совпадает с `hw-fingerprint.ps1`.
  2. Две одинаковые сборки → у обеих `≈<сосед>`, подсказка перечисляет, что совпало. Разные сборки — бейджа нет.
  3. В сессии A: «спроси соседа, что уже нашли». Claude зовёт `ask_peer` без live, в ленте A карточка `ask_peer` с выжимкой. Карточки разрешения нет.
  4. «Спроси соседа живьём, какой BIOS». В ленте B появляется `💬 от A: …`, точка B фиолетовая, на обеих карточках `💬`. Ответ B приходит в карточку инструмента у A.
  5. «■» в A, пока B ещё занят своим ходом: вопрос снят, B после своего хода его не получает.
  6. Встречный вопрос из B в A, пока A ждёт → в B ошибка «сама ждёт ответа соседа».
  7. Убедиться, что `peers` и `ask_peer` в режиме `auto` не просят разрешения. Если просят, значит `--allowedTools` не подхватился: записать в бэклог.
  8. Под OCCT на одной из СЗ профиль не снимается повторно каждые 2 с (проверить `logs\hub-*.log`: exec профиля — не чаще раза в 5 минут).

- [ ] **Step 5: Полный прогон и коммит**

Run: `"C:\Program Files\dotnet\dotnet.exe" test --filter "FullyQualifiedName!~AllRepoClientRecipes_NoFalseConcatWarning"`
Expected: PASS, все проекты.

```bash
git status --short
git add docs/superpowers/specs/2026-09-25-desk-gui-design.md CLAUDE.md docs/dev-knowledge-base.md docs/live-checklist-2026-09-25-desk-part4.md
git commit -m "docs(desk): ask_peer и похожие СЗ — спека, база знаний, живой чек-лист"
```
