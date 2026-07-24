# Агент переживает ребут + auto-reconnect — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** После ребута клиентской машины агент сам поднимается (scheduled task `-AtStartup`), переподнимает sshd и переподключается к hub под тем же СЗ из persisted `state.json` — без ручного вмешательства; весь механизм откатывается без следов.

**Architecture:** Новый режим `agent.exe --resume <statePath>` (по аналогии с `--revert`), headless, читает `state.json`, вызывает `WindowsSystemAccessManager.Resume` (переподнять только sshd + сдвинуть watchdog) и `AgentSession.ResumeAsync` (connect+register). Автостарт-таск ставится в `Open` (шаг 8), снимается в `Revert` первым шагом. sshd прячется за интерфейс `ISshServer` ради тестируемости `Resume`.

**Tech Stack:** .NET 8, xUnit, PowerShell scheduled tasks, SignalR.

Спека: [docs/superpowers/specs/2026-07-24-agent-survive-reboot-design.md](../specs/2026-07-24-agent-survive-reboot-design.md)

---

## File Structure

- `src/SzDiag.Agent/RevertState.cs` — +2 поля (`AutostartTaskName`, `CreatedAutostartTask`).
- `src/SzDiag.Agent/ISshServer.cs` — **новый** интерфейс sshd (шов для тестов `Resume`).
- `src/SzDiag.Agent/PortableSshServer.cs` — `: ISshServer` (члены уже есть).
- `src/SzDiag.Agent/ISystemAccessManager.cs` — +`Resume(RevertState, AccessSpec)`.
- `src/SzDiag.Agent/WindowsSystemAccessManager.cs` — static-билдеры задач, guard, шаг 8 в `Open`, снятие автостарта в `Revert`, метод `Resume`; поле `_sshd` → `ISshServer`.
- `src/SzDiag.Agent/AgentSession.cs` — `ResumeAsync`, `Completion`.
- `src/SzDiag.Agent/AgentCommandWiring.cs` — **новый**: вынос регистрации RunTests/RunDiag-обработчиков и heartbeat-цикла из `Program.cs` (DRY между интерактивной и resume-ветками).
- `src/SzDiag.Agent/Program.cs` — ветка `--resume`; интерактивная ветка использует `AgentCommandWiring`.
- Зеркальные тесты в `tests/SzDiag.Agent.Tests/`.

---

## Task 1: RevertState — поля автостарта

**Files:**
- Modify: `src/SzDiag.Agent/RevertState.cs`
- Test: `tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs`

- [ ] **Step 1: Расширить round-trip тест новыми полями**

В `tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs`, в тесте `SaveThenLoad_RoundTrips`, добавить в инициализатор `state` две строки после `CreatedSshdTask = true`:

```csharp
            CreatedSshdTask = true,
            AutostartTaskName = "szdiag-autostart-156864",
            CreatedAutostartTask = true
```

и два ассерта после `Assert.True(loaded.CreatedSshdTask);`:

```csharp
        Assert.Equal("szdiag-autostart-156864", loaded.AutostartTaskName);
        Assert.True(loaded.CreatedAutostartTask);
```

- [ ] **Step 2: Запустить тест — убедиться, что не компилируется/падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~RevertStateStoreTests.SaveThenLoad_RoundTrips`
Expected: FAIL — компиляция не проходит (нет свойств `AutostartTaskName`/`CreatedAutostartTask`).

- [ ] **Step 3: Добавить поля в RevertState**

В `src/SzDiag.Agent/RevertState.cs` после строки `public string AuthorizedKeyComment { get; set; } = "";` добавить:

```csharp
    public string AutostartTaskName { get; set; } = "";
```

и после `public bool CreatedWatchdogTask { get; set; }` добавить:

```csharp
    public bool CreatedAutostartTask { get; set; }
```

- [ ] **Step 4: Запустить тест — PASS**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~RevertStateStoreTests.SaveThenLoad_RoundTrips`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/RevertState.cs tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs
git commit -m "feat(agent): поля автостарт-таска в RevertState"
```

---

## Task 2: ISshServer — шов для тестируемости Resume

**Files:**
- Create: `src/SzDiag.Agent/ISshServer.cs`
- Modify: `src/SzDiag.Agent/PortableSshServer.cs:19`
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs:17`

Чистый рефактор (без новой логики): `WindowsSystemAccessManager` начинает зависеть от интерфейса, чтобы в тестах `Resume` подменять sshd фейком (реальный `_sshd.Start` ждёт порт 5с и кидает `SshdStartException`).

- [ ] **Step 1: Создать интерфейс**

Создать `src/SzDiag.Agent/ISshServer.cs`:

```csharp
namespace SzDiag.Agent;

/// <summary>Жизненный цикл портативного sshd. Абстракция ради подмены в тестах
/// (реальный Start ждёт готовности порта и ходит в систему).</summary>
public interface ISshServer
{
    /// <summary>Свежие host-ключи + конфиг + запуск sshd под SYSTEM; ждёт готовности порта.</summary>
    void Start(int port, string authorizedKeyLine, string taskName);

    /// <summary>Снять sshd-задачу и добить наш sshd (идемпотентно).</summary>
    void Stop(string taskName);

    /// <summary>Рабочая папка sshd (host-ключи, конфиг, authorized_keys).</summary>
    string WorkDir { get; }
}
```

- [ ] **Step 2: PortableSshServer реализует интерфейс**

В `src/SzDiag.Agent/PortableSshServer.cs` заменить объявление класса:

```csharp
public sealed class PortableSshServer
```

на:

```csharp
public sealed class PortableSshServer : ISshServer
```

(Методы `Start`/`Stop` и свойство `WorkDir` уже существуют — менять их не нужно.)

- [ ] **Step 3: WindowsSystemAccessManager зависит от ISshServer**

В `src/SzDiag.Agent/WindowsSystemAccessManager.cs` заменить тип поля и параметра конструктора `PortableSshServer` → `ISshServer`:

```csharp
    private readonly ISshServer _sshd;
```

```csharp
    public WindowsSystemAccessManager(IPowerShellRunner ps, ISshServer sshd, string statePath)
```

- [ ] **Step 4: Сборка и весь набор тестов зелёные (поведение не изменилось)**

Run: `dotnet build; dotnet test tests/SzDiag.Agent.Tests`
Expected: BUILD OK, все тесты PASS (существующий `Make()` передаёт реальный `PortableSshServer` — он реализует `ISshServer`).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/ISshServer.cs src/SzDiag.Agent/PortableSshServer.cs src/SzDiag.Agent/WindowsSystemAccessManager.cs
git commit -m "refactor(agent): ISshServer — шов для тестов Resume"
```

---

## Task 3: Static-билдеры команд задач (watchdog + автостарт)

**Files:**
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`
- Test: `tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs`

Выносим генерацию PowerShell для watchdog- и автостарт-задач в чистые static-методы (паттерн уже есть в `PortableSshServer.BuildRegisterTaskCommand`). Это делает их юнит-тестируемыми без запуска `Open`.

- [ ] **Step 1: Написать падающие тесты билдеров**

В `tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs` добавить тесты (внутри класса):

```csharp
    [Fact]
    public void BuildWatchdogTaskCommand_UsesRevertAndOnceTrigger()
    {
        var cmd = WindowsSystemAccessManager.BuildWatchdogTaskCommand(
            "szdiag-watchdog-156864", @"C:\a\agent.exe", @"C:\s\state.json",
            new DateTime(2026, 7, 24, 15, 0, 0));

        Assert.Contains("--revert", cmd);
        Assert.Contains("-Once", cmd);
        Assert.Contains("2026-07-24T15:00:00", cmd);
        Assert.Contains("szdiag-watchdog-156864", cmd);
        Assert.Contains("SYSTEM", cmd);
    }

    [Fact]
    public void BuildAutostartTaskCommand_UsesAtStartupAndResume()
    {
        var cmd = WindowsSystemAccessManager.BuildAutostartTaskCommand(
            "szdiag-autostart-156864", @"C:\a\agent.exe", @"C:\s\state.json");

        Assert.Contains("-AtStartup", cmd);
        Assert.Contains("--resume", cmd);
        Assert.Contains("szdiag-autostart-156864", cmd);
        Assert.Contains("SYSTEM", cmd);
    }
```

- [ ] **Step 2: Запустить — FAIL (методов нет)**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~BuildWatchdogTaskCommand`
Expected: FAIL — компиляция не проходит (нет `BuildWatchdogTaskCommand`).

- [ ] **Step 3: Добавить билдеры и переключить Open step 7 на билдер**

В `src/SzDiag.Agent/WindowsSystemAccessManager.cs` добавить два static-метода (например, перед `GeneratePassword`):

```csharp
    /// <summary>PowerShell регистрации watchdog-задачи (-Once на runAt): по таймауту
    /// запускает этот exe с --revert. -Force перезаписывает существующую (для resume-сдвига).</summary>
    public static string BuildWatchdogTaskCommand(string taskName, string exePath, string statePath, DateTime runAt) =>
        $"$a = New-ScheduledTaskAction -Execute '{exePath}' -Argument '--revert \"{statePath}\"'; " +
        $"$t = New-ScheduledTaskTrigger -Once -At '{runAt:yyyy-MM-ddTHH:mm:ss}'; " +
        $"Register-ScheduledTask -TaskName '{taskName}' -Action $a -Trigger $t " +
        "-RunLevel Highest -User 'SYSTEM' -Force";

    /// <summary>PowerShell регистрации автостарт-задачи (-AtStartup): после ребута
    /// поднимает этот exe с --resume под SYSTEM (до логина, headless).</summary>
    public static string BuildAutostartTaskCommand(string taskName, string exePath, string statePath) =>
        $"$a = New-ScheduledTaskAction -Execute '{exePath}' -Argument '--resume \"{statePath}\"'; " +
        "$t = New-ScheduledTaskTrigger -AtStartup; " +
        $"Register-ScheduledTask -TaskName '{taskName}' -Action $a -Trigger $t " +
        "-RunLevel Highest -User 'SYSTEM' -Force";
