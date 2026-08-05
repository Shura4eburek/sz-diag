# Бэкап базы знаний внутри hub — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Перенести оффсайт-бэкап vault'а из PowerShell-скрипта с задачей планировщика в фоновый сервис hub.

**Architecture:** Логика git живёт в `SzDiag.Kb.KbGitBackup` (дёргает `git.exe` через `Process`, ничего не знает про хостинг, возвращает типизированный результат). Hub поднимает `KbBackupService` — `BackgroundService` по образцу существующего `OfflineSweeper`: прогон на старте, дальше по `PeriodicTimer`, финальный прогон при остановке. Скрипт `tools/kb-backup.ps1` теряет установку расписания и остаётся ручной кнопкой.

**Tech Stack:** .NET 8, xUnit, `Microsoft.Extensions.Hosting.BackgroundService`, внешний `git.exe`.

**Спека:** [docs/superpowers/specs/2026-08-05-kb-backup-in-hub-design.md](../specs/2026-08-05-kb-backup-in-hub-design.md)

## Global Constraints

- Целевой фреймворк — **net8.0**. Все новые файлы — **UTF-8 с BOM** (PowerShell 5.1 иначе ломает кириллицу).
- Комментарии и текст логов — **на русском** (как в существующем коде).
- Сообщение коммита пишется во временный файл **строго UTF-8 без BOM** (`new UTF8Encoding(false)`) — BOM утекает в первую строку заголовка коммита.
- Никакого `2>&1`-стиля слияния потоков через shell: stdout и stderr читаются раздельно, асинхронно.
- Исключения не выпускаются из `BackgroundService` наружу — unhandled валит весь хост.
- Тесты требуют `git` в `PATH` и работают на временном репозитории с локальным bare-remote — сеть не нужна.
- Пути резолвятся от переданного корня vault, не от CWD.

---

## File Structure

| Файл | Ответственность |
|---|---|
| `src/SzDiag.Kb/KbBackupResult.cs` (создать) | `KbBackupOutcome` + `KbBackupResult` + интерфейс `IKbBackup` |
| `src/SzDiag.Kb/KbGitBackup.cs` (создать) | Реализация: add → status → commit → push, запуск `git.exe` с таймаутом |
| `src/SzDiag.Hub/HubOptions.cs` (изменить) | Секция `KbBackup` |
| `src/SzDiag.Hub/KbBackupService.cs` (создать) | `BackgroundService`: старт / таймер / остановка, логирование |
| `src/SzDiag.Hub/Program.cs` (изменить) | Регистрация `IKbBackup` + hosted service |
| `tests/SzDiag.Kb.Tests/KbGitBackupTests.cs` (создать) | Поведение бэкапа на временном репо |
| `tests/SzDiag.Hub.Tests/KbBackupServiceTests.cs` (создать) | Рубильник `Enabled`, устойчивость к исключениям |
| `tools/kb-backup.ps1` (изменить) | Только ручной прогон + `-Uninstall` |
| `tools/build-dist.ps1` (изменить) | Секция `KbBackup` в генерируемом `appsettings.json` хаба |
| `CLAUDE.md` (изменить) | Штатный путь бэкапа — hub |

---

### Task 1: KbGitBackup в SzDiag.Kb

**Files:**
- Create: `src/SzDiag.Kb/KbBackupResult.cs`
- Create: `src/SzDiag.Kb/KbGitBackup.cs`
- Test: `tests/SzDiag.Kb.Tests/KbGitBackupTests.cs`

**Interfaces:**
- Consumes: ничего (первая задача).
- Produces:
  - `enum KbBackupOutcome { NoChanges, Pushed, CommittedNotPushed, Failed }`
  - `sealed record KbBackupResult(KbBackupOutcome Outcome, int ChangedFiles, string Message)`
  - `interface IKbBackup { Task<KbBackupResult> RunAsync(CancellationToken ct); }`
  - `sealed class KbGitBackup : IKbBackup` с конструктором `KbGitBackup(string vaultRoot, string remote, string branch, TimeSpan commandTimeout)`

- [ ] **Step 1: Написать контракт результата**

Файл `src/SzDiag.Kb/KbBackupResult.cs`:

```csharp
namespace SzDiag.Kb;

/// <summary>Чем закончился прогон бэкапа vault'а.</summary>
public enum KbBackupOutcome
{
    /// <summary>Vault не менялся — коммита не было.</summary>
    NoChanges,

    /// <summary>Закоммичено и выгружено в remote.</summary>
    Pushed,

    /// <summary>Коммит лёг локально, push не прошёл (сеть/креды). Данные не потеряны.</summary>
    CommittedNotPushed,

    /// <summary>Прогон не удался: не git-репозиторий, упал add/commit, таймаут.</summary>
    Failed,
}

/// <param name="ChangedFiles">Сколько файлов попало в коммит (0, если коммита не было).</param>
/// <param name="Message">Человекочитаемая причина/итог — уходит в лог хаба.</param>
public sealed record KbBackupResult(KbBackupOutcome Outcome, int ChangedFiles, string Message);

/// <summary>Оффсайт-бэкап базы знаний. Отдельный интерфейс — чтобы hub тестировался без git.</summary>
public interface IKbBackup
{
    Task<KbBackupResult> RunAsync(CancellationToken ct);
}
```

- [ ] **Step 2: Написать падающие тесты**

Файл `tests/SzDiag.Kb.Tests/KbGitBackupTests.cs`:

```csharp
using System.Diagnostics;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class KbGitBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szkb-git-{Guid.NewGuid():N}");
    private readonly string _vault;
    private readonly string _remote;

    public KbGitBackupTests()
    {
        _vault = Path.Combine(_root, "vault");
        _remote = Path.Combine(_root, "remote.git");
        Directory.CreateDirectory(_vault);
        Directory.CreateDirectory(_remote);

        Git(_remote, "init --bare --initial-branch=main");
        Git(_vault, "init --initial-branch=main");
        // Без user.name/user.email git отказывается коммитить.
        Git(_vault, "config user.email test@szdiag.local");
        Git(_vault, "config user.name SzDiagTest");
        Git(_vault, $"remote add origin \"{_remote}\"");

        // Стартовый коммит: чтобы у ветки main была история и push имел что толкать.
        File.WriteAllText(Path.Combine(_vault, "README.md"), "kb");
        Git(_vault, "add -A");
        Git(_vault, "commit -m init");
        Git(_vault, "push origin main");
    }

    private KbGitBackup NewBackup(string? vault = null, string remoteName = "origin")
        => new(vault ?? _vault, remoteName, "main", TimeSpan.FromMinutes(1));

    [Fact]
    public async Task RunAsync_NoChanges_ReturnsNoChanges()
    {
        var result = await NewBackup().RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.NoChanges, result.Outcome);
        Assert.Equal(0, result.ChangedFiles);
        Assert.Equal(1, CountRemoteCommits());
    }

    [Fact]
    public async Task RunAsync_NewFile_PushesToRemote()
    {
        File.WriteAllText(Path.Combine(_vault, "нотатка.md"), "текст");

        var result = await NewBackup().RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.Pushed, result.Outcome);
        Assert.Equal(1, result.ChangedFiles);
        Assert.Equal(2, CountRemoteCommits());
        Assert.Contains("нотатка.md", GitOut(_remote, "ls-tree --name-only -r main"));
    }

    [Fact]
    public async Task RunAsync_DeletedFile_IsAlsoBackedUp()
    {
        File.Delete(Path.Combine(_vault, "README.md"));

        var result = await NewBackup().RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.Pushed, result.Outcome);
        Assert.DoesNotContain("README.md", GitOut(_remote, "ls-tree --name-only -r main"));
    }

    [Fact]
    public async Task RunAsync_CommitSubject_HasCyrillicAndNoBom()
    {
        File.WriteAllText(Path.Combine(_vault, "нотатка.md"), "текст");

        await NewBackup().RunAsync(CancellationToken.None);

        var subject = GitOut(_vault, "log -1 --pretty=%s").Trim();
        Assert.StartsWith("kb: автосохранение", subject);
        // BOM (U+FEFF) — escape-последовательностью: голый символ в исходнике невидим и теряется при копировании.
        Assert.DoesNotContain('\uFEFF', subject);
    }

    [Fact]
    public async Task RunAsync_UnreachableRemote_CommitsLocallyAndReports()
    {
        Git(_vault, $"remote add broken \"{Path.Combine(_root, "нет-такого.git")}\"");
        File.WriteAllText(Path.Combine(_vault, "нотатка.md"), "текст");

        var result = await NewBackup(remoteName: "broken").RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.CommittedNotPushed, result.Outcome);
        Assert.Equal(2, CountLocalCommits());
        Assert.NotEmpty(result.Message);
    }

    [Fact]
    public async Task RunAsync_NotAGitRepo_ReturnsFailedWithoutThrowing()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        var result = await NewBackup(vault: plain).RunAsync(CancellationToken.None);

        Assert.Equal(KbBackupOutcome.Failed, result.Outcome);
        Assert.Contains(plain, result.Message);
    }

    private int CountRemoteCommits()
        => int.Parse(GitOut(_remote, "rev-list --count main").Trim());

    private int CountLocalCommits()
        => int.Parse(GitOut(_vault, "rev-list --count main").Trim());

    private static void Git(string cwd, string args) => GitOut(cwd, args);

    private static string GitOut(string cwd, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {args}: {stderr}{stdout}");
        return stdout;
    }

    public void Dispose()
    {
        // .git держит файлы только read-only — снимаем атрибут, иначе Delete падает.
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(f, FileAttributes.Normal);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
```

