# Портативный OpenSSH для агента — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Агент открывает SSH-доступ через свой портативный `sshd.exe` (дочерний процесс, свежие host-ключи каждую сессию), не завися от системного OpenSSH и Windows Update.

**Architecture:** Новый класс `PortableSshServer` управляет жизненным циклом дочернего `sshd.exe` из папки агента. `WindowsSystemAccessManager` перестаёт ставить/стартовать системную службу — вместо этого гасит системный sshd на сессию (если он занимал порт) и поднимает портативный. `build-dist.ps1` качает и вкладывает бинарники OpenSSH в `dist\client\ssh\`. Watchdog чистит все следы, включая возврат системного sshd.

**Tech Stack:** .NET 8 / C#, xUnit, PowerShell (build + системные вызовы агента), Win32-OpenSSH portable.

---

## File Structure

- **Create** `src/SzDiag.Agent/PortableSshServer.cs` — жизненный цикл дочернего `sshd.exe`: генерация конфига/ключей, старт, проверка, стоп.
- **Create** `tests/SzDiag.Agent.Tests/PortableSshServerTests.cs` — юниты на конфиг, разбор лога, идемпотентность стопа.
- **Modify** `src/SzDiag.Agent/RevertState.cs` — убрать `InstalledOpenSsh`/`StartedSshService`, добавить `StoppedSystemSshd`/`GeneratedHostKeys`.
- **Modify** `src/SzDiag.Agent/WindowsSystemAccessManager.cs` — переписать шаг OpenSSH в `Open`/`Revert`; удалить `OpenSshUnavailableException`.
- **Modify** `src/SzDiag.Agent/AgentOptions.cs` — добавить путь к папке портативного ssh.
- **Modify** `src/SzDiag.Agent/Program.cs` — убрать `catch (OpenSshUnavailableException)`; прокинуть путь ssh в `PortableSshServer`.
- **Modify** `tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs` — покрыть новые флаги в round-trip.
- **Modify** `tools/build-dist.ps1` — скачивание/кэш/копирование бинарников OpenSSH.
- **Modify** `docs/TESTING.md` — три e2e-сценария + token-privilege проверка.

**Замечание по TDD-границам:** реальный запуск `sshd.exe` и системные PowerShell-вызовы (`Stop-Service`) не юнит-тестируются — они Windows/окружение-зависимы и покрываются ручным e2e (как весь `WindowsSystemAccessManager` сейчас; для него dedicated-юнитов нет). Юнитами берём чистую логику: генерацию текста `sshd_config`, разбор лога sshd, идемпотентность `Stop`, сериализацию `RevertState`.

---

## Task 1: Новые поля RevertState

**Files:**
- Modify: `src/SzDiag.Agent/RevertState.cs`
- Modify: `tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs`

- [ ] **Step 1: Обновить тест round-trip под новые флаги**

В `tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs` заменить тело `SaveThenLoad_RoundTrips` на:

```csharp
[Fact]
public void SaveThenLoad_RoundTrips()
{
    var state = new RevertState
    {
        Sz = "156864",
        CreatedUser = true,
        SetTokenPolicy = true,
        TokenPolicyPreviousValue = null,
        FirewallRuleName = "szdiag-ssh",
        StoppedSystemSshd = true,
        GeneratedHostKeys = true
    };

    RevertStateStore.Save(_path, state);
    var loaded = RevertStateStore.Load(_path);

    Assert.NotNull(loaded);
    Assert.Equal("156864", loaded!.Sz);
    Assert.True(loaded.CreatedUser);
    Assert.True(loaded.SetTokenPolicy);
    Assert.Null(loaded.TokenPolicyPreviousValue);
    Assert.Equal("szdiag-ssh", loaded.FirewallRuleName);
    Assert.True(loaded.StoppedSystemSshd);
    Assert.True(loaded.GeneratedHostKeys);
}
```

- [ ] **Step 2: Запустить тест — должен НЕ компилироваться**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~RevertStateStoreTests`
Expected: ошибка компиляции — нет свойств `StoppedSystemSshd`/`GeneratedHostKeys`.

- [ ] **Step 3: Обновить RevertState**

В `src/SzDiag.Agent/RevertState.cs` удалить строки `public bool InstalledOpenSsh { get; set; }` и `public bool StartedSshService { get; set; }`, добавить два новых флага. Итоговый блок флагов:

```csharp
    public bool CreatedUser { get; set; }
    public bool StoppedSystemSshd { get; set; }
    public bool GeneratedHostKeys { get; set; }
    public bool AddedFirewallRule { get; set; }
    public bool WroteAuthorizedKey { get; set; }
    public bool CreatedAuthorizedKeysFile { get; set; }
    public bool SetTokenPolicy { get; set; }
```

- [ ] **Step 4: Запустить тест — должен пройти**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~RevertStateStoreTests`
Expected: PASS. (Остальной проект пока НЕ соберётся — `WindowsSystemAccessManager` ещё ссылается на старые флаги; это чиним в Task 3. Фильтр по классу изолирует эту проверку — если весь проект не компилируется, переходи к Task 3 и вернись сюда после.)

> Примечание: т.к. C# компилирует проект целиком, шаги 2/4 могут падать на ошибках из `WindowsSystemAccessManager`. Это ожидаемо — флаги и их потребитель меняются в связке. Коммить Task 1+3 можно вместе, если по отдельности проект не собирается.

- [ ] **Step 5: Commit (если проект собирается) либо отложить до Task 3**

```bash
git add src/SzDiag.Agent/RevertState.cs tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs
git commit -m "refactor(agent): RevertState — флаги StoppedSystemSshd/GeneratedHostKeys вместо Installed/StartedSsh"
```

---

## Task 2: PortableSshServer — генерация sshd_config

**Files:**
- Create: `src/SzDiag.Agent/PortableSshServer.cs`
- Create: `tests/SzDiag.Agent.Tests/PortableSshServerTests.cs`

Цель этой задачи — только чистая логика (текст конфига), без запуска процессов.

- [ ] **Step 1: Написать падающий тест на BuildConfig**

Создать `tests/SzDiag.Agent.Tests/PortableSshServerTests.cs`:

```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class PortableSshServerTests
{
    [Fact]
    public void BuildConfig_ContainsPortHostKeyAndAuthorizedKeys()
    {
        var cfg = PortableSshServer.BuildConfig(
            port: 2222,
            hostKeyPath: @"C:\ProgramData\szdiag\ssh\ssh_host_ed25519_key",
            authorizedKeysPath: @"C:\ProgramData\szdiag\ssh\authorized_keys");

        Assert.Contains("Port 2222", cfg);
        Assert.Contains(@"HostKey C:\ProgramData\szdiag\ssh\ssh_host_ed25519_key", cfg);
        // Для админ-аккаунтов Windows OpenSSH иначе форсит administrators_authorized_keys —
        // нам нужен Match-override на нашу папку.
        Assert.Contains("Match Group administrators", cfg);
        Assert.Contains(@"AuthorizedKeysFile C:\ProgramData\szdiag\ssh\authorized_keys", cfg);
    }

    [Fact]
    public void BuildConfig_EnablesVerboseLoggingForDiagnostics()
    {
        var cfg = PortableSshServer.BuildConfig(22, "k", "a");
        Assert.Contains("LogLevel VERBOSE", cfg);
    }
}
```

- [ ] **Step 2: Запустить — должен НЕ компилироваться**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PortableSshServerTests`
Expected: нет типа `PortableSshServer`.

- [ ] **Step 3: Создать PortableSshServer с BuildConfig**

Создать `src/SzDiag.Agent/PortableSshServer.cs`:

```csharp
using System.Diagnostics;

namespace SzDiag.Agent;

/// <summary>sshd не удалось поднять — процесс упал сразу после старта.</summary>
public sealed class SshdStartException : Exception
{
    public SshdStartException(string message) : base(message) { }
}

/// <summary>
/// Жизненный цикл портативного sshd.exe как дочернего процесса агента: свой конфиг,
/// свои host-ключи (свежие каждую сессию), свой AuthorizedKeysFile. Не зависит от
/// системной службы OpenSSH и Windows Update. Умирает вместе с агентом (fail-closed).
/// </summary>
public sealed class PortableSshServer
{
    /// <summary>Текст sshd_config под нашу папку. Match-override нужен, т.к. для членов
    /// Administrators Windows OpenSSH по умолчанию форсит administrators_authorized_keys
    /// и игнорирует per-user AuthorizedKeysFile.</summary>
    public static string BuildConfig(int port, string hostKeyPath, string authorizedKeysPath) =>
        $"""
        Port {port}
        HostKey {hostKeyPath}
        LogLevel VERBOSE
        PasswordAuthentication no
        PubkeyAuthentication yes
        Subsystem sftp sftp-server.exe
        Match Group administrators
            AuthorizedKeysFile {authorizedKeysPath}
        """;
}
```