```

Затем заменить в `Open` инлайновый блок watchdog (шаг 7, строки со `$a = New-ScheduledTaskAction ... -Argument '--revert ...'`) на вызов билдера:

```csharp
        // 7. Watchdog scheduled task (запускает этот exe с --revert по таймауту)
        var exe = Environment.ProcessPath!;
        _ps.Run(BuildWatchdogTaskCommand(state.WatchdogTaskName, exe, _statePath,
            DateTime.Now.Add(spec.WatchdogTimeout)));
        state.CreatedWatchdogTask = true;
        Persist();
```

- [ ] **Step 4: Запустить билдер-тесты + весь набор — PASS**

Run: `dotnet test tests/SzDiag.Agent.Tests`
Expected: PASS (билдеры зелёные; поведение `Open` не изменилось — тот же скрипт).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/WindowsSystemAccessManager.cs tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs
git commit -m "refactor(agent): static-билдеры watchdog/autostart задач"
```

---

## Task 4: Open ставит автостарт + guard; Revert снимает первым

**Files:**
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`
- Test: `tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs`

- [ ] **Step 1: Написать падающие тесты (guard + порядок снятия)**

В `tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs` добавить:

```csharp
    [Fact]
    public void Revert_WithAutostartTask_UnregistersItBeforeWatchdog()
    {
        var ps = new FakePs();
        var state = new RevertState
        {
            Sz = "156864",
            AutostartTaskName = "szdiag-autostart-156864", CreatedAutostartTask = true,
            WatchdogTaskName = "szdiag-watchdog-156864", CreatedWatchdogTask = true
        };

        Make(ps).Revert(state);

        var autostartIdx = ps.Scripts.FindIndex(s => s.Contains("szdiag-autostart-156864"));
        var watchdogIdx = ps.Scripts.FindIndex(s => s.Contains("szdiag-watchdog-156864"));
        Assert.True(autostartIdx >= 0, "автостарт-таск должен сниматься");
        Assert.True(autostartIdx < watchdogIdx, "автостарт снимается ПЕРЕД watchdog");
    }

    [Fact]
    public void Revert_WithoutAutostartFlag_DoesNotTouchAutostart()
    {
        var ps = new FakePs();
        var state = new RevertState
        {
            Sz = "156864",
            AutostartTaskName = "szdiag-autostart-156864", CreatedAutostartTask = false
        };

        Make(ps).Revert(state);

        Assert.DoesNotContain(ps.Scripts, s => s.Contains("szdiag-autostart-156864"));
    }

    [Fact]
    public void RevertStaleState_DifferentSz_RevertsOld()
    {
        var ps = new FakePs();
        var mgr = Make(ps);
        RevertStateStore.Save(_statePath, new RevertState
        {
            Sz = "111", AutostartTaskName = "szdiag-autostart-111", CreatedAutostartTask = true
        });

        mgr.RevertStaleState("222");

        Assert.Contains(ps.Scripts, s => s.Contains("Unregister-ScheduledTask") && s.Contains("szdiag-autostart-111"));
    }

    [Fact]
    public void RevertStaleState_SameSz_DoesNothing()
    {
        var ps = new FakePs();
        var mgr = Make(ps);
        RevertStateStore.Save(_statePath, new RevertState { Sz = "222", CreatedUser = true });

        mgr.RevertStaleState("222");

        Assert.Empty(ps.Scripts);
    }
```

- [ ] **Step 2: Запустить — FAIL**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~WindowsSystemAccessManagerTests`
Expected: FAIL — нет метода `RevertStaleState`; `Revert` не снимает автостарт.

- [ ] **Step 3: Реализовать guard, шаг 8 в Open, снятие автостарта в Revert**

В `src/SzDiag.Agent/WindowsSystemAccessManager.cs`:

(а) В инициализатор `state` в начале `Open` добавить имя автостарт-таска (после `AuthorizedKeyComment`):

```csharp
            AuthorizedKeyComment = $"szdiag-{spec.Sz}",
            AutostartTaskName = $"szdiag-autostart-{spec.Sz}"
```

(б) Самой первой строкой тела `Open` (перед `var state = ...`) вызвать guard:

```csharp
    public RevertState Open(AccessSpec spec)
    {
        // Остаток от ДРУГОЙ незакрытой СЗ → откатить, иначе её задачи/автостарт повиснут.
        RevertStaleState(spec.Sz);

        var state = new RevertState
```

(в) После блока watchdog (шаг 7) добавить шаг 8 — регистрацию автостарта (`exe` уже объявлен в шаге 7):

```csharp
        // 8. Автостарт после ребута: scheduled task -AtStartup → agent.exe --resume.
        //    Ставится последним (доступ уже поднят), снимается в Revert первым.
        _ps.Run(BuildAutostartTaskCommand(state.AutostartTaskName, exe, _statePath));
        state.CreatedAutostartTask = true;
        Persist();
```

(г) Добавить публичный метод guard (например, после `Revert`):

```csharp
    /// <summary>Если на диске остался state.json от ДРУГОЙ (незакрытой) СЗ — откатить её,
    /// прежде чем открывать новую. Иначе задачи/автостарт прошлой СЗ повиснут = след.</summary>
    public void RevertStaleState(string currentSz)
    {
        var existing = RevertStateStore.Load(_statePath);
        if (existing is not null && existing.Sz != currentSz)
            Revert(existing);
    }
```

(д) В начало `Revert` (первым шагом, до `if (state.CreatedWatchdogTask)`) добавить снятие автостарта:

```csharp
    public void Revert(RevertState state)
    {
        // Автостарт снимаем ПЕРВЫМ: если откат упадёт на середине, агент не должен
        // воскреснуть при следующем ребуте.
        if (state.CreatedAutostartTask)
            _ps.Run($"Unregister-ScheduledTask -TaskName '{state.AutostartTaskName}' -Confirm:$false " +
                    "-ErrorAction SilentlyContinue", throwOnError: false);

        // Обратный порядок; каждый шаг под флагом → повторный вызов безопасен.
        if (state.CreatedWatchdogTask)
```

- [ ] **Step 4: Запустить тесты класса — PASS**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~WindowsSystemAccessManagerTests`
Expected: PASS (включая существующие `Revert_*` и `Revert_Twice_IsIdempotent`).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/WindowsSystemAccessManager.cs tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs
git commit -m "feat(agent): Open ставит автостарт-таск + guard; Revert снимает его первым"
```

---

## Task 5: WindowsSystemAccessManager.Resume

**Files:**
- Modify: `src/SzDiag.Agent/ISystemAccessManager.cs`
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`
- Test: `tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs`

- [ ] **Step 1: Написать падающий тест Resume (с FakeSshd)**

В `tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs` добавить фейк sshd и тест:

```csharp
    private sealed class FakeSshd : ISshServer
    {
        public int StartCalls { get; private set; }
        public string? StartedTask { get; private set; }
        public void Start(int port, string authorizedKeyLine, string taskName)
        {
            StartCalls++;
            StartedTask = taskName;
        }
        public void Stop(string taskName) { }
        public string WorkDir => @"C:\nonexistent\work";
    }

    [Fact]
    public void Resume_RestartsSshdAndReschedulesWatchdog_NotUserOrFirewall()
    {
        var ps = new FakePs();
        var sshd = new FakeSshd();
        var mgr = new WindowsSystemAccessManager(ps, sshd, _statePath);
        var state = new RevertState
        {
            Sz = "156864",
            SshdTaskName = "szdiag-sshd-156864",
            WatchdogTaskName = "szdiag-watchdog-156864",
            AuthorizedKeyComment = "szdiag-156864"
        };
        var spec = new AccessSpec("156864", "svc-diag", "ssh-ed25519 AAAA", 22, TimeSpan.FromHours(6));

        mgr.Resume(state, spec);

        Assert.Equal(1, sshd.StartCalls);
        Assert.Equal("szdiag-sshd-156864", sshd.StartedTask);
        Assert.Contains(ps.Scripts, s => s.Contains("Register-ScheduledTask") && s.Contains("szdiag-watchdog-156864"));
        Assert.DoesNotContain(ps.Scripts, s => s.Contains("New-LocalUser"));
        Assert.DoesNotContain(ps.Scripts, s => s.Contains("New-NetFirewallRule"));
    }
