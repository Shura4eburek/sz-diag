# sz-diag Фаза 3 — План 2: клиентская часть (тест-раннер + скрины в агенте)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Агент по push-команде `RunTests` прогоняет `testsuite.json` (CLI-команды + скриншоты), собирает `TestReport`, формирует `report.md` и заливает файлы на hub.

**Architecture:** `TestSuite` (конфиг) → `TestRunner` (использует `ICommandExecutor` и `IScreenCapturer`, строит `TestReport` + PNG-байты) → `TestReportRunner` (сборка md через `ReportMarkdownBuilder` из SzDiag.Kb + загрузка на hub через `IHubLink`). Захват экрана — `GdiScreenCapturer` (только Windows, юнитами не покрыт). Обработка push `RunTests` подключается в Program.

**Tech Stack:** .NET 8, C#, System.Drawing.Common (скриншот), xUnit.

**Предпосылка:** реализованы Фазы 1-2 и План 1 Фазы 3 (маршруты `RunTests`/`UploadReportFile`, `UploadReportPart`, приём на hub). Спека: [../specs/2026-07-01-phase3-test-runner-design.md](../specs/2026-07-01-phase3-test-runner-design.md).

---

## File Structure

```
src/SzDiag.Agent/
  SzDiag.Agent.csproj    — ССЫЛКА на SzDiag.Kb, пакет System.Drawing.Common, copy testsuite.json
  TestStep.cs            — модель шага конфига
  TestSuite.cs           — загрузка testsuite.json
  CommandResult.cs       — результат выполнения команды
  ICommandExecutor.cs / PowerShellCommandExecutor.cs — запуск команд (обёртка PowerShellRunner)
  IScreenCapturer.cs / GdiScreenCapturer.cs — снимок экрана
  TestRunner.cs          — прогон шагов -> TestReport + скрины
  TestReportRunner.cs    — оркестрация: run -> md -> загрузка
  IHubLink.cs            — ДОБАВИТЬ OnRunTests, UploadReportFileAsync
  SignalRHubLink.cs      — ДОБАВИТЬ реализацию
  AgentOptions.cs        — ДОБАВИТЬ TestSuitePath
  Program.cs             — подписка OnRunTests -> TestReportRunner
  testsuite.json         — дефолтный набор
tests/SzDiag.Agent.Tests/
  TestSuiteTests.cs, TestRunnerTests.cs, TestReportRunnerTests.cs
  AgentSessionTests.cs   — ДОБАВИТЬ заглушки новых членов IHubLink в FakeHubLink
```

---

### Task 1: Ссылка на Kb + TestSuite/TestStep

**Files:**
- Modify: `src/SzDiag.Agent/SzDiag.Agent.csproj`
- Create: `src/SzDiag.Agent/TestStep.cs`, `src/SzDiag.Agent/TestSuite.cs`
- Test: `tests/SzDiag.Agent.Tests/TestSuiteTests.cs`

- [ ] **Step 1: Ссылка на SzDiag.Kb**

Run:
```bash
dotnet add src/SzDiag.Agent reference src/SzDiag.Kb
```

- [ ] **Step 2: Модель шага и загрузчик**

`src/SzDiag.Agent/TestStep.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Шаг набора тестов: type = "command" | "screenshot".</summary>
public sealed record TestStep(string Type, string Name, string? Run = null);
```

`src/SzDiag.Agent/TestSuite.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Agent;

/// <summary>Набор тестов из testsuite.json.</summary>
public sealed class TestSuite
{
    public IReadOnlyList<TestStep> Steps { get; init; } = Array.Empty<TestStep>();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static TestSuite Load(string path)
        => JsonSerializer.Deserialize<TestSuite>(File.ReadAllText(path), Opts) ?? new TestSuite();
}
```

- [ ] **Step 3: Написать падающие тесты**

`tests/SzDiag.Agent.Tests/TestSuiteTests.cs`:
```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class TestSuiteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"suite-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_ParsesStepsInOrder()
    {
        File.WriteAllText(_path, """
            { "steps": [
              { "type": "command", "name": "Система", "run": "systeminfo" },
              { "type": "screenshot", "name": "Экран" }
            ] }
            """);

        var suite = TestSuite.Load(_path);

        Assert.Equal(2, suite.Steps.Count);
        Assert.Equal("command", suite.Steps[0].Type);
        Assert.Equal("systeminfo", suite.Steps[0].Run);
        Assert.Equal("screenshot", suite.Steps[1].Type);
        Assert.Null(suite.Steps[1].Run);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
```