- [ ] **Step 4: Запустить — должен пройти**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PortableSshServerTests`
Expected: PASS (2 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/PortableSshServer.cs tests/SzDiag.Agent.Tests/PortableSshServerTests.cs
git commit -m "feat(agent): PortableSshServer.BuildConfig — sshd_config под свою папку"
```

---

## Task 3: PortableSshServer — разбор лога неудачного старта

**Files:**
- Modify: `src/SzDiag.Agent/PortableSshServer.cs`
- Modify: `tests/SzDiag.Agent.Tests/PortableSshServerTests.cs`

- [ ] **Step 1: Тест на извлечение причины из лога sshd**

Добавить в `PortableSshServerTests.cs`:

```csharp
[Fact]
public void DescribeFailure_ReturnsLastMeaningfulLogLines()
{
    var log = string.Join('\n', new[]
    {
        "debug1: sshd version OpenSSH_for_Windows_9.5",
        "debug1: private host key: #0 type 3 ECDSA",
        "Unable to load host key: C:\\...\\ssh_host_ed25519_key",
        "sshd: no hostkeys available -- exiting."
    });

    var msg = PortableSshServer.DescribeFailure(log);

    Assert.Contains("no hostkeys available", msg);
    Assert.DoesNotContain("debug1: sshd version", msg); // шумные debug-строки отброшены
}

[Fact]
public void DescribeFailure_EmptyLog_ReturnsFallback()
{
    Assert.Contains("без вывода", PortableSshServer.DescribeFailure(""));
}
```

- [ ] **Step 2: Запустить — FAIL (нет DescribeFailure)**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PortableSshServerTests`
Expected: не компилируется — нет `DescribeFailure`.

- [ ] **Step 3: Реализовать DescribeFailure**

Добавить в `PortableSshServer` (в класс):

```csharp
    /// <summary>Достаёт из лога sshd осмысленные строки (fatal/error/exiting/Unable),
    /// отбрасывая debug-шум, — для внятного сообщения оператору вместо сырого дампа.</summary>
    public static string DescribeFailure(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return "sshd упал без вывода в лог.";

        var meaningful = log.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("debug", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tail = meaningful.Count > 0 ? meaningful : new List<string> { "sshd упал без вывода в лог." };
        return string.Join("; ", tail.TakeLast(3));
    }
```

- [ ] **Step 4: Запустить — PASS**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PortableSshServerTests`
Expected: PASS (4 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/PortableSshServer.cs tests/SzDiag.Agent.Tests/PortableSshServerTests.cs
git commit -m "feat(agent): PortableSshServer.DescribeFailure — внятная причина из лога sshd"
```

---

## Task 4: PortableSshServer — Start/Stop (реальный процесс)

**Files:**
- Modify: `src/SzDiag.Agent/PortableSshServer.cs`

Запуск процесса не юнит-тестируем (Windows/бинарники), но код пишем полностью и покрываем в e2e (Task 9).

- [ ] **Step 1: Добавить состояние, конструктор, Start, Stop**

Добавить в `PortableSshServer` поля/методы. `sshDir` — папка с `sshd.exe`/`ssh-keygen.exe`; `workDir` — куда пишем ключи/конфиг/лог (`ProgramData\szdiag\ssh`).

```csharp
    private readonly string _sshDir;
    private readonly string _workDir;
    private readonly PowerShellRunner _ps;
    private Process? _proc;

    public string HostKeyPath => Path.Combine(_workDir, "ssh_host_ed25519_key");
    public string ConfigPath => Path.Combine(_workDir, "sshd_config");
    public string LogPath => Path.Combine(_workDir, "sshd.log");
    public string AuthorizedKeysPath => Path.Combine(_workDir, "authorized_keys");
    public string WorkDir => _workDir;

    public PortableSshServer(string sshDir, string workDir, PowerShellRunner ps)
    {
        _sshDir = sshDir;
        _workDir = workDir;
        _ps = ps;
    }

    /// <summary>Свежие host-ключи + конфиг + запуск sshd.exe дочерним процессом.
    /// Кидает SshdStartException, если sshd умер в первые ~1.5с (с причиной из лога).</summary>
    public void Start(int port, string authorizedKeyLine)
    {
        Directory.CreateDirectory(_workDir);

        // Свежий host-ключ каждую сессию — битый ключ невозможен.
        if (File.Exists(HostKeyPath)) File.Delete(HostKeyPath);
        if (File.Exists(HostKeyPath + ".pub")) File.Delete(HostKeyPath + ".pub");
        _ps.Run($"& '{Path.Combine(_sshDir, "ssh-keygen.exe")}' -t ed25519 -f '{HostKeyPath}' -N '\"\"' -q");

        File.WriteAllText(AuthorizedKeysPath, authorizedKeyLine.Trim() + Environment.NewLine);
        File.WriteAllText(ConfigPath, BuildConfig(port, HostKeyPath, AuthorizedKeysPath));

        // ACL на ключ и authorized_keys: только SYSTEM+Administrators, иначе sshd
        // отказывается их использовать ("bad permissions").
        foreach (var f in new[] { HostKeyPath, AuthorizedKeysPath })
            _ps.Run($"icacls '{f}' /inheritance:r /grant 'SYSTEM:F' /grant 'BUILTIN\\Administrators:F'",
                throwOnError: false);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_sshDir, "sshd.exe"),
            Arguments = $"-f \"{ConfigPath}\" -D -E \"{LogPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (File.Exists(LogPath)) File.Delete(LogPath);
        _proc = Process.Start(psi) ?? throw new SshdStartException("не удалось запустить sshd.exe");

        // Дать sshd мгновение подняться; если он сразу умер — вытащить причину из лога.
        if (_proc.WaitForExit(1500))
        {
            var log = File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";
            throw new SshdStartException($"sshd не стартовал: {DescribeFailure(log)}");
        }
    }

    /// <summary>Убить sshd (идемпотентно). Fail-closed при откате/краше агента.</summary>
    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); }
        catch { /* уже мог сам завершиться — гонка, не критично */ }
        _proc = null;
    }
