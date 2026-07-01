# sz-diag Фаза 3 — План 1: серверная часть (отчёт в kb + приём на hub + CLI)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Модель отчёта и сборка `report.md` в `SzDiag.Kb`; приём файлов отчёта на hub с записью в `kb/СЗ/<n>/reports/<ts>/`; push-команда `RunTests` агенту; CLI-команда `test run <СЗ>`.

**Architecture:** `SzDiag.Kb` получает модель `TestReport` и `ReportMarkdownBuilder`. Hub принимает файлы по SignalR (`UploadReportFile`) и пишет их через `KbReportStore`; management-API `POST /api/sessions/{sz}/test` триггерит push `RunTests` агенту (симметрично закрытию СЗ). CLI `test run` дёргает эндпоинт.

**Tech Stack:** .NET 8, C#, ASP.NET Core SignalR, xUnit.

**Предпосылка:** Фазы 1-2 реализованы. Спека: [../specs/2026-07-01-phase3-test-runner-design.md](../specs/2026-07-01-phase3-test-runner-design.md).

---

## File Structure

```
src/SzDiag.Kb/
  TestStepResult.cs      — результат одного шага (модель)
  TestReport.cs          — отчёт целиком (модель)
  ReportMarkdownBuilder.cs — TestReport -> markdown
  KbPaths.cs             — ДОБАВИТЬ ReportsDir/ReportDir
src/SzDiag.Contracts/
  UploadReportPart.cs    — DTO одного файла отчёта (sz, timestamp, имя, байты)
  HubRoutes.cs           — ДОБАВИТЬ RunTests, UploadReportFile
src/SzDiag.Hub/
  IReportStore.cs        — контракт записи файлов отчёта
  KbReportStore.cs       — запись в kb/СЗ/<n>/reports/<ts>/ (санитизация имени)
  TestRunTrigger.cs      — резолв connId + push RunTests (симметрично SessionCloser)
  IAgentCommandSender.cs — ДОБАВИТЬ SendRunTestsAsync
  SignalRAgentCommandSender.cs — ДОБАВИТЬ реализацию
  AgentHub.cs            — ДОБАВИТЬ UploadReportFile
  ManagementApi.cs       — ДОБАВИТЬ POST /sessions/{sz}/test
  Program.cs             — DI + MaximumReceiveMessageSize
src/SzDiag.Cli/
  IHubApiClient.cs / HubApiClient.cs — ДОБАВИТЬ TriggerTestAsync
  Program.cs             — ветка test run
tests/SzDiag.Kb.Tests/   ReportMarkdownBuilderTests.cs
tests/SzDiag.Hub.Tests/  KbReportStoreTests.cs, TestRunTriggerTests.cs, ReportUploadIntegrationTests.cs
tests/SzDiag.Cli.Tests/  HubApiClientTests.cs (расширить)
```

---

### Task 1: Модель отчёта + пути в KbPaths

**Files:**
- Create: `src/SzDiag.Kb/TestStepResult.cs`, `src/SzDiag.Kb/TestReport.cs`
- Modify: `src/SzDiag.Kb/KbPaths.cs`
- Test: `tests/SzDiag.Kb.Tests/KbPathsTests.cs` (добавить кейс)

- [ ] **Step 1: Модели**

`src/SzDiag.Kb/TestStepResult.cs`:
```csharp
namespace SzDiag.Kb;

public enum TestStepKind { Command, Screenshot }

/// <summary>Результат одного шага прогона.</summary>
public sealed record TestStepResult(
    string Name,
    TestStepKind Kind,
    string? Command = null,
    string? Output = null,
    int? ExitCode = null,
    string? Error = null,
    string? ScreenshotFile = null);
```

`src/SzDiag.Kb/TestReport.cs`:
```csharp
namespace SzDiag.Kb;

/// <summary>Отчёт прогона диагностики по СЗ.</summary>
public sealed record TestReport(
    string Sz,
    string Hostname,
    DateTimeOffset RunAt,
    IReadOnlyList<TestStepResult> Steps);
```

- [ ] **Step 2: Добавить пути отчётов в KbPaths**

