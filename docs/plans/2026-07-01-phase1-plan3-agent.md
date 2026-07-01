# sz-diag Фаза 1 — План 3: агент на клиентской машине

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Интерактивный `sz-agent.exe`: спрашивает номер СЗ, открывает SSH-доступ (svc-diag + pre-shared публичный ключ, OpenSSH, firewall, token-policy, watchdog), подключается к hub и держит статус online, а по любому из триггеров (CLI, локально `[C]`, закрытие окна, watchdog) идемпотентно откатывает всё до нуля следов.

**Architecture:** Windows-операции спрятаны за `ISystemAccessManager`, поэтому оркестрация тестируется юнитами с фейком. `WindowsSystemAccessManager` вызывает PowerShell и ведёт `RevertState` (персистится в файл — переживает краш и используется watchdog'ом). Связь с hub — за `IHubLink` (реальная реализация над SignalR-клиентом). `RevertCoordinator` гарантирует единичный откат при любом числе триггеров.

**Tech Stack:** .NET 8, C#, Microsoft.AspNetCore.SignalR.Client, System.Text.Json, P/Invoke (SetConsoleCtrlHandler), PowerShell (вызовы из агента).

**Предпосылка:** реализованы Планы 1 и 2.

Спека: [../specs/2026-07-01-sz-diag-phase1-design.md](../specs/2026-07-01-sz-diag-phase1-design.md)

> **Важно про тестирование:** `WindowsSystemAccessManager` меняет системное состояние (учётки, службы, firewall, реестр) → он НЕ покрывается юнит-тестами. Его проверка — интеграционный чек-лист на одноразовой Windows-ВМ (Task 6). Юнитами покрыта вся остальная логика через фейки.

---

## File Structure

```
src/
  SzDiag.Agent/
    SzDiag.Agent.csproj      — self-contained exe + манифест админа
    app.manifest             — requireAdministrator
    AccessSpec.cs            — параметры открытия доступа
    RevertState.cs           — что было применено (для отката), (де)сериализация
    RevertStateStore.cs      — сохранение/загрузка RevertState в файл
    ISystemAccessManager.cs  — абстракция Windows-операций
    RevertCoordinator.cs     — единичный идемпотентный откат
    IHubLink.cs              — абстракция связи с hub
    AgentSession.cs          — оркестрация open→register→heartbeat→revert
    WindowsSystemAccessManager.cs — реальная реализация (PowerShell)
    PowerShellRunner.cs      — запуск PS-команд
    SignalRHubLink.cs        — реальная связь по SignalR
    AgentOptions.cs          — hub url, токен, путь к pub-ключу, порт, watchdog
    Program.cs               — интерактив, wiring, перехват закрытия окна
tests/
  SzDiag.Agent.Tests/
    SzDiag.Agent.Tests.csproj
    RevertStateStoreTests.cs
    RevertCoordinatorTests.cs
    AgentSessionTests.cs
```

---

### Task 0: Скелет проекта агента + манифест админа

**Files:**
- Create: `src/SzDiag.Agent/SzDiag.Agent.csproj`, `src/SzDiag.Agent/app.manifest`, `tests/SzDiag.Agent.Tests/SzDiag.Agent.Tests.csproj`

- [ ] **Step 1: Создать проекты**

Run:
```bash
dotnet new console -n SzDiag.Agent -o src/SzDiag.Agent -f net8.0
dotnet new xunit -n SzDiag.Agent.Tests -o tests/SzDiag.Agent.Tests -f net8.0
dotnet sln add src/SzDiag.Agent tests/SzDiag.Agent.Tests
dotnet add src/SzDiag.Agent reference src/SzDiag.Contracts
dotnet add tests/SzDiag.Agent.Tests reference src/SzDiag.Agent src/SzDiag.Contracts
dotnet add src/SzDiag.Agent package Microsoft.AspNetCore.SignalR.Client
```

- [ ] **Step 2: Манифест требования админа**

`src/SzDiag.Agent/app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
</assembly>
```

В `src/SzDiag.Agent/SzDiag.Agent.csproj` в `<PropertyGroup>` добавить:
```xml
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <Nullable>enable</Nullable>
```

- [ ] **Step 3: Удалить шаблонный тест**

Удалить `tests/SzDiag.Agent.Tests/UnitTest1.cs`.

- [ ] **Step 4: Сборка**

Run: `dotnet build src/SzDiag.Agent`
Expected: Build succeeded (манифест применится при публикации exe).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "chore: скелет проекта агента с манифестом админа"
```

---

### Task 1: AccessSpec и RevertState + персистенс

**Files:**
- Create: `src/SzDiag.Agent/AccessSpec.cs`, `src/SzDiag.Agent/RevertState.cs`, `src/SzDiag.Agent/RevertStateStore.cs`
- Test: `tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs`

- [ ] **Step 1: Написать модели**

`src/SzDiag.Agent/AccessSpec.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Параметры открытия доступа на клиентской машине.</summary>
public sealed record AccessSpec(
    string Sz,
    string ServiceAccount,     // "svc-diag"
    string ServicePublicKey,   // содержимое публичного ключа сервиса
    int SshPort,
    TimeSpan WatchdogTimeout);
```

`src/SzDiag.Agent/RevertState.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Что именно применено при открытии — чтобы откатить только это и идемпотентно.</summary>
public sealed class RevertState
{
    public string Sz { get; set; } = "";
    public string ServiceAccount { get; set; } = "svc-diag";
    public string FirewallRuleName { get; set; } = "";
    public string WatchdogTaskName { get; set; } = "";
    public string AuthorizedKeyComment { get; set; } = "";

    public bool CreatedUser { get; set; }
    public bool InstalledOpenSsh { get; set; }
    public bool StartedSshService { get; set; }
    public bool AddedFirewallRule { get; set; }
    public bool WroteAuthorizedKey { get; set; }
    public bool CreatedAuthorizedKeysFile { get; set; }
    public bool SetTokenPolicy { get; set; }

    /// <summary>Прежнее значение LocalAccountTokenFilterPolicy: null = отсутствовало.</summary>
    public int? TokenPolicyPreviousValue { get; set; }
    public bool CreatedWatchdogTask { get; set; }
}
```

`src/SzDiag.Agent/RevertStateStore.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Agent;

/// <summary>Сохранение/загрузка RevertState в файл (переживает краш; читается watchdog'ом).</summary>
public static class RevertStateStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static void Save(string path, RevertState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, Opts));
    }

    public static RevertState? Load(string path)
        => File.Exists(path) ? JsonSerializer.Deserialize<RevertState>(File.ReadAllText(path)) : null;

    public static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs`:
```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class RevertStateStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"szstate-{Guid.NewGuid():N}", "state.json");

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var state = new RevertState
        {
            Sz = "156864",
            CreatedUser = true,
            SetTokenPolicy = true,
            TokenPolicyPreviousValue = null,
            FirewallRuleName = "szdiag-ssh"
        };

        RevertStateStore.Save(_path, state);
        var loaded = RevertStateStore.Load(_path);

        Assert.NotNull(loaded);
        Assert.Equal("156864", loaded!.Sz);
        Assert.True(loaded.CreatedUser);
        Assert.True(loaded.SetTokenPolicy);
        Assert.Null(loaded.TokenPolicyPreviousValue);
        Assert.Equal("szdiag-ssh", loaded.FirewallRuleName);
    }

    [Fact]
    public void Load_Missing_ReturnsNull() => Assert.Null(RevertStateStore.Load(_path));

    [Fact]
    public void Delete_RemovesFile()
    {
        RevertStateStore.Save(_path, new RevertState());
        RevertStateStore.Delete(_path);
        Assert.False(File.Exists(_path));
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter RevertStateStoreTests`
Expected: FAIL — типы не существуют.

- [ ] **Step 4: (реализация уже написана в Step 1)** — просто пересобрать.

Run: `dotnet test tests/SzDiag.Agent.Tests --filter RevertStateStoreTests`
Expected: PASS (3 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/AccessSpec.cs src/SzDiag.Agent/RevertState.cs src/SzDiag.Agent/RevertStateStore.cs tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs
git commit -m "feat(agent): AccessSpec, RevertState и персистенс состояния отката"
```

---

### Task 2: ISystemAccessManager + RevertCoordinator

**Files:**
- Create: `src/SzDiag.Agent/ISystemAccessManager.cs`, `src/SzDiag.Agent/RevertCoordinator.cs`
- Test: `tests/SzDiag.Agent.Tests/RevertCoordinatorTests.cs`

- [ ] **Step 1: Написать интерфейс менеджера**

`src/SzDiag.Agent/ISystemAccessManager.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Windows-операции открытия/отката доступа. Реализация меняет систему.</summary>
public interface ISystemAccessManager
{
    /// <summary>Открыть доступ по spec. Возвращает состояние для последующего отката.</summary>
    RevertState Open(AccessSpec spec);

    /// <summary>Откатить только применённые шаги. Обязана быть идемпотентной.</summary>
    void Revert(RevertState state);
}
```

- [ ] **Step 2: Написать падающие тесты координатора**

`tests/SzDiag.Agent.Tests/RevertCoordinatorTests.cs`:
```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class RevertCoordinatorTests
{
    [Fact]
    public async Task Trigger_RunsActionOnce()
    {
        var count = 0;
        var coord = new RevertCoordinator(() => { count++; return Task.CompletedTask; });

        await coord.TriggerAsync();
        await coord.TriggerAsync();
        await coord.TriggerAsync();

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Trigger_Concurrent_RunsActionOnce()
    {
        var count = 0;
        var coord = new RevertCoordinator(async () =>
        {
            await Task.Delay(50);
            Interlocked.Increment(ref count);
        });

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => coord.TriggerAsync()));

        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter RevertCoordinatorTests`
Expected: FAIL — `RevertCoordinator` не существует.

- [ ] **Step 4: Реализовать RevertCoordinator**

`src/SzDiag.Agent/RevertCoordinator.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Гарантирует, что откат выполнится ровно один раз при любом числе триггеров.</summary>
public sealed class RevertCoordinator
{
    private readonly Func<Task> _action;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public RevertCoordinator(Func<Task> action) => _action = action;

    public async Task TriggerAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_done) return;
            _done = true;
            await _action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter RevertCoordinatorTests`
Expected: PASS (2 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Agent/ISystemAccessManager.cs src/SzDiag.Agent/RevertCoordinator.cs tests/SzDiag.Agent.Tests/RevertCoordinatorTests.cs
git commit -m "feat(agent): ISystemAccessManager + единичный RevertCoordinator"
```

---

### Task 3: IHubLink + AgentSession (оркестрация)

**Files:**
- Create: `src/SzDiag.Agent/IHubLink.cs`, `src/SzDiag.Agent/AgentSession.cs`
- Test: `tests/SzDiag.Agent.Tests/AgentSessionTests.cs`

- [ ] **Step 1: Написать IHubLink**

`src/SzDiag.Agent/IHubLink.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Связь агента с hub. Реальная реализация — над SignalR-клиентом.</summary>
public interface IHubLink
{
    Task ConnectAsync(CancellationToken ct = default);
    Task RegisterAsync(string sz, string hostname, CancellationToken ct = default);
    Task HeartbeatAsync(string sz, CancellationToken ct = default);

    /// <summary>Подписка на команду revert от hub (sz → callback).</summary>
    void OnRevert(Func<string, Task> handler);

    ValueTask DisposeAsync();
}
```

- [ ] **Step 2: Написать падающие тесты AgentSession**

`tests/SzDiag.Agent.Tests/AgentSessionTests.cs`:
```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class AgentSessionTests
{
    private sealed class FakeManager : ISystemAccessManager
    {
        public int OpenCalls { get; private set; }
        public int RevertCalls { get; private set; }
        public RevertState Open(AccessSpec spec)
        {
            OpenCalls++;
            return new RevertState { Sz = spec.Sz };
        }
        public void Revert(RevertState state) => RevertCalls++;
    }

    private sealed class FakeHubLink : IHubLink
    {
        public bool Connected { get; private set; }
        public string? RegisteredSz { get; private set; }
        public int Heartbeats { get; private set; }
        public bool Disposed { get; private set; }
        private Func<string, Task>? _onRevert;

        public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }
        public Task RegisterAsync(string sz, string hostname, CancellationToken ct = default) { RegisteredSz = sz; return Task.CompletedTask; }
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) { Heartbeats++; return Task.CompletedTask; }
        public void OnRevert(Func<string, Task> handler) => _onRevert = handler;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }

        public Task FireRevert(string sz) => _onRevert!(sz);
    }

    private static AccessSpec Spec() =>
        new("156864", "svc-diag", "ssh-ed25519 AAAA...", 22, TimeSpan.FromHours(6));

    [Fact]
    public async Task StartAsync_OpensAccessConnectsAndRegisters()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");

        await session.StartAsync();

        Assert.Equal(1, mgr.OpenCalls);
        Assert.True(link.Connected);
        Assert.Equal("156864", link.RegisteredSz);
    }

    [Fact]
    public async Task HeartbeatOnceAsync_SendsHeartbeat()
    {
        var session = new AgentSession(new FakeManager(), new FakeHubLink() is var l ? l : null!, Spec(), "PC-1");
        await session.StartAsync();
        await session.HeartbeatOnceAsync();
        // повторный доступ к тому же link через рефлексию не нужен: проверяем через новый сценарий ниже
    }

    [Fact]
    public async Task RevertFromHub_RevertsOnceAndDisposesLink()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await link.FireRevert("156864");
        await link.FireRevert("156864"); // повторный триггер

        Assert.Equal(1, mgr.RevertCalls);
        Assert.True(link.Disposed);
    }

    [Fact]
    public async Task RevertLocalAsync_RevertsOnce()
    {
        var mgr = new FakeManager();
        var link = new FakeHubLink();
        var session = new AgentSession(mgr, link, Spec(), "PC-1");
        await session.StartAsync();

        await session.RevertAsync();
        await session.RevertAsync();

        Assert.Equal(1, mgr.RevertCalls);
    }
}
```

> Примечание: тест `HeartbeatOnceAsync_SendsHeartbeat` упрощён — держите ссылку на `link` в переменной и проверяйте `link.Heartbeats == 1`. Замените его тело на явный вариант (см. Step 4 после реализации): создать `link`, передать в сессию, после `HeartbeatOnceAsync()` проверить `Assert.Equal(1, link.Heartbeats)`.

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter AgentSessionTests`
Expected: FAIL — `AgentSession` не существует.