```

- [ ] **Step 2: Проверить сборку и весь тест-набор проекта**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PortableSshServerTests`
Expected: PASS (4 теста; новые методы компилируются, юниты Start/Stop не трогают).

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Agent/PortableSshServer.cs
git commit -m "feat(agent): PortableSshServer.Start/Stop — sshd дочерним процессом со свежими ключами"
```

---

## Task 5: AgentOptions — путь к портативному ssh

**Files:**
- Modify: `src/SzDiag.Agent/AgentOptions.cs`

- [ ] **Step 1: Добавить SshBinDir**

В `src/SzDiag.Agent/AgentOptions.cs` после `TestSuitePath` добавить:

```csharp
    /// <summary>Папка с портативным sshd.exe/ssh-keygen.exe (рядом с exe: dist\client\ssh).</summary>
    public string SshBinDir { get; set; } = "ssh";
    /// <summary>Рабочая папка sshd на клиенте: host-ключи, конфиг, лог, authorized_keys.</summary>
    public string SshWorkDir { get; set; } = @"C:\ProgramData\szdiag\ssh";
```

- [ ] **Step 2: Проверить сборку**

Run: `dotnet build src/SzDiag.Agent`
Expected: SUCCESS.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Agent/AgentOptions.cs
git commit -m "feat(agent): опции SshBinDir/SshWorkDir для портативного sshd"
```

---

## Task 6: WindowsSystemAccessManager — переписать OpenSSH-шаги

**Files:**
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`

Это ядро изменения. `PortableSshServer` внедряется в конструктор; шаги установки/старта системной службы заменяются на гашение системного sshd + `PortableSshServer.Start`; `authorized_keys` теперь пишется в рабочую папку sshd (через `PortableSshServer`), а не в `administrators_authorized_keys`.

- [ ] **Step 1: Обновить конструктор и убрать константы Windows Update**

В `WindowsSystemAccessManager.cs`:

Удалить: класс `OpenSshUnavailableException` (весь), поле `OpenSshInstallTimeout`, поле `AdminAuthKeys`.

Изменить конструктор и поля:

```csharp
    private const string AdminsSid = "S-1-5-32-544";
    private const string TokenPolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private readonly PowerShellRunner _ps;
    private readonly PortableSshServer _sshd;
    private readonly string _statePath;

    public WindowsSystemAccessManager(PowerShellRunner ps, PortableSshServer sshd, string statePath)
    {
        _ps = ps;
        _sshd = sshd;
        _statePath = statePath;
    }