- [ ] **Step 3: Запустить тесты и убедиться, что падают**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~KbGitBackupTests`
Expected: FAIL — компиляция не проходит, `KbGitBackup` не существует.

- [ ] **Step 4: Реализовать KbGitBackup**

Файл `src/SzDiag.Kb/KbGitBackup.cs`:

```csharp
using System.Diagnostics;
using System.Text;

namespace SzDiag.Kb;

/// <summary>
/// Коммитит изменения vault'а и пушит в remote. Дёргает системный git.exe:
/// он уже стоит на боксе и сам берёт креды из Windows Credential Manager.
/// </summary>
public sealed class KbGitBackup : IKbBackup
{
    private readonly string _vaultRoot;
    private readonly string _remote;
    private readonly string _branch;
    private readonly TimeSpan _commandTimeout;

    public KbGitBackup(string vaultRoot, string remote, string branch, TimeSpan commandTimeout)
    {
        _vaultRoot = Path.GetFullPath(vaultRoot);
        _remote = remote;
        _branch = branch;
        _commandTimeout = commandTimeout;
    }

    public async Task<KbBackupResult> RunAsync(CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(_vaultRoot, ".git")))
            return new KbBackupResult(KbBackupOutcome.Failed, 0, $"не git-репозиторий: {_vaultRoot}");

        try
        {
            var add = await RunGitAsync("add -A", ct);
            if (add.ExitCode != 0)
                return new KbBackupResult(KbBackupOutcome.Failed, 0, $"git add: {add.Error}");

            var status = await RunGitAsync("status --porcelain", ct);
            if (status.ExitCode != 0)
                return new KbBackupResult(KbBackupOutcome.Failed, 0, $"git status: {status.Error}");

            var changed = status.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Length;
            if (changed == 0)
                return new KbBackupResult(KbBackupOutcome.NoChanges, 0, "изменений нет");

            var commit = await CommitAsync(changed, ct);
            if (commit.ExitCode != 0)
            {
                // Сюда же прилетает отлуп pre-commit hook'а (жирный файл в vault).
                return new KbBackupResult(KbBackupOutcome.Failed, 0,
                    $"git commit: {First(commit.Error, commit.Output)}");
            }

            var push = await RunGitAsync($"push {_remote} {_branch}", ct);
            if (push.ExitCode != 0)
            {
                return new KbBackupResult(KbBackupOutcome.CommittedNotPushed, changed,
                    First(push.Error, push.Output));
            }

            return new KbBackupResult(KbBackupOutcome.Pushed, changed, "выгружено");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new KbBackupResult(KbBackupOutcome.Failed, 0, ex.Message);
        }
    }

    private async Task<GitRun> CommitAsync(int changed, CancellationToken ct)
    {
        var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm");
        var msgFile = Path.Combine(Path.GetTempPath(), $"kb-backup-msg-{Guid.NewGuid():N}.txt");
        // Строго UTF-8 БЕЗ BOM: иначе git утаскивает BOM в первую строку заголовка коммита.
        await File.WriteAllTextAsync(
            msgFile, $"kb: автосохранение {stamp} ({changed} файл(ов))",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct);
        try
        {
            return await RunGitAsync($"commit -F \"{msgFile}\"", ct);
        }
        finally
        {
            try { File.Delete(msgFile); } catch (IOException) { }
        }
    }

    private async Task<GitRun> RunGitAsync(string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _vaultRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"не удалось запустить git {args}");

        // Потоки читаем параллельно с ожиданием: заполненный буфер pipe вешает процесс.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_commandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Виснет сеть — убиваем git, hub не должен ждать его вечно.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new GitRun(-1, "", $"git {args}: таймаут {_commandTimeout}");
        }

        return new GitRun(process.ExitCode, await stdout, await stderr);
    }

    private static string First(string error, string output)
    {
        var text = string.IsNullOrWhiteSpace(error) ? output : error;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "без деталей" : line;
    }

    private readonly record struct GitRun(int ExitCode, string Output, string Error);
}
```

- [ ] **Step 5: Запустить тесты и убедиться, что проходят**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter FullyQualifiedName~KbGitBackupTests`
Expected: PASS, 6 тестов.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Kb/KbBackupResult.cs src/SzDiag.Kb/KbGitBackup.cs tests/SzDiag.Kb.Tests/KbGitBackupTests.cs
git commit -m "feat(kb): KbGitBackup — коммит и push vault'а через системный git"
```

---

### Task 2: KbBackupService в hub

**Files:**
- Create: `src/SzDiag.Hub/KbBackupService.cs`
- Modify: `src/SzDiag.Hub/HubOptions.cs` (добавить секцию в конец класса)
- Modify: `src/SzDiag.Hub/Program.cs:41` (рядом с `AddHostedService<OfflineSweeper>()`)
- Test: `tests/SzDiag.Hub.Tests/KbBackupServiceTests.cs`

**Interfaces:**
- Consumes: `IKbBackup`, `KbBackupResult`, `KbBackupOutcome` из Task 1.
- Produces: `HubOptions.KbBackup` типа `KbBackupOptions` (`Enabled`, `Interval`, `Remote`, `Branch`, `CommandTimeout`); `sealed class KbBackupService : BackgroundService` с конструктором `(IKbBackup backup, IOptions<HubOptions> options, ILogger<KbBackupService> logger)`.

- [ ] **Step 1: Написать падающие тесты**

Файл `tests/SzDiag.Hub.Tests/KbBackupServiceTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class KbBackupServiceTests
{
    private static KbBackupService NewService(IKbBackup backup, bool enabled)
    {
        var opts = new HubOptions
        {
            KbBackup = new KbBackupOptions
            {
                Enabled = enabled,
                // Крупный интервал: в тестах нас интересуют прогоны на старте и остановке,
                // а не тики таймера.
                Interval = TimeSpan.FromHours(1),
            },
        };
        return new KbBackupService(backup, Options.Create(opts), NullLogger<KbBackupService>.Instance);
    }

    [Fact]
    public async Task Disabled_NeverRunsBackup()
    {
        var backup = new FakeBackup();
        var service = NewService(backup, enabled: false);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, backup.Calls);
    }

    [Fact]
    public async Task Enabled_RunsOnStartAndOnStop()
    {
        var backup = new FakeBackup();
        var service = NewService(backup, enabled: true);

        await service.StartAsync(CancellationToken.None);
        await backup.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, backup.Calls);
    }

    [Fact]
    public async Task BackupThrows_ServiceSurvives()
    {
        var backup = new FakeBackup { Throw = true };
        var service = NewService(backup, enabled: true);

        await service.StartAsync(CancellationToken.None);
        await backup.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, backup.Calls);
    }

    private sealed class FakeBackup : IKbBackup
    {
        private int _calls;
        public bool Throw { get; init; }
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<KbBackupResult> RunAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1) FirstCall.TrySetResult();
            if (Throw) throw new InvalidOperationException("git сломался");
            return Task.FromResult(new KbBackupResult(KbBackupOutcome.NoChanges, 0, "изменений нет"));
        }
    }
}
```

- [ ] **Step 2: Запустить тесты и убедиться, что падают**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~KbBackupServiceTests`
Expected: FAIL — компиляция не проходит, `KbBackupService`/`KbBackupOptions` не существуют.