- [ ] **Step 4: Реализовать AgentSession и поправить тест heartbeat**

`src/SzDiag.Agent/AgentSession.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Оркестрация сессии агента: открыть доступ, подключиться, регистрировать,
/// слать heartbeat, идемпотентно откатывать по любому триггеру.</summary>
public sealed class AgentSession
{
    private readonly ISystemAccessManager _manager;
    private readonly IHubLink _link;
    private readonly AccessSpec _spec;
    private readonly string _hostname;
    private readonly RevertCoordinator _coordinator;
    private RevertState? _state;

    public AgentSession(ISystemAccessManager manager, IHubLink link, AccessSpec spec, string hostname)
    {
        _manager = manager;
        _link = link;
        _spec = spec;
        _hostname = hostname;
        _coordinator = new RevertCoordinator(DoRevertAsync);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        _state = _manager.Open(_spec);
        _link.OnRevert(async _ => await _coordinator.TriggerAsync());
        await _link.ConnectAsync(ct);
        await _link.RegisterAsync(_spec.Sz, _hostname, ct);
    }

    public Task HeartbeatOnceAsync(CancellationToken ct = default) => _link.HeartbeatAsync(_spec.Sz, ct);

    /// <summary>Локальный/watchdog/консоль-триггер отката.</summary>
    public Task RevertAsync() => _coordinator.TriggerAsync();

    private async Task DoRevertAsync()
    {
        if (_state is not null) _manager.Revert(_state);
        await _link.DisposeAsync();
    }
}
```