- [ ] **Step 4: Запустить**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter TestSuiteTests`
Expected: PASS (1 тест).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/TestStep.cs src/SzDiag.Agent/TestSuite.cs src/SzDiag.Agent/SzDiag.Agent.csproj tests/SzDiag.Agent.Tests/TestSuiteTests.cs
git commit -m "feat(agent): TestSuite/TestStep + ссылка на SzDiag.Kb"
```

---

### Task 2: ICommandExecutor и IScreenCapturer

**Files:**
- Create: `src/SzDiag.Agent/CommandResult.cs`, `src/SzDiag.Agent/ICommandExecutor.cs`, `src/SzDiag.Agent/PowerShellCommandExecutor.cs`, `src/SzDiag.Agent/IScreenCapturer.cs`

Тонкие обёртки; покрываются через `TestRunner` фейками (Task 3) и VM (Task 4).

- [ ] **Step 1: Написать контракты и обёртку команд**

`src/SzDiag.Agent/CommandResult.cs`:
```csharp
namespace SzDiag.Agent;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr);
```

`src/SzDiag.Agent/ICommandExecutor.cs`:
```csharp
namespace SzDiag.Agent;

public interface ICommandExecutor
{
    CommandResult Run(string command);
}
```

`src/SzDiag.Agent/PowerShellCommandExecutor.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Выполняет команды теста через PowerShell (обёртка PowerShellRunner).</summary>
public sealed class PowerShellCommandExecutor : ICommandExecutor
{
    private readonly PowerShellRunner _ps;
    public PowerShellCommandExecutor(PowerShellRunner ps) => _ps = ps;

    public CommandResult Run(string command)
    {
        var r = _ps.Run(command, throwOnError: false);
        return new CommandResult(r.ExitCode, r.StdOut, r.StdErr);
    }
}
```

`src/SzDiag.Agent/IScreenCapturer.cs`:
```csharp
namespace SzDiag.Agent;

/// <summary>Результат захвата экрана: либо PNG-байты, либо причина недоступности.</summary>
public sealed record ScreenCapture(byte[]? Png, string? Error);

public interface IScreenCapturer
{
    ScreenCapture Capture();
}
```

- [ ] **Step 2: Сборка**

Run: `dotnet build src/SzDiag.Agent`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Agent/CommandResult.cs src/SzDiag.Agent/ICommandExecutor.cs src/SzDiag.Agent/PowerShellCommandExecutor.cs src/SzDiag.Agent/IScreenCapturer.cs
git commit -m "feat(agent): контракты исполнителя команд и захватчика экрана"
```

---

### Task 3: TestRunner

**Files:**
- Create: `src/SzDiag.Agent/TestRunner.cs`
- Test: `tests/SzDiag.Agent.Tests/TestRunnerTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Agent.Tests/TestRunnerTests.cs`:
```csharp
using SzDiag.Agent;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Agent.Tests;