В `src/SzDiag.Kb/KbPaths.cs` добавить методы в класс `KbPaths` (после `LogsDir`):
```csharp
    public string ReportsDir(string sz) => Path.Combine(SzDir(sz), "reports");
    public string ReportDir(string sz, string timestamp) => Path.Combine(ReportsDir(sz), timestamp);
```

- [ ] **Step 3: Добавить тест пути**

В `tests/SzDiag.Kb.Tests/KbPathsTests.cs` добавить метод в класс `KbPathsTests`:
```csharp
    [Fact]
    public void ReportDir_UnderSzReports()
    {
        var p = new KbPaths("/vault");
        Assert.Equal(Path.Combine("/vault", "СЗ", "156864", "reports", "20260701-120000"),
            p.ReportDir("156864", "20260701-120000"));
    }
```

- [ ] **Step 4: Прогнать тест**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter KbPathsTests`
Expected: PASS (3 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Kb/TestStepResult.cs src/SzDiag.Kb/TestReport.cs src/SzDiag.Kb/KbPaths.cs tests/SzDiag.Kb.Tests/KbPathsTests.cs
git commit -m "feat(kb): модель отчёта диагностики и пути reports"
```

---

### Task 2: ReportMarkdownBuilder

**Files:**
- Create: `src/SzDiag.Kb/ReportMarkdownBuilder.cs`
- Test: `tests/SzDiag.Kb.Tests/ReportMarkdownBuilderTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Kb.Tests/ReportMarkdownBuilderTests.cs`:
```csharp
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Kb.Tests;

public class ReportMarkdownBuilderTests
{
    private static TestReport Report(params TestStepResult[] steps) =>
        new("156864", "PC-1", new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero), steps);

    [Fact]
    public void Build_IncludesHeaderWithSzHostAndDate()
    {
        var md = ReportMarkdownBuilder.Build(Report());
        Assert.Contains("# Отчёт диагностики — СЗ 156864", md);
        Assert.Contains("PC-1", md);
        Assert.Contains("2026-07-01", md);
    }

    [Fact]
    public void Build_CommandStep_ShowsCommandAndOutput()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Система", TestStepKind.Command, Command: "systeminfo", Output: "OS: Windows", ExitCode: 0)));
        Assert.Contains("## Система", md);
        Assert.Contains("systeminfo", md);
        Assert.Contains("OS: Windows", md);
    }

    [Fact]
    public void Build_FailedCommandStep_ShowsError()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Диски", TestStepKind.Command, Command: "smart", Error: "команда не найдена")));
        Assert.Contains("ошибка: команда не найдена", md);
    }

    [Fact]
    public void Build_ScreenshotStep_EmbedsImage()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Экран", TestStepKind.Screenshot, ScreenshotFile: "screen-1.png")));
        Assert.Contains("![[screen-1.png]]", md);
    }

    [Fact]
    public void Build_ScreenshotStep_Unavailable_ShowsError()
    {
        var md = ReportMarkdownBuilder.Build(Report(
            new TestStepResult("Экран", TestStepKind.Screenshot, Error: "нет сессии")));
        Assert.Contains("скрин недоступен: нет сессии", md);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter ReportMarkdownBuilderTests`
Expected: FAIL — `ReportMarkdownBuilder` не существует.

- [ ] **Step 3: Реализовать ReportMarkdownBuilder**