Поправить тест `HeartbeatOnceAsync_SendsHeartbeat` в `AgentSessionTests.cs` на:
```csharp
    [Fact]
    public async Task HeartbeatOnceAsync_SendsHeartbeat()
    {
        var link = new FakeHubLink();
        var session = new AgentSession(new FakeManager(), link, Spec(), "PC-1");
        await session.StartAsync();

        await session.HeartbeatOnceAsync();

        Assert.Equal(1, link.Heartbeats);
    }
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter AgentSessionTests`
Expected: PASS (4 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Agent/IHubLink.cs src/SzDiag.Agent/AgentSession.cs tests/SzDiag.Agent.Tests/AgentSessionTests.cs
git commit -m "feat(agent): IHubLink и оркестратор AgentSession с идемпотентным откатом"
```

---

### Task 4: PowerShellRunner

**Files:**
- Create: `src/SzDiag.Agent/PowerShellRunner.cs`

Тонкая обёртка запуска PS. Проверяется в составе VM-чек-листа (Task 6).

- [ ] **Step 1: Реализовать**

`src/SzDiag.Agent/PowerShellRunner.cs`:
```csharp
using System.Diagnostics;

namespace SzDiag.Agent;

public sealed record PsResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Запуск PowerShell-команд. Кидает при ненулевом коде, если throwOnError.</summary>
public sealed class PowerShellRunner
{
    public PsResult Run(string script, bool throwOnError = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command -",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine(script);
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();

        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell завершился с кодом {p.ExitCode}: {stderr}");

        return new PsResult(p.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 2: Сборка**

Run: `dotnet build src/SzDiag.Agent`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Agent/PowerShellRunner.cs
git commit -m "feat(agent): PowerShellRunner"
```

---

### Task 5: WindowsSystemAccessManager (реальная реализация)

**Files:**
- Create: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`

> Юнит-тестами не покрывается (меняет систему). Верификация — Task 6 на ВМ.
> Порядок отката — обратный открытию; каждый шаг проверяет флаг в RevertState → идемпотентность.

- [ ] **Step 1: Реализовать менеджер**

`src/SzDiag.Agent/WindowsSystemAccessManager.cs`:
```csharp
using System.Security.Cryptography;

namespace SzDiag.Agent;

/// <summary>
/// Реальная Windows-реализация. Open применяет шаги и прогрессивно пишет RevertState
/// в файл (переживает краш). Revert откатывает по флагам, обратный порядок, идемпотентно.
/// Ключ администратора кладётся в administrators_authorized_keys (OpenSSH на Windows
/// игнорирует per-user authorized_keys для членов Administrators).
/// </summary>
public sealed class WindowsSystemAccessManager : ISystemAccessManager
{
    private const string AdminsSid = "S-1-5-32-544";
    private const string TokenPolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private static readonly string AdminAuthKeys =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys");

    private readonly PowerShellRunner _ps;
    private readonly string _statePath;

    public WindowsSystemAccessManager(PowerShellRunner ps, string statePath)
    {
        _ps = ps;
        _statePath = statePath;
    }

    public RevertState Open(AccessSpec spec)
    {
        var state = new RevertState
        {
            Sz = spec.Sz,
            ServiceAccount = spec.ServiceAccount,
            FirewallRuleName = $"szdiag-ssh-{spec.Sz}",
            WatchdogTaskName = $"szdiag-watchdog-{spec.Sz}",
            AuthorizedKeyComment = $"szdiag-{spec.Sz}"
        };
        void Persist() => RevertStateStore.Save(_statePath, state);
        Persist();

        // 1. OpenSSH Server
        var installed = _ps.Run(
            "(Get-WindowsCapability -Online -Name 'OpenSSH.Server*').State").StdOut;
        if (!installed.Contains("Installed"))
        {
            _ps.Run("Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0");
            state.InstalledOpenSsh = true;
            Persist();
        }

        // 2. Служба sshd
        var running = _ps.Run("(Get-Service sshd -ErrorAction SilentlyContinue).Status").StdOut;
        if (!running.Contains("Running"))
        {
            _ps.Run("Set-Service sshd -StartupType Automatic; Start-Service sshd");
            state.StartedSshService = true;
            Persist();
        }

        // 3. Firewall
        _ps.Run($"New-NetFirewallRule -Name '{state.FirewallRuleName}' " +
                $"-DisplayName '{state.FirewallRuleName}' -Enabled True -Direction Inbound " +
                $"-Protocol TCP -Action Allow -LocalPort {spec.SshPort}");
        state.AddedFirewallRule = true;
        Persist();

        // 4. LocalAccountTokenFilterPolicy
        var prev = _ps.Run(
            $"(Get-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
            "-ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy").StdOut.Trim();
        state.TokenPolicyPreviousValue = int.TryParse(prev, out var pv) ? pv : null;
        if (state.TokenPolicyPreviousValue != 1)
        {
            _ps.Run($"New-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
                    "-Value 1 -PropertyType DWord -Force");
            state.SetTokenPolicy = true;
            Persist();
        }

        // 5. Учётка svc-diag (админ)
        var password = GeneratePassword();
        _ps.Run($"net user {spec.ServiceAccount} '{password}' /add");
        _ps.Run($"Add-LocalGroupMember -SID {AdminsSid} -Member {spec.ServiceAccount}");
        state.CreatedUser = true;
        Persist();

        // 6. administrators_authorized_keys + ACL
        var keyLine = $"{spec.ServicePublicKey.Trim()} {state.AuthorizedKeyComment}";
        if (!File.Exists(AdminAuthKeys))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AdminAuthKeys)!);
            File.WriteAllText(AdminAuthKeys, keyLine + Environment.NewLine);
            state.CreatedAuthorizedKeysFile = true;
            _ps.Run($"icacls '{AdminAuthKeys}' /inheritance:r " +
                    "/grant 'SYSTEM:F' /grant 'BUILTIN\\Administrators:F'");
        }
        else
        {
            File.AppendAllText(AdminAuthKeys, keyLine + Environment.NewLine);
        }
        state.WroteAuthorizedKey = true;
        Persist();

        // 7. Watchdog scheduled task (запускает этот exe с --revert по таймауту)
        var exe = Environment.ProcessPath!;
        var runAt = DateTime.Now.Add(spec.WatchdogTimeout).ToString("yyyy-MM-ddTHH:mm:ss");
        _ps.Run(
            $"$a = New-ScheduledTaskAction -Execute '{exe}' -Argument '--revert \"{_statePath}\"'; " +
            $"$t = New-ScheduledTaskTrigger -Once -At '{runAt}'; " +
            $"Register-ScheduledTask -TaskName '{state.WatchdogTaskName}' -Action $a -Trigger $t " +
            "-RunLevel Highest -User 'SYSTEM' -Force");
        state.CreatedWatchdogTask = true;
        Persist();

        return state;
    }

    public void Revert(RevertState state)
    {
        // Обратный порядок; каждый шаг под флагом → повторный вызов безопасен.
        if (state.CreatedWatchdogTask)
            _ps.Run($"Unregister-ScheduledTask -TaskName '{state.WatchdogTaskName}' -Confirm:$false " +
                    "-ErrorAction SilentlyContinue", throwOnError: false);

        if (state.WroteAuthorizedKey && File.Exists(AdminAuthKeys))
        {
            if (state.CreatedAuthorizedKeysFile)
                File.Delete(AdminAuthKeys);
            else
            {
                var kept = File.ReadAllLines(AdminAuthKeys)
                    .Where(l => !l.Contains(state.AuthorizedKeyComment));
                File.WriteAllLines(AdminAuthKeys, kept);
            }
        }

        if (state.AddedFirewallRule)
            _ps.Run($"Remove-NetFirewallRule -Name '{state.FirewallRuleName}' -ErrorAction SilentlyContinue",
                    throwOnError: false);

        if (state.SetTokenPolicy)
        {
            if (state.TokenPolicyPreviousValue is null)
                _ps.Run($"Remove-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
                        "-ErrorAction SilentlyContinue", throwOnError: false);
            else
                _ps.Run($"Set-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
                        $"-Value {state.TokenPolicyPreviousValue}", throwOnError: false);
        }

        if (state.CreatedUser)
            _ps.Run($"net user {state.ServiceAccount} /delete", throwOnError: false);

        if (state.StartedSshService)
            _ps.Run("Stop-Service sshd -ErrorAction SilentlyContinue; " +
                    "Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue", throwOnError: false);

        if (state.InstalledOpenSsh)
            _ps.Run("Remove-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0", throwOnError: false);

        RevertStateStore.Delete(_statePath);
    }

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return "Aa1!" + Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-");
    }
}
```

- [ ] **Step 2: Сборка**

Run: `dotnet build src/SzDiag.Agent`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Agent/WindowsSystemAccessManager.cs
git commit -m "feat(agent): WindowsSystemAccessManager — открытие/откат доступа"
```

---

### Task 6: SignalRHubLink (реальная связь)

**Files:**
- Create: `src/SzDiag.Agent/SignalRHubLink.cs`

- [ ] **Step 1: Реализовать**

`src/SzDiag.Agent/SignalRHubLink.cs`:
```csharp
using Microsoft.AspNetCore.SignalR.Client;
using SzDiag.Contracts;

namespace SzDiag.Agent;

public sealed class SignalRHubLink : IHubLink
{
    private readonly HubConnection _conn;

    public SignalRHubLink(string hubUrl, string token)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{hubUrl.TrimEnd('/')}{HubRoutes.Path}", o =>
                o.Headers[HubRoutes.TokenHeader] = token)
            .WithAutomaticReconnect()
            .Build();
    }

    public Task ConnectAsync(CancellationToken ct = default) => _conn.StartAsync(ct);

    public Task RegisterAsync(string sz, string hostname, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.Register, new RegisterRequest(sz, hostname), ct);

    public Task HeartbeatAsync(string sz, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.Heartbeat, sz, ct);

    public void OnRevert(Func<string, Task> handler)
        => _conn.On<string>(HubRoutes.Revert, sz => handler(sz));

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
```

- [ ] **Step 2: Сборка**

Run: `dotnet build src/SzDiag.Agent`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Agent/SignalRHubLink.cs
git commit -m "feat(agent): SignalRHubLink — реальная связь с hub"
```

---

### Task 7: Program.cs — интерактив, wiring, перехват закрытия окна

**Files:**
- Create: `src/SzDiag.Agent/AgentOptions.cs`, `src/SzDiag.Agent/appsettings.json`
- Modify: `src/SzDiag.Agent/Program.cs` (заменить целиком)
- Modify: `src/SzDiag.Agent/SzDiag.Agent.csproj` (копировать appsettings, добавить конфиг-пакеты)

- [ ] **Step 1: Пакеты конфигурации**

Run:
```bash
dotnet add src/SzDiag.Agent package Microsoft.Extensions.Configuration.Json
dotnet add src/SzDiag.Agent package Microsoft.Extensions.Configuration.EnvironmentVariables
```

- [ ] **Step 2: AgentOptions и appsettings**

`src/SzDiag.Agent/AgentOptions.cs`:
```csharp
namespace SzDiag.Agent;

public sealed class AgentOptions
{
    public string HubUrl { get; set; } = "http://localhost:5000";
    public string AgentToken { get; set; } = "";
    public string ServiceAccount { get; set; } = "svc-diag";
    public string ServicePublicKeyPath { get; set; } = "service_key.pub";
    public int SshPort { get; set; } = 22;
    public double WatchdogHours { get; set; } = 6;
    public double HeartbeatSeconds { get; set; } = 20;
    public string StatePath { get; set; } = @"C:\ProgramData\szdiag\state.json";
}
```

`src/SzDiag.Agent/appsettings.json`:
```json
{
  "HubUrl": "http://SERVICE-HOST:5000",
  "AgentToken": "REPLACE_WITH_AGENT_TOKEN",
  "ServiceAccount": "svc-diag",
  "ServicePublicKeyPath": "service_key.pub",
  "SshPort": 22,
  "WatchdogHours": 6,
  "HeartbeatSeconds": 20,
  "StatePath": "C:\\ProgramData\\szdiag\\state.json"
}
```

В `src/SzDiag.Agent/SzDiag.Agent.csproj` добавить:
```xml
  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Заменить Program.cs**

`src/SzDiag.Agent/Program.cs`:
```csharp
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using SzDiag.Agent;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZAGENT_")
    .Build();
var opts = new AgentOptions();
config.Bind(opts);

var ps = new PowerShellRunner();

// Режим watchdog / автозакрытие: sz-agent --revert <statePath>
if (args.Length >= 2 && args[0] == "--revert")
{
    var st = RevertStateStore.Load(args[1]);
    if (st is not null) new WindowsSystemAccessManager(ps, args[1]).Revert(st);
    return 0;
}

Console.Write("Введите номер СЗ: ");
var sz = (Console.ReadLine() ?? "").Trim();
if (string.IsNullOrWhiteSpace(sz) || !sz.All(char.IsDigit))
{
    Console.WriteLine("Некорректный номер СЗ.");
    return 1;
}

var pubKey = File.ReadAllText(opts.ServicePublicKeyPath);
var spec = new AccessSpec(sz, opts.ServiceAccount, pubKey, opts.SshPort,
    TimeSpan.FromHours(opts.WatchdogHours));

var manager = new WindowsSystemAccessManager(ps, opts.StatePath);
var link = new SignalRHubLink(opts.HubUrl, opts.AgentToken);
var session = new AgentSession(manager, link, spec, Environment.MachineName);

Console.WriteLine($"Открываю доступ для СЗ {sz}…");
await session.StartAsync();
Console.WriteLine($"СЗ {sz}: доступ открыт ● online. Хост {Environment.MachineName}.");

// Перехват закрытия окна консоли (крестик) → откат.
using var closeGuard = new ConsoleCloseGuard(() => session.RevertAsync().GetAwaiter().GetResult());

// Heartbeat в фоне.
using var cts = new CancellationTokenSource();
var heartbeat = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        try { await session.HeartbeatOnceAsync(cts.Token); } catch { /* переподключение SignalR */ }
        await Task.Delay(TimeSpan.FromSeconds(opts.HeartbeatSeconds), cts.Token);
    }
});