public class TestRunnerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, CommandResult> _map;
        public FakeExecutor(Dictionary<string, CommandResult> map) => _map = map;
        public CommandResult Run(string command)
            => _map.TryGetValue(command, out var r) ? r : new CommandResult(0, "", "");
    }

    private sealed class FakeCapturer : IScreenCapturer
    {
        private readonly ScreenCapture _result;
        public FakeCapturer(ScreenCapture result) => _result = result;
        public ScreenCapture Capture() => _result;
    }

    private static readonly DateTimeOffset At = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Run_CommandStep_CapturesOutput()
    {
        var runner = new TestRunner(
            new FakeExecutor(new() { ["systeminfo"] = new CommandResult(0, "OS: Win", "") }),
            new FakeCapturer(new ScreenCapture(null, "n/a")));
        var suite = new TestSuite { Steps = new[] { new TestStep("command", "Система", "systeminfo") } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        var step = output.Report.Steps.Single();
        Assert.Equal(TestStepKind.Command, step.Kind);
        Assert.Equal("OS: Win", step.Output);
        Assert.Equal(0, step.ExitCode);
    }

    [Fact]
    public void Run_FailedCommand_RecordsErrorAndContinues()
    {
        var runner = new TestRunner(
            new FakeExecutor(new() { ["bad"] = new CommandResult(1, "", "не найдено") }),
            new FakeCapturer(new ScreenCapture(null, "n/a")));
        var suite = new TestSuite { Steps = new[]
        {
            new TestStep("command", "Плохая", "bad"),
            new TestStep("command", "Хорошая", "ok"),
        } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        Assert.Equal(2, output.Report.Steps.Count);
        Assert.Contains("не найдено", output.Report.Steps[0].Error);
        Assert.Null(output.Report.Steps[1].Error);
    }

    [Fact]
    public void Run_ScreenshotStep_StoresPngAndAssignsFileName()
    {
        var png = new byte[] { 1, 2, 3 };
        var runner = new TestRunner(
            new FakeExecutor(new()),
            new FakeCapturer(new ScreenCapture(png, null)));
        var suite = new TestSuite { Steps = new[] { new TestStep("screenshot", "Экран") } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        var step = output.Report.Steps.Single();
        Assert.Equal("screen-1.png", step.ScreenshotFile);
        Assert.Equal(png, output.Screenshots["screen-1.png"]);
    }

    [Fact]
    public void Run_ScreenshotUnavailable_RecordsError()
    {
        var runner = new TestRunner(
            new FakeExecutor(new()),
            new FakeCapturer(new ScreenCapture(null, "нет сессии")));
        var suite = new TestSuite { Steps = new[] { new TestStep("screenshot", "Экран") } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        Assert.Equal("нет сессии", output.Report.Steps.Single().Error);
        Assert.Empty(output.Screenshots);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter TestRunnerTests`
Expected: FAIL — `TestRunner` не существует.

- [ ] **Step 3: Реализовать TestRunner**

`src/SzDiag.Agent/TestRunner.cs`:
```csharp
using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Результат прогона: отчёт + байты скриншотов по именам файлов.</summary>
public sealed record TestRunOutput(TestReport Report, IReadOnlyDictionary<string, byte[]> Screenshots);

/// <summary>Выполняет шаги набора: команды и скриншоты. Падение шага фиксируется, прогон продолжается.</summary>
public sealed class TestRunner
{
    private readonly ICommandExecutor _exec;
    private readonly IScreenCapturer _capturer;

    public TestRunner(ICommandExecutor exec, IScreenCapturer capturer)
    {
        _exec = exec;
        _capturer = capturer;
    }

    public TestRunOutput Run(TestSuite suite, string sz, string hostname, DateTimeOffset now)
    {
        var steps = new List<TestStepResult>();
        var shots = new Dictionary<string, byte[]>();
        var shotN = 0;

        foreach (var step in suite.Steps)
        {
            if (step.Type.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
            {
                var cap = _capturer.Capture();
                if (cap.Png is not null)
                {
                    shotN++;
                    var fn = $"screen-{shotN}.png";
                    shots[fn] = cap.Png;
                    steps.Add(new TestStepResult(step.Name, TestStepKind.Screenshot, ScreenshotFile: fn));
                }
                else
                {
                    steps.Add(new TestStepResult(step.Name, TestStepKind.Screenshot, Error: cap.Error ?? "неизвестно"));
                }
                continue;
            }

            // command
            try
            {
                var r = _exec.Run(step.Run ?? "");
                steps.Add(r.ExitCode == 0
                    ? new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Output: r.StdOut, ExitCode: 0)
                    : new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Error: $"код {r.ExitCode}: {r.StdErr}"));
            }
            catch (Exception ex)
            {
                steps.Add(new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Error: ex.Message));
            }
        }

        return new TestRunOutput(new TestReport(sz, hostname, now, steps), shots);
    }
}
```

- [ ] **Step 4: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter TestRunnerTests`
Expected: PASS (4 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/TestRunner.cs tests/SzDiag.Agent.Tests/TestRunnerTests.cs
git commit -m "feat(agent): TestRunner — прогон шагов в TestReport"
```

---

### Task 4: GdiScreenCapturer + дефолтный testsuite.json

**Files:**
- Modify: `src/SzDiag.Agent/SzDiag.Agent.csproj` (пакет System.Drawing.Common + copy testsuite.json)
- Create: `src/SzDiag.Agent/GdiScreenCapturer.cs`, `src/SzDiag.Agent/testsuite.json`

> `GdiScreenCapturer` юнитами не покрывается (GUI/Windows) — проверка на VM (Task 7).

- [ ] **Step 1: Пакет System.Drawing.Common**

Run:
```bash
dotnet add src/SzDiag.Agent package System.Drawing.Common --version 8.0.0
```

- [ ] **Step 2: Реализовать GdiScreenCapturer**

`src/SzDiag.Agent/GdiScreenCapturer.cs`:
```csharp
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace SzDiag.Agent;

/// <summary>Снимок основного дисплея через GDI (BitBlt). Работает в интерактивной сессии.</summary>
[SupportedOSPlatform("windows")]
public sealed class GdiScreenCapturer : IScreenCapturer
{
    public ScreenCapture Capture()
    {
        try
        {
            var bounds = GetPrimaryBounds();
            using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return new ScreenCapture(ms.ToArray(), null);
        }
        catch (Exception ex)
        {
            return new ScreenCapture(null, ex.Message);
        }
    }

    private static Rectangle GetPrimaryBounds()
    {
        // Виртуальный экран основного монитора через P/Invoke метрики.
        var w = GetSystemMetrics(0); // SM_CXSCREEN
        var h = GetSystemMetrics(1); // SM_CYSCREEN
        return new Rectangle(0, 0, w, h);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
```

- [ ] **Step 3: Дефолтный testsuite.json + копирование**

`src/SzDiag.Agent/testsuite.json`:
```json
{
  "steps": [
    { "type": "command", "name": "Система", "run": "systeminfo" },
    { "type": "command", "name": "Диски и SMART", "run": "Get-PhysicalDisk | Format-List; Get-PhysicalDisk | Get-StorageReliabilityCounter | Format-List" },
    { "type": "command", "name": "Память", "run": "Get-CimInstance Win32_PhysicalMemory | Format-List Manufacturer,Capacity,Speed,PartNumber" },
    { "type": "command", "name": "Драйверы", "run": "driverquery" },
    { "type": "command", "name": "Ошибки System (48ч)", "run": "Get-WinEvent -FilterHashtable @{LogName='System';Level=1,2;StartTime=(Get-Date).AddHours(-48)} -ErrorAction SilentlyContinue | Select-Object -First 50 TimeCreated,Id,ProviderName,Message | Format-List" },
    { "type": "command", "name": "GPU (dxdiag)", "run": "$t=\"$env:TEMP\\dxdiag.txt\"; dxdiag /t $t; Start-Sleep 3; Get-Content $t" },
    { "type": "screenshot", "name": "Состояние экрана" }
  ]
}
```

В `src/SzDiag.Agent/SzDiag.Agent.csproj` в существующий `<ItemGroup>` с `appsettings.json` добавить строку:
```xml
    <None Update="testsuite.json" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 4: Сборка**

Run: `dotnet build src/SzDiag.Agent`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/GdiScreenCapturer.cs src/SzDiag.Agent/testsuite.json src/SzDiag.Agent/SzDiag.Agent.csproj
git commit -m "feat(agent): GdiScreenCapturer и дефолтный testsuite.json"
```

---

### Task 5: IHubLink — OnRunTests + UploadReportFileAsync

**Files:**
- Modify: `src/SzDiag.Agent/IHubLink.cs`, `src/SzDiag.Agent/SignalRHubLink.cs`, `tests/SzDiag.Agent.Tests/AgentSessionTests.cs`

- [ ] **Step 1: Расширить IHubLink**

В `src/SzDiag.Agent/IHubLink.cs` добавить в интерфейс (перед `ValueTask DisposeAsync();`):
```csharp
    /// <summary>Подписка на команду прогона тестов от hub (sz → callback).</summary>
    void OnRunTests(Func<string, Task> handler);

    Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part, CancellationToken ct = default);
```

- [ ] **Step 2: Реализовать в SignalRHubLink**

В `src/SzDiag.Agent/SignalRHubLink.cs` добавить методы в класс:
```csharp
    public void OnRunTests(Func<string, Task> handler)
        => _conn.On<string>(HubRoutes.RunTests, sz => handler(sz));

    public Task UploadReportFileAsync(UploadReportPart part, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.UploadReportFile, part, ct);
```

- [ ] **Step 3: Починить FakeHubLink в AgentSessionTests**

В `tests/SzDiag.Agent.Tests/AgentSessionTests.cs` в класс `FakeHubLink` добавить (перед `DisposeAsync`):
```csharp
        public List<SzDiag.Contracts.UploadReportPart> Uploaded { get; } = new();
        private Func<string, Task>? _onRunTests;
        public void OnRunTests(Func<string, Task> handler) => _onRunTests = handler;
        public Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part, CancellationToken ct = default)
        {
            Uploaded.Add(part);
            return Task.CompletedTask;
        }
        public Task FireRunTests(string sz) => _onRunTests!(sz);
```

- [ ] **Step 4: Прогнать тесты агента (регресс)**

Run: `dotnet test tests/SzDiag.Agent.Tests`
Expected: PASS — существующие тесты (`AgentSessionTests`, `TestSuiteTests`, `TestRunnerTests` и пр.) собираются и зелёные.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Agent/IHubLink.cs src/SzDiag.Agent/SignalRHubLink.cs tests/SzDiag.Agent.Tests/AgentSessionTests.cs
git commit -m "feat(agent): IHubLink — OnRunTests и UploadReportFileAsync"
```

---

### Task 6: TestReportRunner + подписка в Program

**Files:**
- Create: `src/SzDiag.Agent/TestReportRunner.cs`
- Modify: `src/SzDiag.Agent/AgentOptions.cs`, `src/SzDiag.Agent/Program.cs`
- Test: `tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs`:
```csharp
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class TestReportRunnerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        public CommandResult Run(string command) => new(0, "OK", "");
    }
    private sealed class FakeCapturer : IScreenCapturer
    {
        public ScreenCapture Capture() => new(new byte[] { 9 }, null);
    }
    private sealed class CapturingLink : IHubLink
    {
        public List<UploadReportPart> Uploaded { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterAsync(string sz, string hostname, CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) => Task.CompletedTask;
        public void OnRevert(Func<string, Task> handler) { }
        public void OnRunTests(Func<string, Task> handler) { }
        public Task UploadReportFileAsync(UploadReportPart part, CancellationToken ct = default)
        {
            Uploaded.Add(part);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAndUpload_UploadsReportMdAndScreenshotsWithSameTimestamp()
    {
        var suite = new TestSuite { Steps = new[]
        {
            new TestStep("command", "Система", "systeminfo"),
            new TestStep("screenshot", "Экран"),
        } };
        var runner = new TestRunner(new FakeExecutor(), new FakeCapturer());
        var link = new CapturingLink();
        var reportRunner = new TestReportRunner(runner, suite, link, "PC-1",
            () => new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero));

        await reportRunner.RunAndUploadAsync("156864");

        Assert.Contains(link.Uploaded, u => u.FileName == "report.md");
        Assert.Contains(link.Uploaded, u => u.FileName == "screen-1.png");
        Assert.All(link.Uploaded, u => Assert.Equal("156864", u.Sz));
        var ts = link.Uploaded.Select(u => u.Timestamp).Distinct().Single();
        Assert.Equal("20260701-123000", ts);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter TestReportRunnerTests`
Expected: FAIL — `TestReportRunner` не существует.

- [ ] **Step 3: Реализовать TestReportRunner**

`src/SzDiag.Agent/TestReportRunner.cs`:
```csharp
using System.Text;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Оркестрация: прогон набора → report.md → загрузка файлов на hub.</summary>
public sealed class TestReportRunner
{
    private readonly TestRunner _runner;
    private readonly TestSuite _suite;
    private readonly IHubLink _link;
    private readonly string _hostname;
    private readonly Func<DateTimeOffset> _now;

    public TestReportRunner(TestRunner runner, TestSuite suite, IHubLink link,
        string hostname, Func<DateTimeOffset>? now = null)
    {
        _runner = runner;
        _suite = suite;
        _link = link;
        _hostname = hostname;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public async Task RunAndUploadAsync(string sz, CancellationToken ct = default)
    {
        var now = _now();
        var timestamp = now.ToString("yyyyMMdd-HHmmss");
        var output = _runner.Run(_suite, sz, _hostname, now);

        var md = ReportMarkdownBuilder.Build(output.Report);
        await _link.UploadReportFileAsync(
            new UploadReportPart(sz, timestamp, "report.md", Encoding.UTF8.GetBytes(md)), ct);

        foreach (var (fileName, bytes) in output.Screenshots)
            await _link.UploadReportFileAsync(new UploadReportPart(sz, timestamp, fileName, bytes), ct);
    }
}
```

- [ ] **Step 4: Прогнать тест**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter TestReportRunnerTests`
Expected: PASS (1 тест).

- [ ] **Step 5: Подписать RunTests в Program.cs**

В `src/SzDiag.Agent/AgentOptions.cs` добавить свойство в класс:
```csharp
    public string TestSuitePath { get; set; } = "testsuite.json";
```

В `src/SzDiag.Agent/Program.cs` после строки
`Console.WriteLine($"СЗ {sz}: доступ открыт ● online. Хост {Environment.MachineName}.");`
добавить:
```csharp
// Тест-раннер: по команде hub RunTests прогнать набор и залить отчёт.
if (File.Exists(opts.TestSuitePath))
{
    var suite = TestSuite.Load(opts.TestSuitePath);
    var reportRunner = new TestReportRunner(
        new TestRunner(new PowerShellCommandExecutor(ps), new GdiScreenCapturer()),
        suite, link, Environment.MachineName);
    link.OnRunTests(async runSz =>
    {
        Console.WriteLine($"Прогон тестов для СЗ {runSz}…");
        try { await reportRunner.RunAndUploadAsync(runSz); Console.WriteLine("Отчёт залит на hub."); }
        catch (Exception ex) { Console.WriteLine($"Ошибка прогона: {ex.Message}"); }
    });
}
```

- [ ] **Step 6: Сборка и все тесты**

Run: `dotnet build`
Expected: Build succeeded.
Run: `dotnet test`
Expected: PASS — все тесты решения (kb + hub + cli + agent).

- [ ] **Step 7: VM/ручной чек-лист (одноразовая Windows-ВМ)**

На хосте запущены hub (План 1) и CLI. Агент опубликован и запущен на ВМ, СЗ открыта.
- [ ] На хосте `szcli test run <СЗ>` → печатает «прогон запущен».
- [ ] В окне агента появляется «Прогон тестов для СЗ …» → «Отчёт залит на hub».
- [ ] На хосте появляется `kb/СЗ/<СЗ>/reports/<timestamp>/report.md` с секциями шагов и `screen-1.png`.
- [ ] `report.md` открывается в Obsidian, скрин отображается (`![[screen-1.png]]`).
- [ ] Шаг с заведомо битой командой в `testsuite.json` → в отчёте «ошибка: …», остальные шаги прошли.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(agent): TestReportRunner + подписка RunTests в Program"
```

---

## Self-Review (выполнено при написании плана)

**Покрытие спеки (клиентская часть):**
- `TestSuite`/`TestStep` (парсинг конфига) → Task 1. ✓
- `ICommandExecutor`/`IScreenCapturer` → Task 2. ✓
- `TestRunner` (шаги, фиксация падения, продолжение) → Task 3. ✓
- `GdiScreenCapturer` + дефолтный `testsuite.json` → Task 4. ✓
- `IHubLink` OnRunTests/UploadReportFileAsync + SignalR → Task 5. ✓
- `TestReportRunner` (сборка md + загрузка) + подписка RunTests → Task 6. ✓
- VM-проверка захвата экрана и полного потока → Task 6 Step 7. ✓

**Плейсхолдеры:** отсутствуют.
**Согласованность типов:** `TestSuite`/`TestStep(Type,Name,Run)`, `CommandResult(ExitCode,StdOut,StdErr)`, `ScreenCapture(Png,Error)`, `TestRunner.Run(...)→TestRunOutput(Report,Screenshots)`, `TestReportRunner.RunAndUploadAsync(sz)`, `IHubLink.OnRunTests/UploadReportFileAsync`, `UploadReportPart` из Плана 1 — едины между задачами и тестами.
**Регресс:** новые члены `IHubLink` ломают `FakeHubLink` в `AgentSessionTests` — заглушки добавляются в Task 5 Step 3. `PowerShellRunner` переиспользуется из Фазы 1.