```

- [ ] **Step 2: Переписать шаги 1–2 и 6 в Open**

Заменить блок шагов 1–2 (весь код от `// 1. OpenSSH Server` до конца шага 2, т.е. до `// 3. Firewall`) на:

```csharp
        // 1. Если системный sshd запущен — он держит порт 22. Гасим на время сессии,
        // вернём при откате. Мы поднимаем СВОЙ sshd и не зависим от состояния системного.
        var systemSshdStatus = _ps.Run("(Get-Service sshd -ErrorAction SilentlyContinue).Status",
            throwOnError: false).StdOut.Trim();
        if (systemSshdStatus.Contains("Running"))
        {
            _ps.Run("Stop-Service sshd -Force -ErrorAction SilentlyContinue", throwOnError: false);
            state.StoppedSystemSshd = true;
            Persist();
        }
```

Заменить блок шага 6 (`// 6. administrators_authorized_keys + ACL` целиком, включая if/else с файлом и `state.WroteAuthorizedKey = true; Persist();`) на:

```csharp
        // 6. Поднять портативный sshd со свежими ключами и нашим authorized_keys.
        // Ключ пишется в рабочую папку sshd (PortableSshServer), не в системный
        // administrators_authorized_keys — мы полностью владеем своим sshd.
        var keyLine = $"{spec.ServicePublicKey.Trim()} {state.AuthorizedKeyComment}";
        _sshd.Start(spec.SshPort, keyLine);
        state.GeneratedHostKeys = true;
        state.WroteAuthorizedKey = true;
        Persist();
```

> Порядок шагов: гашение системного sshd (шаг 1) — в самом начале; `_sshd.Start` (шаг 6) — после firewall/token-policy/user, т.к. sshd должен видеть уже созданную учётку `svc-diag` и открытый порт. Firewall (шаг 3), token policy (шаг 4), user (шаг 5) остаются без изменений.

- [ ] **Step 3: Переписать Revert**

Заменить тело `Revert` целиком на (обратный порядок, каждый шаг под флагом):

```csharp
    public void Revert(RevertState state)
    {
        // Обратный порядок; каждый шаг под флагом → повторный вызов безопасен.
        if (state.CreatedWatchdogTask)
            _ps.Run($"Unregister-ScheduledTask -TaskName '{state.WatchdogTaskName}' -Confirm:$false " +
                    "-ErrorAction SilentlyContinue", throwOnError: false);

        // Наш sshd — дочерний, при живом агенте убьётся тут; при watchdog-ревёрте
        // (агент уже мёртв) процесса нет, но ключи/конфиг на диске надо снять.
        _sshd.Stop();
        if (state.GeneratedHostKeys && Directory.Exists(_sshd.WorkDir))
        {
            try { Directory.Delete(_sshd.WorkDir, recursive: true); } catch { /* залочен — не критично */ }
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
            _ps.Run($"Remove-LocalUser -Name '{state.ServiceAccount}' -ErrorAction SilentlyContinue", throwOnError: false);

        // Вернуть системный sshd, если гасили его на время сессии.
        if (state.StoppedSystemSshd)
            _ps.Run("Start-Service sshd -ErrorAction SilentlyContinue", throwOnError: false);

        RevertStateStore.Delete(_statePath);
    }
```

> Поле `AuthorizedKeyComment` в `RevertState` остаётся (используется в `keyLine`). Флаги `WroteAuthorizedKey`/`CreatedAuthorizedKeysFile` в `Revert` больше не разветвляют логику удаления системного файла (файла больше нет), но `WroteAuthorizedKey` оставляем как признак «дошли до старта sshd»; `CreatedAuthorizedKeysFile` в `RevertState` можно оставить неиспользуемым — удалять не обязательно (YAGNI: не трогаем сериализацию лишний раз).