Console.WriteLine("\n[C] Закрыть СЗ и откатить    [Q] Выход без отката (не рекомендуется)");
while (true)
{
    var key = Console.ReadKey(intercept: true).Key;
    if (key == ConsoleKey.C)
    {
        Console.WriteLine("\nЗакрываю СЗ и откатываю…");
        await session.RevertAsync();
        break;
    }
    if (key == ConsoleKey.Q) break;
}

cts.Cancel();
try { await heartbeat; } catch (OperationCanceledException) { }
Console.WriteLine("Готово.");
return 0;

/// <summary>Ловит CTRL_CLOSE_EVENT (крестик окна) и запускает откат.</summary>
sealed class ConsoleCloseGuard : IDisposable
{
    private delegate bool HandlerRoutine(int ctrlType);
    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    private readonly HandlerRoutine _handler;
    private readonly Action _onClose;

    public ConsoleCloseGuard(Action onClose)
    {
        _onClose = onClose;
        _handler = Handle;
        SetConsoleCtrlHandler(_handler, true);
    }

    private bool Handle(int ctrlType)
    {
        // 2 = CTRL_CLOSE_EVENT
        if (ctrlType == 2) _onClose();
        return true;
    }

    public void Dispose() => SetConsoleCtrlHandler(_handler, false);
}
```

- [ ] **Step 4: Сборка и юнит-тесты всего решения**

Run: `dotnet build`
Expected: Build succeeded.
Run: `dotnet test`
Expected: PASS — все юнит/интеграционные тесты (hub + cli + agent).

- [ ] **Step 5: VM интеграционный чек-лист (ручной, на одноразовой Windows-ВМ)**

Опубликовать агента:
```bash
dotnet publish src/SzDiag.Agent -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
Скопировать exe + `appsettings.json` + `service_key.pub` на ВМ, задать `HubUrl`/`AgentToken`. На хосте запущены hub (План 1) и CLI (План 2).