```

- [ ] **Step 2: Запустить — FAIL (нет Resume)**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~Resume_RestartsSshd`
Expected: FAIL — компиляция не проходит (нет метода `Resume`).

- [ ] **Step 3: Добавить Resume в интерфейс и реализацию**

В `src/SzDiag.Agent/ISystemAccessManager.cs` добавить в интерфейс:

```csharp
    /// <summary>Переподнять доступ после ребута из сохранённого state (только sshd +
    /// сдвиг watchdog); user/firewall/token policy переживают ребут и не трогаются.</summary>
    void Resume(RevertState state, AccessSpec spec);
```

В `src/SzDiag.Agent/WindowsSystemAccessManager.cs` добавить реализацию (после `Open`):

```csharp
    public void Resume(RevertState state, AccessSpec spec)
    {
        // Переподнять только sshd — единственное, что умирает от ребута (транзиентная
        // задача). User/firewall/token policy/watchdog переживают ребут.
        var keyLine = $"{spec.ServicePublicKey.Trim()} {state.AuthorizedKeyComment}";
        _sshd.Start(spec.SshPort, keyLine, state.SshdTaskName);

        // Пересоздать watchdog с новым дедлайном (-Force): серия ребутов под стрессом
        // продлевает сессию, а не грохает её протухшим -Once из прошлой загрузки.
        _ps.Run(BuildWatchdogTaskCommand(state.WatchdogTaskName, Environment.ProcessPath!,
            _statePath, DateTime.Now.Add(spec.WatchdogTimeout)));
    }
```

**Важно:** добавление `Resume` в интерфейс ломает компиляцию `FakeManager` в
`tests/SzDiag.Agent.Tests/AgentSessionTests.cs` (он реализует `ISystemAccessManager`).
Сразу обновить `FakeManager` — добавить счётчик и метод (используется тестами Task 6):

```csharp
        public int OpenCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public int ResumeCalls { get; private set; }
```

```csharp
        public void Revert(RevertState state) => RevertCalls++;
        public void Resume(RevertState state, AccessSpec spec) => ResumeCalls++;
```

- [ ] **Step 4: Запустить тест + весь набор — PASS**

Run: `dotnet test tests/SzDiag.Agent.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/ISystemAccessManager.cs src/SzDiag.Agent/WindowsSystemAccessManager.cs tests/SzDiag.Agent.Tests/WindowsSystemAccessManagerTests.cs
git commit -m "feat(agent): Resume — переподнять sshd + сдвинуть watchdog после ребута"
```

---

## Task 6: AgentSession.ResumeAsync + Completion

**Files:**
- Modify: `src/SzDiag.Agent/AgentSession.cs`
- Test: `tests/SzDiag.Agent.Tests/AgentSessionTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`FakeManager` уже получил `ResumeCalls` и метод `Resume` в Task 5. В
`tests/SzDiag.Agent.Tests/AgentSessionTests.cs` добавить два теста:

```csharp
    [Fact]
    public async Task ResumeAsync_ResumesAccessConnectsAndRegisters()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.ResumeAsync(new RevertState { Sz = "156864" });

        Assert.Equal(1, mgr.ResumeCalls);
        Assert.Equal(0, mgr.OpenCalls);
        Assert.True(link.Connected);
        Assert.Equal("156864", link.RegisteredSz);
    }

    [Fact]
    public async Task Completion_CompletesAfterRevert()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");
        await session.ResumeAsync(new RevertState { Sz = "156864" });
        Assert.False(session.Completion.IsCompleted);

        await link.FireRevert("156864");

        Assert.True(session.Completion.IsCompleted);
    }
```

- [ ] **Step 2: Запустить — FAIL**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AgentSessionTests`
Expected: FAIL — нет `ResumeAsync`/`Completion`.

- [ ] **Step 3: Реализовать ResumeAsync и Completion**

В `src/SzDiag.Agent/AgentSession.cs` добавить поле и свойство (после `private RevertState? _state;`):

```csharp
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Завершается после отката (hub/watchdog/локально) — сигнал headless-режиму выйти.</summary>
    public Task Completion => _completed.Task;
```

Добавить метод (после `StartAsync`):

```csharp
    /// <summary>Возобновление после ребута: state загружен с диска, доступ переподнимается
    /// (Resume, не Open), дальше как обычная сессия — connect + register под тем же СЗ.</summary>
    public async Task ResumeAsync(RevertState loaded, CancellationToken ct = default)
    {
        _state = loaded;
        _manager.Resume(loaded, _spec);
        _link.OnRevert(async _ => await _coordinator.TriggerAsync());
        await _link.ConnectAsync(ct);
        await _link.RegisterAsync(_spec.Sz, _hostname, ct);
    }
```