`src/SzDiag.Kb/ReportMarkdownBuilder.cs`:
```csharp
using System.Text;

namespace SzDiag.Kb;

/// <summary>Собирает report.md из модели TestReport.</summary>
public static class ReportMarkdownBuilder
{
    public static string Build(TestReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Отчёт диагностики — СЗ {report.Sz}");
        sb.AppendLine();
        sb.AppendLine($"- Хост: {report.Hostname}");
        sb.AppendLine($"- Дата: {report.RunAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        foreach (var s in report.Steps)
        {
            sb.AppendLine($"## {s.Name}");
            sb.AppendLine();
            if (s.Kind == TestStepKind.Command)
            {
                if (s.Command is not null)
                {
                    sb.AppendLine($"`{s.Command}`");
                    sb.AppendLine();
                }
                if (s.Error is not null)
                    sb.AppendLine($"ошибка: {s.Error}");
                else
                {
                    sb.AppendLine("```");
                    sb.AppendLine(s.Output ?? "");
                    sb.AppendLine("```");
                }
            }
            else // Screenshot
            {
                sb.AppendLine(s.Error is not null
                    ? $"скрин недоступен: {s.Error}"
                    : $"![[{s.ScreenshotFile}]]");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Kb.Tests --filter ReportMarkdownBuilderTests`
Expected: PASS (5 тестов).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Kb/ReportMarkdownBuilder.cs tests/SzDiag.Kb.Tests/ReportMarkdownBuilderTests.cs
git commit -m "feat(kb): ReportMarkdownBuilder — сборка report.md"
```

---

### Task 3: Контракты — UploadReportPart + маршруты

**Files:**
- Create: `src/SzDiag.Contracts/UploadReportPart.cs`
- Modify: `src/SzDiag.Contracts/HubRoutes.cs`

- [ ] **Step 1: DTO файла отчёта**

`src/SzDiag.Contracts/UploadReportPart.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>Один файл отчёта, загружаемый агентом на hub.</summary>
public sealed record UploadReportPart(string Sz, string Timestamp, string FileName, byte[] Content);
```

- [ ] **Step 2: Добавить маршруты**

В `src/SzDiag.Contracts/HubRoutes.cs` в класс `HubRoutes` добавить константы (рядом с `Revert`):
```csharp
    // Hub -> агент: запустить прогон тестов.
    public const string RunTests = nameof(RunTests);

    // Агент -> hub: загрузить файл отчёта.
    public const string UploadReportFile = nameof(UploadReportFile);
```

- [ ] **Step 3: Сборка**

Run: `dotnet build src/SzDiag.Contracts`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/SzDiag.Contracts/UploadReportPart.cs src/SzDiag.Contracts/HubRoutes.cs
git commit -m "feat(contracts): UploadReportPart и маршруты RunTests/UploadReportFile"
```

---

### Task 4: KbReportStore — запись файлов отчёта

**Files:**
- Create: `src/SzDiag.Hub/IReportStore.cs`, `src/SzDiag.Hub/KbReportStore.cs`
- Test: `tests/SzDiag.Hub.Tests/KbReportStoreTests.cs`

- [ ] **Step 1: Контракт**

`src/SzDiag.Hub/IReportStore.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>Запись файлов отчёта прогона в базу знаний.</summary>
public interface IReportStore
{
    /// <summary>Сохраняет файл в kb/СЗ/&lt;sz&gt;/reports/&lt;timestamp&gt;/. Возвращает полный путь.</summary>
    string Save(string sz, string timestamp, string fileName, byte[] content);
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Hub.Tests/KbReportStoreTests.cs`:
```csharp
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class KbReportStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szrep-{Guid.NewGuid():N}");

    [Fact]
    public void Save_WritesFileUnderReportsTimestamp()
    {
        var store = new KbReportStore(_root);
        var path = store.Save("156864", "20260701-120000", "report.md", "hi"u8.ToArray());

        var expected = new KbPaths(_root).ReportDir("156864", "20260701-120000");
        Assert.Equal(Path.Combine(expected, "report.md"), path);
        Assert.Equal("hi", File.ReadAllText(path));
    }

    [Fact]
    public void Save_SanitizesTraversalInFileName()
    {
        var store = new KbReportStore(_root);
        var path = store.Save("156864", "ts", "../../evil.md", "x"u8.ToArray());

        // имя схлопывается до evil.md внутри reports/ts
        Assert.EndsWith(Path.Combine("reports", "ts", "evil.md"), path);
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter KbReportStoreTests`
Expected: FAIL — `KbReportStore` не существует.

- [ ] **Step 4: Реализовать KbReportStore**

`src/SzDiag.Hub/KbReportStore.cs`:
```csharp
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Пишет файлы отчёта в kb через KbPaths. Имя файла санитизируется
/// (только имя, без каталогов) — защита от выхода за пределы reports/.</summary>
public sealed class KbReportStore : IReportStore
{
    private readonly KbPaths _paths;
    public KbReportStore(string kbRoot) => _paths = new KbPaths(kbRoot);

    public string Save(string sz, string timestamp, string fileName, byte[] content)
    {
        var safeName = Path.GetFileName(fileName);
        var dir = _paths.ReportDir(sz, timestamp);
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, safeName);
        File.WriteAllBytes(full, content);
        return full;
    }
}
```

- [ ] **Step 5: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter KbReportStoreTests`
Expected: PASS (2 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hub/IReportStore.cs src/SzDiag.Hub/KbReportStore.cs tests/SzDiag.Hub.Tests/KbReportStoreTests.cs
git commit -m "feat(hub): KbReportStore — запись файлов отчёта в kb"
```

---

### Task 5: Push RunTests агенту (TestRunTrigger)

**Files:**
- Modify: `src/SzDiag.Hub/IAgentCommandSender.cs`, `src/SzDiag.Hub/SignalRAgentCommandSender.cs`
- Create: `src/SzDiag.Hub/TestRunTrigger.cs`
- Test: `tests/SzDiag.Hub.Tests/TestRunTriggerTests.cs`

- [ ] **Step 1: Расширить IAgentCommandSender**

В `src/SzDiag.Hub/IAgentCommandSender.cs` добавить метод в интерфейс:
```csharp
    Task SendRunTestsAsync(string connectionId, string sz, CancellationToken ct = default);
```

В `src/SzDiag.Hub/SignalRAgentCommandSender.cs` добавить реализацию в класс:
```csharp
    public Task SendRunTestsAsync(string connectionId, string sz, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.RunTests, sz, ct);
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Hub.Tests/TestRunTriggerTests.cs`:
```csharp
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class TestRunTriggerTests
{
    private sealed class SpySender : IAgentCommandSender
    {
        public List<(string conn, string sz)> Reverts { get; } = new();
        public List<(string conn, string sz)> Tests { get; } = new();
        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) { Reverts.Add((c, sz)); return Task.CompletedTask; }
        public Task SendRunTestsAsync(string c, string sz, CancellationToken ct = default) { Tests.Add((c, sz)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Trigger_KnownSz_PushesRunTests()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpySender();
        var trigger = new TestRunTrigger(reg, sender);

        var ok = await trigger.TriggerAsync("156864");

        Assert.True(ok);
        Assert.Equal(("conn-1", "156864"), sender.Tests.Single());
    }

    [Fact]
    public async Task Trigger_UnknownSz_ReturnsFalse()
    {
        var trigger = new TestRunTrigger(new SessionRegistry(), new SpySender());
        Assert.False(await trigger.TriggerAsync("000000"));
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter TestRunTriggerTests`
Expected: FAIL — `TestRunTrigger` не существует (и/или интерфейс не собран у существующих фейков).

> Примечание: в `SessionCloserTests.cs` уже есть фейк `IAgentCommandSender` (`SpyCommandSender`).
> После добавления метода в интерфейс он не скомпилируется — добавить туда заглушку:
> ```csharp
>     public Task SendRunTestsAsync(string connectionId, string sz, CancellationToken ct = default) => Task.CompletedTask;
> ```

- [ ] **Step 4: Реализовать TestRunTrigger + починить фейк в SessionCloserTests**

`src/SzDiag.Hub/TestRunTrigger.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>Триггерит прогон тестов на агенте по номеру СЗ (push RunTests).</summary>
public sealed class TestRunTrigger
{
    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;

    public TestRunTrigger(SessionRegistry registry, IAgentCommandSender sender)
    {
        _registry = registry;
        _sender = sender;
    }

    public async Task<bool> TriggerAsync(string sz, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;
        await _sender.SendRunTestsAsync(connId, sz, ct);
        return true;
    }
}
```

В `tests/SzDiag.Hub.Tests/SessionCloserTests.cs` в класс `SpyCommandSender` добавить:
```csharp
        public Task SendRunTestsAsync(string connectionId, string sz, CancellationToken ct = default) => Task.CompletedTask;
```

- [ ] **Step 5: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter "TestRunTriggerTests|SessionCloserTests"`
Expected: PASS (2 + 3 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hub/IAgentCommandSender.cs src/SzDiag.Hub/SignalRAgentCommandSender.cs src/SzDiag.Hub/TestRunTrigger.cs tests/SzDiag.Hub.Tests/TestRunTriggerTests.cs tests/SzDiag.Hub.Tests/SessionCloserTests.cs
git commit -m "feat(hub): push RunTests агенту (TestRunTrigger)"
```

---

### Task 6: AgentHub.UploadReportFile + Program (лимит, DI, эндпоинт)

**Files:**
- Modify: `src/SzDiag.Hub/AgentHub.cs`, `src/SzDiag.Hub/ManagementApi.cs`, `src/SzDiag.Hub/Program.cs`
- Test: `tests/SzDiag.Hub.Tests/ReportUploadIntegrationTests.cs`

- [ ] **Step 1: Добавить приём файла в AgentHub**

В `src/SzDiag.Hub/AgentHub.cs` добавить поле и параметр конструктора для `IReportStore`,
и метод. Полный файл:
```csharp
using Microsoft.AspNetCore.SignalR;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>SignalR-хаб для агентов. Тонкий слой над сервисами.</summary>
public sealed class AgentHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IKnowledgeBaseScaffolder _kb;
    private readonly IReportStore _reports;

    public AgentHub(SessionRegistry registry, ISessionStore store,
        IKnowledgeBaseScaffolder kb, IReportStore reports)
    {
        _registry = registry;
        _store = store;
        _kb = kb;
        _reports = reports;
    }

    public async Task Register(RegisterRequest request)
    {
        var ip = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        _registry.Register(request.Sz, ip, request.Hostname, Context.ConnectionId);
        _kb.EnsureSkeleton(request.Sz);
        await _store.RecordOpenAsync(
            new SessionRecord(request.Sz, ip, request.Hostname, DateTimeOffset.UtcNow, null));
    }

    public Task Heartbeat(string sz)
    {
        _registry.Heartbeat(sz);
        return Task.CompletedTask;
    }

    public Task UploadReportFile(UploadReportPart part)
    {
        _reports.Save(part.Sz, part.Timestamp, part.FileName, part.Content);
        return Task.CompletedTask;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _registry.MarkOfflineByConnection(Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}
```

- [ ] **Step 2: Добавить эндпоинт test в ManagementApi**

В `src/SzDiag.Hub/ManagementApi.cs` добавить внутрь `MapManagementApi`, после эндпоинта target:
```csharp
        group.MapPost("/sessions/{sz}/test", async (string sz, TestRunTrigger trigger) =>
            await trigger.TriggerAsync(sz) ? Results.Ok() : Results.NotFound());
```

- [ ] **Step 3: DI, лимит, регистрация в Program.cs**

В `src/SzDiag.Hub/Program.cs`:

Заменить `builder.Services.AddSignalR();` на:
```csharp
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 10 * 1024 * 1024);
```

Рядом с регистрацией `IKnowledgeBaseScaffolder` добавить:
```csharp
builder.Services.AddSingleton<IReportStore>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<HubOptions>>().Value;
    return new KbReportStore(opts.KnowledgeBaseRoot);
});
builder.Services.AddSingleton<TestRunTrigger>();
```

- [ ] **Step 4: Написать интеграционный тест**

`tests/SzDiag.Hub.Tests/ReportUploadIntegrationTests.cs`:
```csharp
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using SzDiag.Contracts;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ReportUploadIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-rep-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-rep-{Guid.NewGuid():N}");

    public ReportUploadIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot));
    }

    [Fact]
    public async Task UploadReportFile_WritesIntoKb()
    {
        var handler = _factory.Server.CreateHandler();
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            }).Build();
        await conn.StartAsync();

        await conn.InvokeAsync(HubRoutes.UploadReportFile,
            new UploadReportPart("156864", "20260701-120000", "report.md", "hello"u8.ToArray()));

        var path = Path.Combine(new KbPaths(_kbRoot).ReportDir("156864", "20260701-120000"), "report.md");
        Assert.True(File.Exists(path));
        Assert.Equal("hello", File.ReadAllText(path));

        await conn.DisposeAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
```

- [ ] **Step 5: Собрать и прогнать все тесты hub**

Run: `dotnet test tests/SzDiag.Hub.Tests`
Expected: PASS — все тесты hub, включая новый `ReportUploadIntegrationTests`.
(Существующий `AgentHubIntegrationTests` тоже должен пройти — конструктор AgentHub получает `IReportStore` через DI, зарегистрированный в Program.)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(hub): приём файлов отчёта (UploadReportFile) + эндпоинт test + лимит"
```

---

### Task 7: CLI `test run <СЗ>`

**Files:**
- Modify: `src/SzDiag.Cli/IHubApiClient.cs`, `src/SzDiag.Cli/HubApiClient.cs`, `src/SzDiag.Cli/Program.cs`
- Test: `tests/SzDiag.Cli.Tests/HubApiClientTests.cs` (добавить кейс)

- [ ] **Step 1: Расширить клиент**

В `src/SzDiag.Cli/IHubApiClient.cs` добавить в интерфейс:
```csharp
    Task<bool> TriggerTestAsync(string sz, CancellationToken ct = default);
```

В `src/SzDiag.Cli/HubApiClient.cs` добавить метод в класс:
```csharp
    public async Task<bool> TriggerTestAsync(string sz, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/sessions/{sz}/test", null, ct);
        return resp.StatusCode == System.Net.HttpStatusCode.OK;
    }
```

- [ ] **Step 2: Добавить падающий тест**

В `tests/SzDiag.Cli.Tests/HubApiClientTests.cs` добавить метод в класс `HubApiClientTests`:
```csharp
    [Fact]
    public async Task TriggerTest_Ok_ReturnsTrue()
    {
        var client = NewClient(new StubHandler(System.Net.HttpStatusCode.OK));
        Assert.True(await client.TriggerTestAsync("156864"));
    }

    [Fact]
    public async Task TriggerTest_NotFound_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(System.Net.HttpStatusCode.NotFound));
        Assert.False(await client.TriggerTestAsync("000000"));
    }
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter HubApiClientTests`
Expected: FAIL — `TriggerTestAsync` не существует.

- [ ] **Step 4: Реализация (Step 1) + ветка в Program.cs**

В `src/SzDiag.Cli/Program.cs` добавить ветку в `switch` перед `default:`:
```csharp
    case "test" when args.Length >= 3 && args[1].Equals("run", StringComparison.OrdinalIgnoreCase):
        Console.WriteLine(await client.TriggerTestAsync(args[2])
            ? $"СЗ {args[2]}: прогон тестов запущен на агенте (отчёт появится в kb по завершении)."
            : $"СЗ {args[2]} не найдена среди активных.");
        break;
```

- [ ] **Step 5: Прогнать тесты и общую сборку**

Run: `dotnet test`
Expected: PASS — все тесты (kb + hub + cli + agent).

- [ ] **Step 6: Smoke — триггер против живого hub**

Run (Git Bash):
```bash
dotnet run --project src/SzDiag.Hub &
sleep 5
dotnet run --project src/SzDiag.Cli -- test run 000000   # агентов нет
kill %1
```
Expected: печатает «СЗ 000000 не найдена среди активных.» (агентов нет — 404 ожидаем).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(cli): команда test run <СЗ>"
```

---

## Self-Review (выполнено при написании плана)

**Покрытие спеки (серверная часть):**
- Модель отчёта + `ReportMarkdownBuilder` → Task 1-2. ✓
- `UploadReportPart` + маршруты `RunTests`/`UploadReportFile` → Task 3. ✓
- `KbReportStore` (запись в kb, санитизация) → Task 4. ✓
- Push `RunTests` (`TestRunTrigger` + sender) → Task 5. ✓
- Приём файла на hub + эндпоинт `POST /sessions/{sz}/test` + лимит 10 МБ → Task 6. ✓
- CLI `test run` → Task 7. ✓

**Плейсхолдеры:** отсутствуют.
**Согласованность типов:** `TestReport`/`TestStepResult`/`TestStepKind`, `UploadReportPart(Sz,Timestamp,FileName,Content)`, `IReportStore.Save(sz,timestamp,fileName,content)`, `HubRoutes.RunTests/UploadReportFile`, `IAgentCommandSender.SendRunTestsAsync`, `TestRunTrigger.TriggerAsync` — едины между задачами. AgentHub-конструктор обновлён с `IReportStore` (Task 6), DI-регистрация добавлена там же.
**Регресс:** добавление метода в `IAgentCommandSender` ломает фейк в `SessionCloserTests` — заглушка добавляется в Task 5 Step 4.