Проверить по шагам:
- [ ] Запуск exe → UAC → приглашение «Введите номер СЗ», ввод `156864`.
- [ ] На хосте `szcli list` показывает `156864 ● online` с IP ВМ.
- [ ] `szcli target 156864` → `ssh svc-diag@<IP>`; с чистого бокса `ssh` по service-ключу входит.
- [ ] На хосте создан каркас `kb/СЗ/156864/` (План 1).
- [ ] `szcli close 156864` → на ВМ агент откатился: нет `svc-diag`, нет firewall-правила `szdiag-ssh-156864`, нет `administrators_authorized_keys`-строки с меткой, снят watchdog-task.
- [ ] Повторный `close` идемпотентен; в `szcli list` СЗ пропала.
- [ ] Отдельный прогон: закрыть окно агента крестиком → доступ так же откатился.
- [ ] Отдельный прогон: не закрывать вручную, дождаться watchdog (уменьшить `WatchdogHours` до ~0.02 для теста) → авто-откат сработал.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(agent): интерактивный Program, wiring, перехват закрытия окна, VM чек-лист"
```

---

## Self-Review (выполнено при написании плана)

**Покрытие спеки (раздел sz-agent):**
- Интерактивный ввод СЗ без параметров → Task 7 (`Program.cs`). ✓
- Открытие доступа (OpenSSH, firewall, LocalAccountTokenFilterPolicy, svc-diag, authorized_keys, watchdog) → Task 5. ✓
- Резидентная связь с hub + heartbeat → Task 3 (`AgentSession`) + Task 6 (`SignalRHubLink`) + Task 7 (loop). ✓
- Идемпотентный revert по 4 триггерам (CLI/локально/крестик/watchdog) → Task 2 (`RevertCoordinator`), Task 3, Task 5 (Revert), Task 7 (`ConsoleCloseGuard`, `--revert`). ✓
- «Ноль следов» после отката → Task 5 (обратный порядок под флагами) + VM чек-лист Task 6. ✓
- Права администратора → Task 0 (манифест). ✓

**Плейсхолдеры:** отсутствуют; весь код приведён.

**Согласованность типов:** `AccessSpec`, `RevertState`, `ISystemAccessManager`, `IHubLink`, `AgentSession`, `RevertCoordinator` — единые сигнатуры между задачами и тестами. Протокол (`HubRoutes.*`, `RegisterRequest`) взят из Плана 1.

**Зависимость от Планов 1/2:** использует `HubRoutes`, `RegisterRequest`; регистрируется в реестре hub, закрывается через management-API/`SessionCloser`.