В `DoRevertAsync` добавить сигнал завершения последней строкой:

```csharp
    private async Task DoRevertAsync()
    {
        if (_state is not null) _manager.Revert(_state);
        await _link.DisposeAsync();
        _completed.TrySetResult();
    }
```

- [ ] **Step 4: Запустить тесты класса + весь набор — PASS**

Run: `dotnet test tests/SzDiag.Agent.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/AgentSession.cs tests/SzDiag.Agent.Tests/AgentSessionTests.cs
git commit -m "feat(agent): AgentSession.ResumeAsync + Completion (сигнал отката)"
```

---

## Task 7: Вынести обработчики RunTests/RunDiag + heartbeat в AgentCommandWiring

**Files:**
- Create: `src/SzDiag.Agent/AgentCommandWiring.cs`
- Modify: `src/SzDiag.Agent/Program.cs`

Чистый рефактор: перенести регистрацию обработчиков и heartbeat-цикла из интерактивной ветки `Program.cs` в переиспользуемый класс (нужен и resume-ветке — DRY). Поведение интерактивной ветки не меняется; покрыто существующим набором + сборкой.

- [ ] **Step 1: Создать AgentCommandWiring**

Создать `src/SzDiag.Agent/AgentCommandWiring.cs`:

```csharp
namespace SzDiag.Agent;

/// <summary>Общая для интерактивной и resume-веток проводка: обработчики RunTests/RunDiag
/// и фоновый heartbeat-цикл. announce(plain, markup) — вывод (markup=null → без разметки).</summary>
public static class AgentCommandWiring
{
    public static void RegisterHandlers(
        IHubLink link, string hostname, PowerShellRunner ps,
        string testSuitePath, Action<string, string?> announce)
    {
        // RunTests: по команде hub прогнать набор из testsuite.json и залить отчёт.
        if (File.Exists(testSuitePath))
        {
            var suite = TestSuite.Load(testSuitePath);
            var reportRunner = new TestReportRunner(
                new TestRunner(new PowerShellCommandExecutor(ps), new GdiScreenCapturer()),
                suite, link, hostname);
            link.OnRunTests(async (runSz, filter) =>
            {
                var scope = string.IsNullOrWhiteSpace(filter) ? "полный прогон" : $"фильтр {filter}";
                announce($"Прогон тестов для СЗ {runSz} ({scope})…", null);
                try
                {
                    var outcome = await reportRunner.RunAndUploadAsync(runSz, filter);
                    if (!outcome.Ran)
                    {
                        var ids = string.Join(", ", outcome.AvailableIds);
                        announce($"Не найдено шагов по фильтру '{filter}'. Доступные: {ids}", null);
                        await link.ReportActivityAsync(runSz, "— готов", null);
                    }
                    else
                    {
                        announce("Отчёт залит на hub.", null);
                        var mark = outcome.AllClean ? "✓" : "⚠";
                        await link.ReportActivityAsync(runSz, $"готов · последний: {outcome.RanLabel} {mark}", null);
                    }
                }
                catch (Exception ex)
                {
                    announce($"Ошибка прогона: {ex.Message}", null);
                    try { await link.ReportActivityAsync(runSz, "готов · последний: ошибка ⚠", null); } catch { }
                }
            });
        }

        // RunDiag: read-only снапшот (каталог проб встроен — работает всегда).
        var diagRunner = new DiagReportRunner(
            new TestRunner(new PowerShellCommandExecutor(ps), new GdiScreenCapturer()),
            DiagnosticProbes.Suite, link, hostname);
        link.OnRunDiag(async (runSz, sections) =>
        {
            var scope = string.IsNullOrWhiteSpace(sections) ? "все секции" : $"секции {sections}";
            announce($"Диагностика СЗ {runSz} ({scope})…", null);
            try
            {
                var outcome = await diagRunner.RunAndUploadAsync(runSz, sections);
                if (!outcome.Ran)
                {
                    var s = string.Join(", ", outcome.AvailableSections);
                    announce($"Не найдено секций по фильтру '{sections}'. Доступные: {s}", null);
                    await link.ReportActivityAsync(runSz, "— готов", null);
                }
                else
                {
                    announce("Диаг-отчёт залит на hub.", null);
                    await link.ReportActivityAsync(runSz, $"готов · диагностика: {outcome.RanLabel}", null);
                }
            }
            catch (Exception ex)
            {
                announce($"Ошибка диагностики: {ex.Message}", null);
                try { await link.ReportActivityAsync(runSz, "готов · диагностика: ошибка ⚠", null); } catch { }
            }
        });
    }

    /// <summary>Фоновый heartbeat-цикл до отмены. Ошибки глотаются (SignalR переподключается).</summary>
    public static Task StartHeartbeatLoop(AgentSession session, int heartbeatSeconds, CancellationToken ct) =>
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await session.HeartbeatOnceAsync(ct); } catch { /* переподключение SignalR */ }
                try { await Task.Delay(TimeSpan.FromSeconds(heartbeatSeconds), ct); }
                catch (OperationCanceledException) { break; }
            }
        });
}
```