- [ ] **Step 4: Проверить сборку агента**

Run: `dotnet build src/SzDiag.Agent`
Expected: ошибки в `Program.cs` (конструктор `WindowsSystemAccessManager` теперь требует `PortableSshServer`, и `catch (OpenSshUnavailableException)` ссылается на удалённый тип). Чиним в Task 7 — это ожидаемо.

- [ ] **Step 5: (коммит вместе с Task 7, т.к. проект не собирается в одиночку)**

---

## Task 7: Program.cs — собрать PortableSshServer и убрать OpenSshUnavailableException

**Files:**
- Modify: `src/SzDiag.Agent/Program.cs`

- [ ] **Step 1: Создать PortableSshServer и передать в manager**

В `src/SzDiag.Agent/Program.cs` найти строку `var manager = new WindowsSystemAccessManager(ps, opts.StatePath);` и заменить на:

```csharp
var sshBinDir = ResolvePath(opts.SshBinDir);
var sshd = new PortableSshServer(sshBinDir, opts.SshWorkDir, ps);
var manager = new WindowsSystemAccessManager(ps, sshd, opts.StatePath);
```

> `ResolvePath` уже определён выше в `Program.cs` (строка ~64) и резолвит относительный путь от `AppContext.BaseDirectory`. `opts.SshWorkDir` абсолютный — не резолвим.

- [ ] **Step 2: Убрать catch OpenSshUnavailableException**

Найти блок:

```csharp
try
{
    await session.StartAsync();
}
catch (OpenSshUnavailableException ex)
{
    Announce(ex.Message, $"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}
```

Заменить на обработку новой ошибки старта sshd:

```csharp
try
{
    await session.StartAsync();
}
catch (SshdStartException ex)
{
    Announce($"Не удалось поднять SSH: {ex.Message}",
        $"[red]Не удалось поднять SSH:[/] {Markup.Escape(ex.Message)}");
    return 1;
}
```

- [ ] **Step 3: Также обновить watchdog-ветку (--revert) — ей тоже нужен sshd**

Найти в `Program.cs` блок `--revert`:

```csharp
if (args.Length >= 2 && args[0] == "--revert")
{
    var st = RevertStateStore.Load(args[1]);
    if (st is not null) new WindowsSystemAccessManager(ps, args[1]).Revert(st);
    return 0;
}
```

Заменить на:

```csharp
if (args.Length >= 2 && args[0] == "--revert")
{
    var st = RevertStateStore.Load(args[1]);
    if (st is not null)
    {
        var revertOpts = new AgentOptions();
        config.Bind(revertOpts);
        var revertSshd = new PortableSshServer(
            Path.IsPathRooted(revertOpts.SshBinDir)
                ? revertOpts.SshBinDir
                : Path.Combine(AppContext.BaseDirectory, revertOpts.SshBinDir),
            revertOpts.SshWorkDir, ps);
        new WindowsSystemAccessManager(ps, revertSshd, args[1]).Revert(st);
    }
    return 0;
}
```

> Важно: `--revert` обрабатывается ДО основного `try` и до создания `opts` в текущем коде. Проверь, что `config` уже создан выше этой ветки (в текущем `Program.cs` `config` создаётся на строке ~15, до `--revert` на строке ~43 — значит `config.Bind` здесь валиден).

- [ ] **Step 4: Собрать и прогнать весь тест-набор**

Run: `dotnet build SzDiag.sln && dotnet test tests/SzDiag.Agent.Tests`
Expected: BUILD SUCCESS, все тесты агента PASS.

- [ ] **Step 5: Прогнать полный набор решения**

Run: `dotnet test`
Expected: все ~113 тестов PASS (ни один не завязан на удалённые флаги, кроме обновлённого RevertStateStoreTests).

- [ ] **Step 6: Commit (Task 1, 6, 7 вместе)**

```bash
git add src/SzDiag.Agent/RevertState.cs src/SzDiag.Agent/WindowsSystemAccessManager.cs src/SzDiag.Agent/Program.cs tests/SzDiag.Agent.Tests/RevertStateStoreTests.cs
git commit -m "feat(agent): открытие доступа через портативный sshd вместо системной службы — уходим от Windows Update и битых host-ключей"
```

---

