# Таймаут на установку OpenSSH — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Агент не виснет навечно на «Открываю доступ…», если `Add-WindowsCapability -Online` (установка OpenSSH) не может достучаться до Windows Update — вместо этого падает за 2 минуты с понятным сообщением.

**Architecture:** `PowerShellRunner.Run` получает опциональный таймаут: если процесс не завершился вовремя — убивается, кидается `PowerShellTimeoutException`. `WindowsSystemAccessManager.Open` оборачивает этим таймаутом только шаг установки OpenSSH и переводит таймаут в понятный `OpenSshUnavailableException`. `Program.cs` ловит этот тип (по тому же паттерну, что уже есть для `HubNotFoundException`) и печатает чистое сообщение вместо общей панели «ФАТАЛ» с полным стектрейсом.

**Tech Stack:** .NET 8, `System.Diagnostics.Process`, xUnit (первые честные тесты `PowerShellRunner` — реальный `powershell.exe`, без моков).

**Проверки после каждой задачи:** `dotnet build` зелёный, `dotnet test` зелёный. Комментарии/вывод — на русском.

---

### Task 1: Таймаут в `PowerShellRunner`

**Files:**
- Modify: `src/SzDiag.Agent/PowerShellRunner.cs`
- Test: `tests/SzDiag.Agent.Tests/PowerShellRunnerTests.cs` (новый файл)

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/SzDiag.Agent.Tests/PowerShellRunnerTests.cs`:

```csharp
using System.Diagnostics;
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class PowerShellRunnerTests
{
    [Fact]
    public void Run_ExceedsTimeout_KillsProcessAndThrowsQuickly()
    {
        var runner = new PowerShellRunner();
        var sw = Stopwatch.StartNew();

        Assert.Throws<PowerShellTimeoutException>(() =>
            runner.Run("Start-Sleep -Seconds 5", timeout: TimeSpan.FromMilliseconds(500)));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"должен убить процесс быстро, а не ждать все 5с (прошло {sw.Elapsed})");
    }

    [Fact]
    public void Run_WithinTimeout_ReturnsNormally()
    {
        var runner = new PowerShellRunner();

        var result = runner.Run("Write-Output 'ok'", timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.StdOut);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что не компилируется**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PowerShellRunnerTests --nologo -v q`
Expected: ошибка компиляции — у `Run` нет параметра `timeout`, нет типа `PowerShellTimeoutException`.

- [ ] **Step 3: Реализовать таймаут**

Заменить `src/SzDiag.Agent/PowerShellRunner.cs` целиком на:

```csharp
using System.Diagnostics;

namespace SzDiag.Agent;

public sealed record PsResult(int ExitCode, string StdOut, string StdErr);

/// <summary>PowerShell-команда не уложилась в отведённый таймаут — процесс убит.</summary>
public sealed class PowerShellTimeoutException : Exception
{
    public PowerShellTimeoutException(string message) : base(message) { }
}

/// <summary>Запуск PowerShell-команд. Кидает при ненулевом коде, если throwOnError.</summary>
public sealed class PowerShellRunner
{
    public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
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

        // Асинхронное чтение запущено ДО WaitForExit: синхронный ReadToEnd() блокируется
        // до EOF, которое наступает только при завершении процесса — с ним таймаут
        // никогда бы не сработал (мы бы зависли на самом чтении, а не дошли до ожидания).
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        var exited = timeout is null
            ? p.WaitForExit(Timeout.Infinite)
            : p.WaitForExit((int)timeout.Value.TotalMilliseconds);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* уже мог сам завершиться — гонка */ }
            throw new PowerShellTimeoutException($"PowerShell не уложился в таймаут {timeout}: {script}");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        if (throwOnError && p.ExitCode != 0)
            throw new InvalidOperationException($"PowerShell завершился с кодом {p.ExitCode}: {stderr}");

        return new PsResult(p.ExitCode, stdout, stderr);
    }
}
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~PowerShellRunnerTests --nologo -v q`
Expected: PASS (2 теста; первый должен уложиться заметно меньше 3 секунд несмотря на `Start-Sleep -Seconds 5` в скрипте).

- [ ] **Step 5: Прогнать весь солюшен (эти тесты трогают реальный процесс — проверить, что ничего не задушили)**

Run: `dotnet test --nologo -v q`
Expected: PASS, все существующие тесты по-прежнему проходят (сигнатура `Run` расширена опциональным параметром — старые вызовы не ломаются).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Agent/PowerShellRunner.cs tests/SzDiag.Agent.Tests/PowerShellRunnerTests.cs
git commit -m "feat(agent): таймаут в PowerShellRunner.Run (PowerShellTimeoutException)"
```

---

### Task 2: Таймаут на шаг установки OpenSSH в `WindowsSystemAccessManager`

**Files:**
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`

> Класс не покрыт юнит-тестами (требует реальных Windows-привилегий/системных
> изменений — так было и до этой задачи). Критерий — зелёная сборка; поведение
> проверяется в поле (см. Task 5).

- [ ] **Step 1: Добавить тип исключения и константу таймаута**

В `src/SzDiag.Agent/WindowsSystemAccessManager.cs` добавить перед объявлением класса
`WindowsSystemAccessManager` (после `using System.Security.Cryptography;` и
`namespace SzDiag.Agent;`):

```csharp
/// <summary>OpenSSH не удалось поставить (нет доступа к Windows Update) — истёк таймаут.</summary>
public sealed class OpenSshUnavailableException : Exception
{
    public OpenSshUnavailableException(string message) : base(message) { }
}
```

- [ ] **Step 2: Добавить константу таймаута в класс**

Добавить после строки `private const string TokenPolicyPath = ...;`:

```csharp
    private static readonly TimeSpan OpenSshInstallTimeout = TimeSpan.FromMinutes(2);