- [ ] **Step 2: Переключить интерактивную ветку Program.cs на AgentCommandWiring**

В `src/SzDiag.Agent/Program.cs` заменить блок регистрации RunTests-обработчика (от `var suitePath = ResolvePath(opts.TestSuitePath);` до закрывающей `});` RunDiag-обработчика, т.е. текущие строки ~122–189) на один вызов:

```csharp
AgentCommandWiring.RegisterHandlers(link, Environment.MachineName, ps,
    ResolvePath(opts.TestSuitePath), (plain, markup) => Announce(plain, markup));
```

И заменить фоновый heartbeat-блок (`using var cts = ...; var heartbeat = Task.Run(async () => { ... });`, строки ~195–204) на:

```csharp
using var cts = new CancellationTokenSource();
var heartbeat = AgentCommandWiring.StartHeartbeatLoop(session, (int)opts.HeartbeatSeconds, cts.Token);
```

(Остальное — блок клавиш C/Q, `cts.Cancel()`, `await heartbeat` — без изменений.)

- [ ] **Step 3: Сборка + весь набор тестов зелёные**

Run: `dotnet build; dotnet test`
Expected: BUILD OK, все ~180 тестов PASS. Интерактивное поведение не изменилось.

- [ ] **Step 4: Commit**

```bash
git add src/SzDiag.Agent/AgentCommandWiring.cs src/SzDiag.Agent/Program.cs
git commit -m "refactor(agent): вынести RunTests/RunDiag/heartbeat в AgentCommandWiring"
```

---

## Task 8: Ветка --resume в Program.cs

**Files:**
- Modify: `src/SzDiag.Agent/Program.cs`

Headless-режим: поднимается автостарт-таском после ребута. Не читает консоль, живёт до отката от hub/watchdog. Логи — только в файл (`AgentLog`). Проверяется сборкой + живым e2e (юнит-тесты top-level не покрывают — это проводка уже протестированных компонентов).

- [ ] **Step 1: Добавить ветку --resume после ветки --revert**

В `src/SzDiag.Agent/Program.cs` сразу после закрывающей `}` блока `if (args.Length >= 2 && args[0] == "--revert") { ... }` (перед `try {` основного потока) вставить:

```csharp
// Режим возобновления после ребута: agent.exe --resume <statePath>. Поднимается
// автостарт-задачей под SYSTEM (headless, без консоли). Переподнимает sshd и
// реконнектится под тем же СЗ из persisted state; живёт до отката от hub/watchdog.
if (args.Length >= 2 && args[0] == "--resume")
{
    var state = RevertStateStore.Load(args[1]);
    if (state is null)
    {
        logFile.WriteLine("[resume] state.json отсутствует — возобновлять нечего.");
        logFile.Flush();
        return 0;
    }

    var rOpts = new AgentOptions();
    config.Bind(rOpts);
    string R(string p) => Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);

    var rPubKey = File.ReadAllText(R(rOpts.ServicePublicKeyPath));
    var rSpec = new AccessSpec(state.Sz, rOpts.ServiceAccount, rPubKey, rOpts.SshPort,
        TimeSpan.FromHours(rOpts.WatchdogHours));
    var rSshd = new PortableSshServer(R(rOpts.SshBinDir), rOpts.SshWorkDir, ps);
    var rManager = new WindowsSystemAccessManager(ps, rSshd, args[1]);

    var rHubUrl = rOpts.HubUrl;
    if (string.IsNullOrWhiteSpace(rHubUrl))
    {
        try { rHubUrl = await HubDiscovery.FindHubAsync(rOpts.AgentToken); }
        catch (HubNotFoundException ex)
        {
            logFile.WriteLine($"[resume] hub не найден: {ex.Message}");
            logFile.Flush();
            return 1; // автостарт повторит при следующем ребуте
        }
    }

    var rLink = new SignalRHubLink(rHubUrl, rOpts.AgentToken);
    var rSession = new AgentSession(rManager, rLink, rSpec, Environment.MachineName);

    // Ребут мог случиться быстрее, чем поднялась сеть — bounded-ретрай подъёма.
    const int maxAttempts = 10;
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            logFile.WriteLine($"[resume] СЗ {state.Sz}: переподнимаю доступ (попытка {attempt})…");
            await rSession.ResumeAsync(state);
            break;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logFile.WriteLine($"[resume] попытка {attempt} не удалась: {ex.Message}; retry через 30с");
            logFile.Flush();
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
    }

    logFile.WriteLine($"[resume] СЗ {state.Sz}: online (после ребута).");
    logFile.Flush();
    try { await rLink.ReportActivityAsync(state.Sz, "— готов (после ребута)", null); } catch { }

    AgentCommandWiring.RegisterHandlers(rLink, Environment.MachineName, ps,
        R(rOpts.TestSuitePath), (plain, _) => { logFile.WriteLine(plain); logFile.Flush(); });

    using var rCts = new CancellationTokenSource();
    var rHeartbeat = AgentCommandWiring.StartHeartbeatLoop(rSession, (int)rOpts.HeartbeatSeconds, rCts.Token);

    await rSession.Completion; // ждём отката от hub (close) или watchdog
    rCts.Cancel();
    try { await rHeartbeat; } catch { }
    logFile.WriteLine($"[resume] СЗ {state.Sz}: сессия закрыта, откат выполнен.");
    logFile.Flush();
    return 0;
}
```