## Task 8: build-dist.ps1 — скачать и вложить портативный OpenSSH

**Files:**
- Modify: `tools/build-dist.ps1`

- [ ] **Step 1: Добавить функцию скачивания OpenSSH перед секцией публикации**

В `tools/build-dist.ps1` после проверки `ssh-keygen` (строка ~33), перед `# 1. SSH-ключ сервиса`, добавить:

```powershell
# 0. Портативный Win32-OpenSSH для клиента (sshd.exe и ко). Качаем один раз с GitHub,
# кэшируем распакованным в client-tools\ssh — в git не коммитим (как OCCT/TM5).
$sshCache = "client-tools\ssh"
if (-not (Test-Path "$sshCache\sshd.exe")) {
    Write-Host "-- качаю портативный OpenSSH (один раз, ~10 МБ)"
    $rel = "https://github.com/PowerShell/Win32-OpenSSH/releases/download/v9.5.0.0p1-Beta/OpenSSH-Win64.zip"
    $zip = "$env:TEMP\OpenSSH-Win64.zip"
    New-Item -ItemType Directory $sshCache -Force | Out-Null
    try {
        Invoke-WebRequest -Uri $rel -OutFile $zip -UseBasicParsing
        Expand-Archive $zip "$env:TEMP\OpenSSH-Win64" -Force
        Copy-Item "$env:TEMP\OpenSSH-Win64\OpenSSH-Win64\*" $sshCache -Recurse -Force
        Remove-Item $zip -Force -ErrorAction SilentlyContinue
    } catch {
        throw "Не удалось скачать портативный OpenSSH ($rel): $($_.Exception.Message). " +
              "Проверь интернет на хосте или положи распакованные бинарники в $sshCache вручную."
    }
} else {
    Write-Host "-- портативный OpenSSH уже в кэше ($sshCache)"
}
```

- [ ] **Step 2: Копировать ssh в dist\client после публикации агента**

Найти блок копирования `client-tools -> dist\client\tools` (строка ~107). Сразу ПОСЛЕ него (после закрывающей `}` этого if/else) добавить:

```powershell
# Портативный sshd рядом с агентом: dist\client\ssh
if (Test-Path dist\client\SzDiag.Agent.exe) {
    Write-Host "-- копирую OpenSSH -> dist\client\ssh"
    New-Item -ItemType Directory dist\client\ssh -Force | Out-Null
    Copy-Item "$sshCache\sshd.exe","$sshCache\ssh-keygen.exe","$sshCache\sftp-server.exe" dist\client\ssh\ -Force
    # dll-зависимости (libcrypto и пр.) лежат рядом с exe в релизе — берём все dll.
    Copy-Item "$sshCache\*.dll" dist\client\ssh\ -Force -ErrorAction SilentlyContinue
}
```

- [ ] **Step 3: Прогнать сборку dist**

Run: `powershell -NoProfile -ExecutionPolicy Bypass -File tools/build-dist.ps1`
Expected: скачивание OpenSSH (или «уже в кэше»), затем `== Готово ==`; в `dist\client\ssh\` лежат `sshd.exe`, `ssh-keygen.exe`, dll.

- [ ] **Step 4: Проверить содержимое**

Run: `ls dist/client/ssh/`
Expected: `sshd.exe`, `ssh-keygen.exe`, `sftp-server.exe`, набор `.dll`.

- [ ] **Step 5: Commit**

```bash
git add tools/build-dist.ps1
git commit -m "build: качаю и вкладываю портативный OpenSSH в dist\client\ssh"
```

---

## Task 9: Ручной e2e и документация

**Files:**
- Modify: `docs/TESTING.md`

- [ ] **Step 1: Дополнить траблшутинг-таблицу и добавить e2e-раздел**

В `docs/TESTING.md` в таблицу «Траблшутинг» добавить строки:

```markdown
| Агент упал на `Start-Service sshd` / битые host-ключи | Больше не воспроизводится: агент носит свой портативный sshd (`dist\client\ssh`) и генерит свежие host-ключи каждую сессию. Системная служба sshd не используется. |
| `sshd не стартовал: <причина>` при открытии | Портативный sshd упал сразу. Причина — последние строки его лога (`C:\ProgramData\szdiag\ssh\sshd.log`). Часто: sshd под админ-токеном не смог создать logon-token (см. ниже про SYSTEM). |
```

Добавить новый раздел в конец файла:

```markdown
## E2e портативного sshd (три сценария)