```

- [ ] **Step 3: Обернуть шаг установки OpenSSH таймаутом**

Заменить блок:

```csharp
        if (!sshdExists)
        {
            _ps.Run("Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0");
            state.InstalledOpenSsh = true;
            Persist();
        }
```

на:

```csharp
        if (!sshdExists)
        {
            try
            {
                _ps.Run("Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0",
                    timeout: OpenSshInstallTimeout);
            }
            catch (PowerShellTimeoutException)
            {
                throw new OpenSshUnavailableException(
                    "OpenSSH не ставится — нет доступа к Windows Update (истёк таймаут " +
                    $"{OpenSshInstallTimeout.TotalMinutes:0} мин). Проверьте интернет на клиенте и запустите агента заново.");
            }
            state.InstalledOpenSsh = true;
            Persist();
        }
```

- [ ] **Step 4: Собрать — без ошибок**

Run: `dotnet build`
Expected: 0 ошибок.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Agent/WindowsSystemAccessManager.cs
git commit -m "feat(agent): таймаут на установку OpenSSH -> OpenSshUnavailableException"
```

---

### Task 3: Чистое сообщение в `Program.cs` вместо панели «ФАТАЛ»

**Files:**
- Modify: `src/SzDiag.Agent/Program.cs`

- [ ] **Step 1: Обернуть `session.StartAsync()` в try/catch**

Заменить:

```csharp
Announce($"Открываю доступ для СЗ {sz}…", $"[grey]Открываю доступ для СЗ {sz}…[/]");
await session.StartAsync();
Announce($"СЗ {sz}: доступ открыт ● online. Хост {Environment.MachineName}.",
    $"СЗ {sz}: доступ открыт [green]● online[/]. Хост {Environment.MachineName}.");
```

на:

```csharp
Announce($"Открываю доступ для СЗ {sz}…", $"[grey]Открываю доступ для СЗ {sz}…[/]");
try
{
    await session.StartAsync();
}
catch (OpenSshUnavailableException ex)
{
    Announce(ex.Message, $"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}
Announce($"СЗ {sz}: доступ открыт ● online. Хост {Environment.MachineName}.",
    $"СЗ {sz}: доступ открыт [green]● online[/]. Хост {Environment.MachineName}.");
```

(Тот же паттерн, что уже используется чуть ниже по файлу для `HubNotFoundException` —
чистое сообщение + `return 1`, вместо попадания в общий `catch (Exception ex)` с полным
`ex.ToString()` в панели «ФАТАЛ».)

- [ ] **Step 2: Собрать и прогнать весь солюшен**

Run: `dotnet build && dotnet test`
Expected: 0 ошибок сборки, все тесты PASS.

- [ ] **Step 3: Коммит**

```bash
git add src/SzDiag.Agent/Program.cs
git commit -m "feat(agent): чистое сообщение при OpenSshUnavailableException вместо панели ФАТАЛ"
```

---

### Task 4: Документация

**Files:**
- Modify: `docs/TESTING.md`

- [ ] **Step 1: Обновить строку траблшутинга про OpenSSH/WU**

В `docs/TESTING.md` в разделе «## Траблшутинг» заменить строку:

```markdown
| Агент требует OpenSSH, а его нет и WU недоступен | Поставь один раз вручную: `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` (от админа), затем запусти агента. |
```

на:

```markdown
| Агент требует OpenSSH, а его нет и WU недоступен | Раньше висел на «Открываю доступ…» навечно; теперь падает за 2 мин с сообщением «OpenSSH не ставится — нет доступа к Windows Update». Поставь один раз вручную: `Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0` (от админа, при наличии интернета — либо в момент, когда сеть появится), затем запусти агента снова. |
```

- [ ] **Step 2: Коммит**

```bash
git add docs/TESTING.md
git commit -m "docs: таймаут установки OpenSSH в TESTING.md"
```

---

### Task 5: Финальная проверка

- [ ] **Step 1: Полная сборка и тесты**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 ошибок, все тесты PASS (было 111 после прошлой фичи; ожидается 111 + 2 новых
= 113: `PowerShellRunnerTests`).

- [ ] **Step 2: Пересобрать dist и проверить в поле (вручную, по желанию)**

`.\tools\build-dist.ps1`, скопировать `dist\client\` на машину без доступа к WU и без
установленного `sshd` (или временно отключить сеть на такой машине перед первым
запуском). Ожидается: вместо бесконечного «Открываю доступ для СЗ…» — через ~2 минуты
сообщение `OpenSSH не ставится — нет доступа к Windows Update (истёк таймаут 2 мин).
Проверьте интернет на клиенте и запустите агента заново.` и агент завершается (не висит).

## Обратная совместимость (для ревью)

- `PowerShellRunner.Run(script, throwOnError, timeout)` — `timeout` опциональный,
  `null` по умолчанию = поведение как раньше (без таймаута). Все существующие вызовы
  `_ps.Run(...)` без таймаута не меняют поведения.
- Остальные шаги `Open`/`Revert` таймаутом не оборачиваются — не ходят в интернет и не
  имели истории зависаний.
- Офлайн-бандл OpenSSH (`.cab` в dist) сознательно не делается — привязан к конкретной
  сборке Windows, а полностью офлайн-сценарий у оператора и так тупиковый для других
  задач (нет доступа к `C:\Share`).