- [ ] **Step 2: Сборка зелёная**

Run: `dotnet build`
Expected: BUILD OK.

- [ ] **Step 3: Полный набор тестов зелёный**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/SzDiag.Agent/Program.cs
git commit -m "feat(agent): режим --resume — авто-возобновление сессии после ребута"
```

---

## Task 9: Обновить статус в CLAUDE.md и пометить спеку реализованной

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/superpowers/specs/2026-07-24-agent-survive-reboot-design.md`

- [ ] **Step 1: Пометить спеку реализованной**

В `docs/superpowers/specs/2026-07-24-agent-survive-reboot-design.md` заменить строку статуса:

```markdown
**Статус:** дизайн, готов к плану реализации
```

на:

```markdown
**Статус:** реализовано (2026-07-24). План: docs/superpowers/plans/2026-07-24-agent-survive-reboot.md
```

- [ ] **Step 2: Обновить открытое направление в CLAUDE.md**

В `CLAUDE.md` в списке «Открытые направления» у пункта «**Агент переживает ребут + авто-реконнект…**» заменить финальную «Спеки нет.» на:

```markdown
  **Реализовано (2026-07-24):** режим `agent.exe --resume` + автостарт-таск `-AtStartup`
  под SYSTEM (ставится в `Open` шаг 8, снимается в `Revert` первым). После ребута агент
  сам переподнимает sshd и реконнектится под тем же СЗ из `state.json`; watchdog при
  resume сдвигается. Спека/план:
  [docs/superpowers/specs/2026-07-24-agent-survive-reboot-design.md](docs/superpowers/specs/2026-07-24-agent-survive-reboot-design.md).
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs/superpowers/specs/2026-07-24-agent-survive-reboot-design.md
git commit -m "docs: агент-переживает-ребут реализован — статус в CLAUDE.md/спеке"
```

---

## Известные ограничения (вне scope, отметить при живом прогоне)

- **Watchdog-ревёрт в headless-режиме** запускается ОТДЕЛЬНЫМ процессом (`agent.exe --revert`),
  и текущий resume-процесс об этом не узнаёт (останется висеть с мёртвым link до следующего
  ребута). Нормальный путь закрытия headless-сессии — команда `close` с hub (→ `OnRevert` →
  `Completion` → выход). Watchdog — бэкстоп «забыли закрыть». Единый агентский таймер выживания —
  отдельное будущее направление.
- **Discovery на -AtStartup**: если сеть не поднялась к моменту старта таска, discovery
  ретраится (bounded, 10×30с ≈ 5 мин), затем процесс выходит (автостарт повторит при
  следующем ребуте).
- Юнит-тесты не покрывают top-level `--resume`/`--revert` проводку и живой `Open`/`sshd.Start`
  — это валидируется сборкой + живым e2e (см. docs/TESTING.md), как и остальной sshd-путь.

## Manual E2E (после реализации, на онлайн-СЗ)

1. `.\tools\build-dist.ps1`, раскатать клиента, открыть СЗ интерактивно.
2. Проверить, что появилась задача `szdiag-autostart-<СЗ>` (`Get-ScheduledTask szdiag-autostart-*`).
3. Ребутнуть клиента → убедиться, что СЗ сама вернулась online в `szcli watch` без ручного старта.
4. `szcli close <СЗ>` → убедиться, что задача `szdiag-autostart-<СЗ>` снята (следа нет).
```