- [ ] **Step 3: Добавить опции в HubOptions**

В `src/SzDiag.Hub/HubOptions.cs` — новое свойство в конец класса `HubOptions` (после `StickyHeader`):

```csharp
    /// <summary>Оффсайт-бэкап базы знаний в git-remote.</summary>
    public KbBackupOptions KbBackup { get; set; } = new();
```

И новый класс в том же файле, после `HubOptions`:

```csharp
public sealed class KbBackupOptions
{
    /// <summary>Рубильник: false — сервис не стартует (vault не под git).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Период автоматического прогона.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Имя remote, куда пушим.</summary>
    public string Remote { get; set; } = "origin";

    /// <summary>Ветка, куда пушим.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Потолок на каждый вызов git: виснет сеть — процесс убивается.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
```

- [ ] **Step 4: Реализовать KbBackupService**

Файл `src/SzDiag.Hub/KbBackupService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>
/// Фоновый оффсайт-бэкап базы знаний: прогон на старте (догнать правки, сделанные
/// руками, пока hub не был поднят), дальше по таймеру, и финальный — при остановке.
/// </summary>
public sealed class KbBackupService : BackgroundService
{
    private readonly IKbBackup _backup;
    private readonly KbBackupOptions _options;
    private readonly ILogger<KbBackupService> _logger;

    public KbBackupService(IKbBackup backup, IOptions<HubOptions> options, ILogger<KbBackupService> logger)
    {
        _backup = backup;
        _options = options.Value.KbBackup;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;

        await RunSafeAsync(stoppingToken);

        using var timer = new PeriodicTimer(_options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafeAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (!_options.Enabled) return;

        // Финальный прогон: закрыли hub — свежак уехал сразу. Токен не передаём (он уже
        // отменён остановкой); прогон ограничен shutdown-таймаутом хоста, и если не успел —
        // изменения просто уедут при следующем старте.
        await RunSafeAsync(CancellationToken.None);
    }

    private async Task RunSafeAsync(CancellationToken ct)
    {
        try
        {
            var result = await _backup.RunAsync(ct);
            switch (result.Outcome)
            {
                case KbBackupOutcome.NoChanges:
                    _logger.LogDebug("kb: изменений нет");
                    break;
                case KbBackupOutcome.Pushed:
                    _logger.LogInformation("kb: выгружено {Count} файл(ов)", result.ChangedFiles);
                    break;
                case KbBackupOutcome.CommittedNotPushed:
                    _logger.LogWarning("kb: закоммичено локально, push не прошёл: {Reason}", result.Message);
                    break;
                default:
                    _logger.LogWarning("kb: бэкап не прошёл: {Reason}", result.Message);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Штатная остановка hub.
        }
        catch (Exception ex)
        {
            // Unhandled из BackgroundService валит весь хост — ловим всё.
            _logger.LogWarning(ex, "kb: бэкап упал");
        }
    }
}
```