Проверять на реальной клиентской машине после `build-dist.ps1`:

1. **Чистая машина** (системного sshd нет вообще) — агент должен открыть доступ без
   Windows Update. Раньше тут висло на «Открываю доступ».
2. **Рабочий системный sshd** — агент гасит его на сессию (`StoppedSystemSshd`),
   поднимает свой, при закрытии СЗ системный возвращается (`Get-Service sshd` → Running).
3. **Битый системный sshd** — наличие битых системных host-ключей больше не влияет:
   агент их не трогает, использует свои.

**Проверка token-privilege (ключевой риск).** sshd обычно работает под LocalSystem
(нужен SeTcbPrivilege для создания logon-token). Наш sshd — дочерний процесс
админ-агента. Проверь: после «Открываю доступ» подключись с хоста
`ssh -i secrets\svc_diag_key -o StrictHostKeyChecking=no svc-diag@<IP> "whoami"`.
- Если `whoami` вернул `<машина>\svc-diag` — token-privilege ОК, план А работает.
- Если в `sshd.log` видно `unable to create logon token` / вход отваливается —
  сработал риск из спеки. Фолбэк (план Б): запускать sshd не напрямую, а транзиентной
  scheduled task под SYSTEM (привязать к сессии, чистить тем же watchdog). Это
  отдельная доработка `PortableSshServer.Start`.
```

- [ ] **Step 2: Commit**

```bash
git add docs/TESTING.md
git commit -m "docs(testing): e2e-сценарии портативного sshd + проверка token-privilege"
```

- [ ] **Step 3: Ручной прогон на реальной машине**

Собрать dist, отнести `dist\client` на тестовую машину, прогнать сценарий 1 (чистая
машина — самый частый кейс). Убедиться: доступ открылся без зависания, `ssh whoami`
работает, при закрытии СЗ следов не осталось (`net user svc-diag` → нет,
`C:\ProgramData\szdiag\ssh` удалена, `Get-NetFirewallRule szdiag-ssh-*` пусто).

Это ручной шаг — не автоматизируется. Если token-privilege риск подтвердится —
завести отдельную задачу на план Б (SYSTEM-scheduled-task) и НЕ мерджить как «готово»
до её решения.

---

## Self-Review (выполнено при написании плана)

- **Покрытие спеки:** портативные бинарники (Task 8) ✓; `PortableSshServer`
  (Tasks 2–4) ✓; упрощение `WindowsSystemAccessManager` (Task 6) ✓; гашение/возврат
  системного sshd (Tasks 6) ✓; свежие host-ключи (Task 4) ✓; новые флаги RevertState
  (Task 1) ✓; watchdog-возврат системного sshd и удаление ключей (Task 6 Revert +
  Task 7 --revert) ✓; диагностика/внятные ошибки (Tasks 3, 7) ✓; тесты (Tasks 1–4) ✓;
  e2e + token-privilege риск (Task 9) ✓.
- **Плейсхолдеры:** нет — весь код приведён целиком.
- **Согласованность типов:** `PortableSshServer(sshDir, workDir, ps)`,
  `.Start(port, authorizedKeyLine)`, `.Stop()`, `.WorkDir`, `.BuildConfig(...)`,
  `.DescribeFailure(...)`, `SshdStartException` — имена совпадают между Task 2/3/4/6/7.
  `WindowsSystemAccessManager(ps, sshd, statePath)` — совпадает в Task 6 и Task 7.
  Флаги `StoppedSystemSshd`/`GeneratedHostKeys` — Task 1 определяет, Task 6 использует.
- **Порядок сборки:** Tasks 1/6/7 меняют код в связке (C# компилирует проект целиком),
  поэтому коммитятся вместе в Task 7 Step 6 — явно оговорено.
```

**Известное отклонение от чистого TDD:** реальный запуск sshd и системные вызовы не
юнит-тестируются (окружение-зависимы), покрываются e2e — как и весь существующий
`WindowsSystemAccessManager`, для которого dedicated-юнитов нет. Юнитами взято всё,
что можно проверить чисто: `BuildConfig`, `DescribeFailure`, сериализация флагов.