- [ ] **Step 5: Зарегистрировать в Program.cs**

В `src/SzDiag.Hub/Program.cs`, сразу после регистрации `IReportStore` (строка 40) и перед `AddHostedService<OfflineSweeper>()`:

```csharp
builder.Services.AddSingleton<IKbBackup>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new KbGitBackup(
        opts.KnowledgeBaseRoot, opts.KbBackup.Remote, opts.KbBackup.Branch, opts.KbBackup.CommandTimeout);
});
builder.Services.AddHostedService<KbBackupService>();
```

- [ ] **Step 6: Запустить тесты и убедиться, что проходят**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~KbBackupServiceTests`
Expected: PASS, 3 теста.

- [ ] **Step 7: Прогнать весь solution**

Run: `dotnet test`
Expected: PASS, всё зелёное (было ~174 теста, стало ~183).

- [ ] **Step 8: Коммит**

```bash
git add src/SzDiag.Hub/KbBackupService.cs src/SzDiag.Hub/HubOptions.cs src/SzDiag.Hub/Program.cs tests/SzDiag.Hub.Tests/KbBackupServiceTests.cs
git commit -m "feat(hub): KbBackupService — бэкап kb на старте, по таймеру и при остановке"
```

---

### Task 3: Миграция — снять планировщик, обновить скрипт и доки

**Files:**
- Modify: `tools/kb-backup.ps1` (сейчас untracked — коммитится здесь же)
- Modify: `tools/build-dist.ps1:196-210` (секция `$hubCfg`)
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: `KbBackupOptions` из Task 2 — имена ключей конфига (`Enabled`, `Interval`, `Remote`, `Branch`, `CommandTimeout`).
- Produces: ничего для последующих задач.

- [ ] **Step 1: Снять задачу планировщика**

Run: `.\tools\kb-backup.ps1 -Uninstall`
Expected: `Задача 'SzDiag-KbBackup' снята`.

Проверка: `Get-ScheduledTask -TaskName SzDiag-KbBackup -ErrorAction SilentlyContinue` — пусто.

- [ ] **Step 2: Выкинуть установку расписания из скрипта**

В `tools/kb-backup.ps1` удалить параметры `-Install`/`-IntervalMinutes` (строки 14-25 справки, `$Install`/`$IntervalMinutes` в `param`, весь блок `if ($Install) { ... }` — строки 54-77). Оставить `-Uninstall`: на машинах, где задача уже стоит, её надо чем-то снести.

Заменить блок `.SYNOPSIS`/`.DESCRIPTION` (строки 1-26) на:

```powershell
<#
.SYNOPSIS
    Ручной оффсайт-бэкап базы знаний: коммитит изменения в kb и пушит в приватный репо.

.DESCRIPTION
    Штатно бэкап делает сам hub (KbBackupService: на старте, каждые 15 минут и при
    остановке; настройки — секция Hub.KbBackup в appsettings.json). Этот скрипт нужен,
    когда hub не поднят, а выгрузить надо сейчас. Если в vault ничего не менялось —
    завершается тихо, без пустых коммитов.

.PARAMETER KbPath
    Путь к vault. По умолчанию — dist\host\kb относительно корня репозитория.

.PARAMETER Uninstall
    Снять старую задачу планировщика SzDiag-KbBackup (расписание переехало в hub).

.EXAMPLE
    .\tools\kb-backup.ps1              # разовый прогон вручную
    .\tools\kb-backup.ps1 -Uninstall   # снести задачу планировщика
#>
```

Блок `param` привести к:

```powershell
[CmdletBinding()]
param(
    [string]$KbPath,
    [switch]$Uninstall
)
```

Остальное (лог, git add/commit/push, комментарии про BOM и `2>&1`) не трогать.

- [ ] **Step 3: Проверить, что скрипт живой**

Run: `.\tools\kb-backup.ps1`
Expected: exit 0 — либо тихий выход (изменений нет), либо строка `Выгружено: N файл(ов)` в консоли и в `dist\host\kb-backup.log`.

- [ ] **Step 4: Добавить секцию KbBackup в генерируемый конфиг хаба**

В `tools/build-dist.ps1`, в here-string `$hubCfg` (строки 196-210), после `"SweepInterval": "00:00:15"` добавить запятую и блок:

```
    "SweepInterval": "00:00:15",
    "KbBackup": {
      "Enabled": true,
      "Interval": "00:15:00",
      "Remote": "origin",
      "Branch": "main",
      "CommandTimeout": "00:02:00"
    }
```

- [ ] **Step 5: Пересобрать dist и убедиться, что конфиг валидный**

Run: `.\tools\build-dist.ps1`
Expected: сборка проходит; `Get-Content dist\host\hub\appsettings.json | ConvertFrom-Json` не ругается и содержит `Hub.KbBackup.Interval = 00:15:00`.

- [ ] **Step 6: Обновить CLAUDE.md**

В раздел «Команды», в блок с `dotnet`-командами и `build-dist`, добавить строку:

```powershell
.\tools\kb-backup.ps1              # ручная выгрузка kb в приватный репо (штатно это делает hub)
```

И абзацем ниже блока `build-dist`:

```markdown
**Бэкап базы знаний.** Vault (`dist\host\kb`) — git-репозиторий с приватным remote.
Бэкап делает сам hub (`KbBackupService`): прогон на старте, дальше каждые 15 минут и
финальный при остановке; настройки — секция `Hub.KbBackup` в `appsettings.json`
(`Enabled` — рубильник, если vault не под git). Правки, сделанные при выключенном hub,
уезжают при следующем старте. `tools\kb-backup.ps1` остаётся ручной кнопкой на случай,
когда hub не поднят. В vault — **только заметки и ссылки**: дампы, CSV с `lhmmon` и
скрины держать вне vault, иначе история репо раздувается необратимо.
```

- [ ] **Step 7: Коммит**

```bash
git add tools/kb-backup.ps1 tools/build-dist.ps1 CLAUDE.md
git commit -m "chore(kb): расписание бэкапа переехало в hub, скрипт остаётся ручным"
```

---

## Проверка результата вручную (после Task 3)

- [ ] Запустить `dist\host\start-hub.cmd`, положить в vault тестовый файл, подождать интервал (или перезапустить hub) — в консоли хаба появляется `kb: выгружено 1 файл(ов)`, коммит виден в `git -C dist\host\kb log origin/main -1`.
- [ ] Остановить hub по Ctrl+C с несохранённой правкой в vault — финальный прогон отрабатывает до выхода.
- [ ] Выдернуть сеть, изменить файл, дождаться прогона — в логе `kb: закоммичено локально, push не прошёл`, `git -C dist\host\kb log -1` показывает локальный коммит.
