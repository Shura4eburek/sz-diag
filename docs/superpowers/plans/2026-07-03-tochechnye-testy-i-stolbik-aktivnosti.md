# Точечный запуск тестов + столбик «Активность» — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Дать оператору (`szcli`) запускать отдельные тесты (`test run <СЗ> occt`) вместо полного цикла и показывать в таблице онлайн-СЗ живой столбик активности (`Тест OCCT 5мин 44сек`).

**Architecture:** Короткий `Id` у шага (`occt`/`tm5`/`furmark`/`3dmark`) — единая опора обеих фич. `RunTests(sz)` расширяется до `RunTests(sz, filter)` через всю цепочку CLI→hub→агент; агент фильтрует `suite.Steps` по `Id`. Активность: агент пушит `(label, sinceUtc?)` в hub на смену состояния, hub кладёт в `SessionRegistry`, CLI считает `elapsed` от `sinceUtc` и рисует 5-й столбец.

**Tech Stack:** .NET 8, C#, ASP.NET Core SignalR, Spectre.Console, xUnit.

**Проверки после каждой задачи:** `dotnet build` зелёный, `dotnet test` зелёный. Комментарии/вывод — на русском (конвенция репозитория).

---

### Task 1: Активность в контрактах и реестре сессий

**Files:**
- Modify: `src/SzDiag.Contracts/SessionInfo.cs`
- Modify: `src/SzDiag.Contracts/HubRoutes.cs`
- Modify: `src/SzDiag.Hub/SessionRegistry.cs`
- Test: `tests/SzDiag.Hub.Tests/SessionRegistryTests.cs`

- [ ] **Step 1: Написать падающие тесты**

Добавить в `SessionRegistryTests.cs` (в конец класса, перед закрывающей `}`):

```csharp
    [Fact]
    public void SetActivity_UpdatesActivityAndSince()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var since = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

        var ok = reg.SetActivity("156864", "Тест OCCT", since);

        Assert.True(ok);
        var s = reg.GetActive().Single();
        Assert.Equal("Тест OCCT", s.Activity);
        Assert.Equal(since, s.ActivitySince);
    }

    [Fact]
    public void SetActivity_UnknownSz_ReturnsFalse()
        => Assert.False(NewRegistry().SetActivity("000000", "x", null));

    [Fact]
    public void Heartbeat_PreservesActivity()
    {
        var reg = NewRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.SetActivity("156864", "Тест OCCT", DateTimeOffset.UtcNow);

        reg.Heartbeat("156864");

        Assert.Equal("Тест OCCT", reg.GetActive().Single().Activity);
    }
```

- [ ] **Step 2: Запустить — убедиться, что не компилируется/падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~SessionRegistryTests`
Expected: ошибка компиляции — нет `SetActivity`, нет свойств `Activity`/`ActivitySince`.

- [ ] **Step 3: Добавить поля в `SessionInfo`**

В `src/SzDiag.Contracts/SessionInfo.cs` заменить запись на:

```csharp
namespace SzDiag.Contracts;

/// <summary>Снимок активной сессии СЗ для реестра и CLI.</summary>
public sealed record SessionInfo(
    string Sz,
    string Ip,
    string Hostname,
    SessionStatus Status,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastHeartbeat,
    string Activity = "",
    DateTimeOffset? ActivitySince = null);
```

- [ ] **Step 4: Добавить константу метода в `HubRoutes`**

В `src/SzDiag.Contracts/HubRoutes.cs` после строки `public const string UploadReportFile = nameof(UploadReportFile);` добавить:

```csharp

    // Агент -> hub: сообщить текущую активность (метка + время старта).
    public const string ReportActivity = nameof(ReportActivity);
```

- [ ] **Step 5: Добавить `SetActivity` в `SessionRegistry`**

В `src/SzDiag.Hub/SessionRegistry.cs` после метода `Heartbeat` (перед `MarkOfflineByConnection`) добавить:

```csharp
    public bool SetActivity(string sz, string activity, DateTimeOffset? since)
    {
        if (!_bySz.TryGetValue(sz, out var e)) return false;
        _bySz[sz] = e with { Info = e.Info with { Activity = activity, ActivitySince = since } };
        return true;
    }
```

- [ ] **Step 6: Запустить тесты — зелёные**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~SessionRegistryTests`
Expected: PASS (все, включая новые три).

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Contracts/SessionInfo.cs src/SzDiag.Contracts/HubRoutes.cs src/SzDiag.Hub/SessionRegistry.cs tests/SzDiag.Hub.Tests/SessionRegistryTests.cs
git commit -m "feat(hub): активность сессии в SessionRegistry + контрактах"
```

---

### Task 2: Приём `ReportActivity` на хабе

**Files:**
- Modify: `src/SzDiag.Hub/AgentHub.cs`
- Test: `tests/SzDiag.Hub.Tests/AgentHubIntegrationTests.cs`

- [ ] **Step 1: Написать падающий интеграционный тест**

В `AgentHubIntegrationTests.cs` перед методом `Dispose()` добавить:

```csharp
    [Fact]
    public async Task ReportActivity_UpdatesRegistry()
    {
        var conn = BuildConnection("test-token");
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("156866", "PC-3"));

        await conn.InvokeAsync(HubRoutes.ReportActivity, "156866", "Тест OCCT", DateTimeOffset.UtcNow);

        var reg = _factory.Services.GetRequiredService<SessionRegistry>();
        var s = reg.GetActive().Single(x => x.Sz == "156866");
        Assert.Equal("Тест OCCT", s.Activity);
        Assert.NotNull(s.ActivitySince);

        await conn.DisposeAsync();
    }
```

- [ ] **Step 2: Запустить — падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AgentHubIntegrationTests.ReportActivity_UpdatesRegistry`
Expected: FAIL — хаб не знает метод `ReportActivity` (HubException).

- [ ] **Step 3: Добавить метод в `AgentHub`**

В `src/SzDiag.Hub/AgentHub.cs` после метода `Heartbeat` добавить:

```csharp
    public Task ReportActivity(string sz, string activity, DateTimeOffset? since)
    {
        _registry.SetActivity(sz, activity, since);
        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AgentHubIntegrationTests.ReportActivity_UpdatesRegistry`
Expected: PASS.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hub/AgentHub.cs tests/SzDiag.Hub.Tests/AgentHubIntegrationTests.cs
git commit -m "feat(hub): AgentHub.ReportActivity -> SessionRegistry"
```

---

### Task 3: Фильтр в `RunTests` (сторона хаба)

**Files:**
- Modify: `src/SzDiag.Hub/IAgentCommandSender.cs`
- Modify: `src/SzDiag.Hub/SignalRAgentCommandSender.cs`
- Modify: `src/SzDiag.Hub/TestRunTrigger.cs`
- Modify: `src/SzDiag.Hub/ManagementApi.cs`
- Test: `tests/SzDiag.Hub.Tests/TestRunTriggerTests.cs`

- [ ] **Step 1: Обновить фейк и написать падающий тест**

В `TestRunTriggerTests.cs` заменить `SpySender` и добавить тест. Класс `SpySender` целиком:

```csharp
    private sealed class SpySender : IAgentCommandSender
    {
        public List<(string conn, string sz)> Reverts { get; } = new();
        public List<(string conn, string sz, string? filter)> Tests { get; } = new();
        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) { Reverts.Add((c, sz)); return Task.CompletedTask; }
        public Task SendRunTestsAsync(string c, string sz, string? filter, CancellationToken ct = default) { Tests.Add((c, sz, filter)); return Task.CompletedTask; }
    }
```

В тесте `Trigger_KnownSz_PushesRunTests` заменить последнюю строку на:

```csharp
        Assert.Equal(("conn-1", "156864", (string?)null), sender.Tests.Single());
```

Добавить новый тест:

```csharp
    [Fact]
    public async Task Trigger_WithFilter_PassesFilterThrough()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpySender();
        var trigger = new TestRunTrigger(reg, sender);

        await trigger.TriggerAsync("156864", "occt");

        Assert.Equal("occt", sender.Tests.Single().filter);
    }
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~TestRunTriggerTests`
Expected: ошибка компиляции (сигнатуры `SendRunTestsAsync`/`TriggerAsync`).

- [ ] **Step 3: Обновить интерфейс `IAgentCommandSender`**

В `src/SzDiag.Hub/IAgentCommandSender.cs` заменить строку `SendRunTestsAsync` на:

```csharp
    Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default);
```

- [ ] **Step 4: Обновить `SignalRAgentCommandSender`**

В `src/SzDiag.Hub/SignalRAgentCommandSender.cs` заменить метод `SendRunTestsAsync` на:

```csharp
    public Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.RunTests, sz, filter, ct);
```

- [ ] **Step 5: Обновить `TestRunTrigger`**

В `src/SzDiag.Hub/TestRunTrigger.cs` заменить метод `TriggerAsync` на:

```csharp
    public async Task<bool> TriggerAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;
        await _sender.SendRunTestsAsync(connId, sz, filter, ct);
        return true;
    }
```

- [ ] **Step 6: Пробросить `filter` в эндпоинте**

В `src/SzDiag.Hub/ManagementApi.cs` заменить строку с `/sessions/{sz}/test` на:

```csharp
        group.MapPost("/sessions/{sz}/test", async (string sz, string? filter, TestRunTrigger trigger) =>
            await trigger.TriggerAsync(sz, filter) ? Results.Ok() : Results.NotFound());
```

(`filter` минимальный API берёт из query-строки `?filter=...` автоматически.)

- [ ] **Step 7: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~TestRunTriggerTests`
Expected: PASS.

- [ ] **Step 8: Коммит**

```bash
git add src/SzDiag.Hub/IAgentCommandSender.cs src/SzDiag.Hub/SignalRAgentCommandSender.cs src/SzDiag.Hub/TestRunTrigger.cs src/SzDiag.Hub/ManagementApi.cs tests/SzDiag.Hub.Tests/TestRunTriggerTests.cs
git commit -m "feat(hub): RunTests с фильтром шагов (?filter=)"
```

---

### Task 4: Фильтр в CLI

**Files:**
- Modify: `src/SzDiag.Cli/IHubApiClient.cs`
- Modify: `src/SzDiag.Cli/HubApiClient.cs`
- Modify: `src/SzDiag.Cli/Program.cs`
- Test: `tests/SzDiag.Cli.Tests/HubApiClientTests.cs`

- [ ] **Step 1: Написать падающий тест**

В `HubApiClientTests.cs` добавить тест (перед закрывающей `}` класса):

```csharp
    [Fact]
    public async Task TriggerTest_WithFilter_AppendsQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        await client.TriggerTestAsync("156864", "occt");

        Assert.Contains("filter=occt", handler.LastRequest!.RequestUri!.Query);
    }
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~HubApiClientTests.TriggerTest_WithFilter_AppendsQuery`
Expected: ошибка компиляции — у `TriggerTestAsync` нет параметра `filter`.

- [ ] **Step 3: Обновить интерфейс**

В `src/SzDiag.Cli/IHubApiClient.cs` заменить строку `TriggerTestAsync` на:

```csharp
    Task<bool> TriggerTestAsync(string sz, string? filter = null, CancellationToken ct = default);
```

- [ ] **Step 4: Обновить `HubApiClient`**

В `src/SzDiag.Cli/HubApiClient.cs` заменить метод `TriggerTestAsync` на:

```csharp
    public async Task<bool> TriggerTestAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(filter)
            ? $"/api/sessions/{sz}/test"
            : $"/api/sessions/{sz}/test?filter={Uri.EscapeDataString(filter)}";
        var resp = await _http.PostAsync(url, null, ct);
        return resp.StatusCode == HttpStatusCode.OK;
    }
```

- [ ] **Step 5: Прокинуть аргумент в CLI**

В `src/SzDiag.Cli/Program.cs` заменить `case "test"...` целиком на:

```csharp
    case "test" when args.Length >= 3 && args[1].Equals("run", StringComparison.OrdinalIgnoreCase):
        var testFilter = args.Length >= 4 ? args[3] : null;
        if (await client.TriggerTestAsync(args[2], testFilter))
        {
            var scope = testFilter is null ? "весь набор" : $"фильтр: {testFilter}";
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {args[2]}: прогон запущен[/] ({scope}) на агенте (отчёт появится в kb).");
        }
        else
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {args[2]} не найдена[/] среди активных.");
        break;
```

В блоке `default:` в строке про `test run` заменить справку на:

```csharp
              [yellow]szcli test run[/] [blue]<СЗ>[/] [grey][[occt|tm5,furmark|…]][/]  прогон тестов (все или по id)
```

- [ ] **Step 6: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~HubApiClientTests`
Expected: PASS.

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Cli/IHubApiClient.cs src/SzDiag.Cli/HubApiClient.cs src/SzDiag.Cli/Program.cs tests/SzDiag.Cli.Tests/HubApiClientTests.cs
git commit -m "feat(cli): test run <СЗ> [фильтр] -> ?filter="
```

---

### Task 5: Столбик «Активность» в таблице CLI

**Files:**
- Modify: `src/SzDiag.Cli/SessionTableRenderer.cs`
- Test: `tests/SzDiag.Cli.Tests/SessionTableRendererTests.cs`

- [ ] **Step 1: Написать падающие тесты**

В `SessionTableRendererTests.cs` в методе-хелпере `RenderToText` после создания `console` (перед `console.Write`) добавить строку, чтобы длинные строки не переносились:

```csharp
        console.Profile.Width = 200;
```

Добавить тесты (перед закрывающей `}` класса):

```csharp
    [Theory]
    [InlineData(45, "45сек")]
    [InlineData(60, "1мин 00сек")]
    [InlineData(344, "5мин 44сек")]
    [InlineData(3599, "59мин 59сек")]
    [InlineData(3600, "1ч 00мин")]
    [InlineData(3900, "1ч 05мин")]
    public void FormatElapsed_Formats(int seconds, string expected)
        => Assert.Equal(expected, SessionTableRenderer.FormatElapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Render_RunningSession_ShowsActivityLabel()
    {
        var now = new DateTimeOffset(2026, 7, 1, 15, 30, 0, TimeSpan.Zero);
        var sessions = new List<SessionInfo>
        {
            new("156864", "10.0.0.42", "PC-1", SessionStatus.Online, now, now,
                "Тест OCCT", now.AddSeconds(-344))
        };

        var text = RenderToText(SessionTableRenderer.Render(sessions, now));

        Assert.Contains("Активность", text);
        Assert.Contains("Тест OCCT", text);
    }
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~SessionTableRendererTests`
Expected: ошибка компиляции — нет `FormatElapsed`, у `Render` нет параметра `now`.

- [ ] **Step 3: Реализовать рендер и форматтер**

Заменить `src/SzDiag.Cli/SessionTableRenderer.cs` целиком на:

```csharp
using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public static class SessionTableRenderer
{
    public static Table Render(IReadOnlyList<SessionInfo> sessions, DateTimeOffset? now = null)
    {
        var nowV = now ?? DateTimeOffset.Now;
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("СЗ");
        table.AddColumn("Статус");
        table.AddColumn("IP");
        table.AddColumn("Хост");
        table.AddColumn("Активность");

        if (sessions.Count == 0)
        {
            table.AddRow("[dim]нет активных СЗ[/]", "", "", "", "");
            return table;
        }

        foreach (var s in sessions.OrderBy(x => x.Sz))
        {
            var status = s.Status == SessionStatus.Online
                ? "[green]● online[/]"
                : "[grey]○ offline[/]";
            table.AddRow(s.Sz, status, s.Ip, s.Hostname, ActivityCell(s, nowV));
        }
        return table;
    }

    /// <summary>Ячейка активности: идущий тест с тикающим временем, простой с меткой, или «—».</summary>
    private static string ActivityCell(SessionInfo s, DateTimeOffset now)
    {
        if (s.Status == SessionStatus.Offline || string.IsNullOrEmpty(s.Activity))
            return "[dim]—[/]";

        var text = Markup.Escape(s.Activity);
        if (s.ActivitySince is DateTimeOffset since)
            return $"[yellow]{text} {FormatElapsed(now - since)}[/]";
        return $"[grey]{text}[/]";
    }

    /// <summary>Человекочитаемое время: «44сек» / «5мин 44сек» / «1ч 05мин».</summary>
    public static string FormatElapsed(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var total = (int)t.TotalSeconds;
        if (total < 60) return $"{total}сек";
        if (total < 3600) return $"{total / 60}мин {total % 60:D2}сек";
        return $"{total / 3600}ч {(total % 3600) / 60:D2}мин";
    }
}
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~SessionTableRendererTests`
Expected: PASS (включая существующие два теста и новые).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Cli/SessionTableRenderer.cs tests/SzDiag.Cli.Tests/SessionTableRendererTests.cs
git commit -m "feat(cli): столбик «Активность» + FormatElapsed"
```

---

### Task 6: Колбэк прогресса в `TestRunner.Run`

**Files:**
- Modify: `src/SzDiag.Agent/TestRunner.cs`
- Test: `tests/SzDiag.Agent.Tests/TestRunnerTests.cs`

- [ ] **Step 1: Написать падающий тест**

В `TestRunnerTests.cs` добавить тест (перед закрывающей `}` класса):

```csharp
    [Fact]
    public void Run_InvokesOnStepForEachStepInOrder()
    {
        var runner = new TestRunner(
            new FakeExecutor(new() { ["systeminfo"] = new CommandResult(0, "OS", "") }),
            new FakeCapturer(new ScreenCapture(null, "n/a")));
        var suite = new TestSuite { Steps = new[]
        {
            new TestStep("command", "Система", "systeminfo"),
            new TestStep("screenshot", "Экран"),
        } };

        var seen = new List<string>();
        runner.Run(suite, "156864", "PC-1", At, s => seen.Add(s.Name));

        Assert.Equal(new[] { "Система", "Экран" }, seen);
    }
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~TestRunnerTests.Run_InvokesOnStepForEachStepInOrder`
Expected: ошибка компиляции — у `Run` нет 5-го параметра.

- [ ] **Step 3: Добавить параметр и вызов**

В `src/SzDiag.Agent/TestRunner.cs` заменить сигнатуру `Run` на:

```csharp
    public TestRunOutput Run(TestSuite suite, string sz, string hostname, DateTimeOffset now,
        Action<TestStep>? onStep = null)
```

Внутри `Run`, первой строкой внутри `foreach (var step in suite.Steps)` (перед первым `if`), добавить:

```csharp
            onStep?.Invoke(step);
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~TestRunnerTests`
Expected: PASS (новый тест + все существующие — параметр опциональный, старые вызовы валидны).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Agent/TestRunner.cs tests/SzDiag.Agent.Tests/TestRunnerTests.cs
git commit -m "feat(agent): TestRunner.Run — колбэк прогресса по шагам"
```

---

### Task 7: `Id` у шага + id в testsuite.json

**Files:**
- Modify: `src/SzDiag.Agent/TestStep.cs`
- Modify: `src/SzDiag.Agent/testsuite.json`
- Test: `tests/SzDiag.Agent.Tests/TestSuiteTests.cs`

- [ ] **Step 1: Написать падающий тест**

В `TestSuiteTests.cs` добавить тест (перед `Dispose`):

```csharp
    [Fact]
    public void Load_ParsesStepId()
    {
        File.WriteAllText(_path, """
            { "steps": [ { "type": "app", "name": "OCCT", "id": "occt", "exe": "x" } ] }
            """);

        var suite = TestSuite.Load(_path);

        Assert.Equal("occt", suite.Steps[0].Id);
    }
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~TestSuiteTests`
Expected: ошибка компиляции — у `TestStep` нет `Id`.

- [ ] **Step 3: Добавить `Id` в конец записи `TestStep`**

В `src/SzDiag.Agent/TestStep.cs` заменить хвост записи — строку
`    string? CompletionWindowClass = null);` на:

```csharp
    string? CompletionWindowClass = null,
    string? Id = null);
```

(Именно в конце — существующие позиционные вызовы `new TestStep("command", "Имя", "run")` и `Exe:`/`Args:` не должны сместиться.)

- [ ] **Step 4: Проставить `id` app-шагам в `testsuite.json`**

В `src/SzDiag.Agent/testsuite.json` в каждый из четырёх app-шагов добавить поле `"id"` сразу после `"type": "app",`:
- шаг TM5 (`"exe": "tools\\tm5\\TM5.exe"`) → `"id": "tm5",`
- шаг OCCT (`"exe": "tools\\occt\\OCCTCmd.exe"`) → `"id": "occt",`
- шаг FurMark (`"exe": "tools\\furmark\\furmark.exe"`) → `"id": "furmark",`
- шаг 3DMark (`"exe": "tools\\3dmark\\3DMarkCmd.exe"`) → `"id": "3dmark",`

Пример для OCCT:

```json
    { "type": "app", "id": "occt", "name": "OCCT — комбинированный + Power (расписание, HTML-отчёт)",
```

- [ ] **Step 5: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~TestSuiteTests`
Expected: PASS.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Agent/TestStep.cs src/SzDiag.Agent/testsuite.json tests/SzDiag.Agent.Tests/TestSuiteTests.cs
git commit -m "feat(agent): Id у шага + id для app-шагов testsuite"
```

---

### Task 8: `ReportActivityAsync` в `IHubLink` (аддитивно)

**Files:**
- Modify: `src/SzDiag.Agent/IHubLink.cs`
- Modify: `src/SzDiag.Agent/SignalRHubLink.cs`
- Modify: `tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs` (фейк `CapturingLink`)
- Modify: `tests/SzDiag.Agent.Tests/AgentSessionTests.cs` (фейк `FakeHubLink`)

> Чисто аддитивный шаг: новый метод интерфейса + реализация в боевом линке и двух фейках. Поведение не меняется — отдельного теста нет, критерий — зелёная сборка/тесты.

- [ ] **Step 1: Добавить метод в интерфейс**

В `src/SzDiag.Agent/IHubLink.cs` после строки `Task UploadReportFileAsync(...)` добавить:

```csharp

    /// <summary>Агент -> hub: текущая активность (метка + время старта; since=null — простой).</summary>
    Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default);
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet build`
Expected: FAIL — `SignalRHubLink`, `CapturingLink`, `FakeHubLink` не реализуют новый метод.

- [ ] **Step 3: Реализовать в `SignalRHubLink`**

В `src/SzDiag.Agent/SignalRHubLink.cs` после метода `UploadReportFileAsync` добавить:

```csharp
    public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
        => _conn.SendAsync(HubRoutes.ReportActivity, sz, activity, since, ct);
```

- [ ] **Step 4: Реализовать в фейке `CapturingLink`**

В `tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs` в класс `CapturingLink` добавить (после `UploadReportFileAsync`):

```csharp
        public List<(string sz, string activity, DateTimeOffset? since)> Activities { get; } = new();
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
        {
            Activities.Add((sz, activity, since));
            return Task.CompletedTask;
        }
```

- [ ] **Step 5: Реализовать в фейке `FakeHubLink`**

В `tests/SzDiag.Agent.Tests/AgentSessionTests.cs` в класс `FakeHubLink` добавить (после `UploadReportFileAsync`):

```csharp
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
            => Task.CompletedTask;
```

- [ ] **Step 6: Собрать и прогнать тесты — зелёные**

Run: `dotnet test tests/SzDiag.Agent.Tests`
Expected: PASS (поведение прежнее, просто компилируется).

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Agent/IHubLink.cs src/SzDiag.Agent/SignalRHubLink.cs tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs tests/SzDiag.Agent.Tests/AgentSessionTests.cs
git commit -m "feat(agent): IHubLink.ReportActivityAsync"
```

---

### Task 9: Фильтрация и исход прогона в `TestReportRunner`

**Files:**
- Modify: `src/SzDiag.Agent/TestReportRunner.cs`
- Test: `tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs`

- [ ] **Step 1: Написать падающие тесты**

В `TestReportRunnerTests.cs` добавить хелпер и тесты (перед закрывающей `}` класса):

```csharp
    private static TestStep App(string id) => new("app", id.ToUpperInvariant(), Id: id, Exe: "x");

    [Fact]
    public void FilterSteps_NullFilter_ReturnsAll()
    {
        var steps = new[] { App("occt"), App("tm5") };
        Assert.Equal(2, TestReportRunner.FilterSteps(steps, null).Count);
    }

    [Fact]
    public void FilterSteps_ById_CaseInsensitive()
    {
        var steps = new[] { App("occt"), App("tm5") };
        Assert.Equal("occt", Assert.Single(TestReportRunner.FilterSteps(steps, "OCCT")).Id);
    }

    [Fact]
    public void FilterSteps_Multiple_ReturnsSubset()
    {
        var steps = new[] { App("occt"), App("tm5"), App("furmark") };
        Assert.Equal(2, TestReportRunner.FilterSteps(steps, "tm5,furmark").Count);
    }

    [Fact]
    public void FilterSteps_UnknownId_Empty()
        => Assert.Empty(TestReportRunner.FilterSteps(new[] { App("occt") }, "nope"));

    [Fact]
    public void AvailableIds_ReturnsOnlyNonEmpty()
    {
        var steps = new TestStep[] { new("command", "Система", "systeminfo"), App("occt") };
        Assert.Equal(new[] { "occt" }, TestReportRunner.AvailableIds(steps));
    }

    [Fact]
    public async Task RunAndUpload_UnknownFilter_DoesNotRunOrUpload()
    {
        var suite = new TestSuite { Steps = new[] { App("occt") } };
        var link = new CapturingLink();
        var reportRunner = new TestReportRunner(
            new TestRunner(new FakeExecutor(), new FakeCapturer()), suite, link, "PC-1",
            () => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

        var outcome = await reportRunner.RunAndUploadAsync("156864", "nope");

        Assert.False(outcome.Ran);
        Assert.Empty(link.Uploaded);
        Assert.Contains("occt", outcome.AvailableIds);
    }
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~TestReportRunnerTests`
Expected: ошибка компиляции — нет `FilterSteps`/`AvailableIds`, у `RunAndUploadAsync` нет `filter`/исхода.

- [ ] **Step 3: Переписать `TestReportRunner`**

Заменить `src/SzDiag.Agent/TestReportRunner.cs` целиком на:

```csharp
using System.Text;
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Исход прогона для агента: гоняли ли что-то, всё ли чисто, короткая метка и доступные id.</summary>
public sealed record TestRunOutcome(bool Ran, bool AllClean, string RanLabel, IReadOnlyList<string> AvailableIds);

/// <summary>Оркестрация: фильтр шагов → прогон → report.md → загрузка файлов на hub + пуш активности.</summary>
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

    /// <summary>Отобрать шаги по фильтру (список id через запятую). Пусто/null — весь набор.</summary>
    public static IReadOnlyList<TestStep> FilterSteps(IReadOnlyList<TestStep> steps, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return steps;
        var ids = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.ToLowerInvariant()).ToHashSet();
        return steps.Where(s => s.Id is not null && ids.Contains(s.Id.ToLowerInvariant())).ToList();
    }

    /// <summary>Id всех шагов, у которых он задан (для подсказки при опечатке в фильтре).</summary>
    public static IReadOnlyList<string> AvailableIds(IReadOnlyList<TestStep> steps)
        => steps.Where(s => !string.IsNullOrWhiteSpace(s.Id)).Select(s => s.Id!).ToList();

    public async Task<TestRunOutcome> RunAndUploadAsync(string sz, string? filter = null, CancellationToken ct = default)
    {
        var steps = FilterSteps(_suite.Steps, filter);
        if (steps.Count == 0)
            return new TestRunOutcome(false, true, "", AvailableIds(_suite.Steps));

        var now = _now();
        var timestamp = now.ToString("yyyyMMdd-HHmmss");
        var runSuite = new TestSuite { Steps = steps };

        // Пуш активности на старте каждого шага; время старта — реальное UtcNow.
        void OnStep(TestStep s)
        {
            var label = s.Type.Equals("app", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(s.Id)
                ? $"Тест {s.Id!.ToUpperInvariant()}"
                : s.Name;
            try { _ = _link.ReportActivityAsync(sz, label, DateTimeOffset.UtcNow, ct); }
            catch { /* статус не критичен */ }
        }

        // Стресс-тесты держат нагрузку минутами — уводим с потока обработчика SignalR.
        var output = await Task.Run(() => _runner.Run(runSuite, sz, _hostname, now, OnStep), ct);

        var md = ReportMarkdownBuilder.Build(output.Report);
        await _link.UploadReportFileAsync(
            new UploadReportPart(sz, timestamp, "report.md", Encoding.UTF8.GetBytes(md)), ct);

        foreach (var (fileName, bytes) in output.Screenshots)
            await _link.UploadReportFileAsync(new UploadReportPart(sz, timestamp, fileName, bytes), ct);

        foreach (var (fileName, bytes) in output.Artifacts)
            await _link.UploadReportFileAsync(new UploadReportPart(sz, timestamp, fileName, bytes), ct);

        var allClean = output.Report.Steps.All(s =>
            s.Error is null && (s.Output is null || !s.Output.Contains('⚠')));
        var ranLabel = string.IsNullOrWhiteSpace(filter)
            ? "полный прогон"
            : string.Join(", ", AvailableIds(steps).Select(i => i.ToUpperInvariant()));
        if (string.IsNullOrEmpty(ranLabel)) ranLabel = "прогон";

        return new TestRunOutcome(true, allClean, ranLabel, AvailableIds(_suite.Steps));
    }
}
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~TestReportRunnerTests`
Expected: PASS (новые тесты + существующий `RunAndUpload_UploadsReportMd...`, который вызывает `RunAndUploadAsync("156864")` без фильтра — исход игнорируется, загрузки прежние).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Agent/TestReportRunner.cs tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs
git commit -m "feat(agent): фильтр шагов + исход прогона + пуш активности"
```

---

### Task 10: Финальная разводка агента (OnRunTests с фильтром + пуш активности в Program)

**Files:**
- Modify: `src/SzDiag.Agent/IHubLink.cs`
- Modify: `src/SzDiag.Agent/SignalRHubLink.cs`
- Modify: `tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs` (фейк `CapturingLink`)
- Modify: `tests/SzDiag.Agent.Tests/AgentSessionTests.cs` (фейк `FakeHubLink`)
- Modify: `src/SzDiag.Agent/Program.cs`

> Меняет сигнатуру `OnRunTests` (агенту нужен `filter`) во всех реализациях и добавляет
> в `Program.cs` разбор фильтра + idle/итоговую активность. Program не покрыт юнит-тестами;
> критерий — зелёная сборка/тесты и e2e из docs/TESTING.md.

- [ ] **Step 1: Сменить сигнатуру `OnRunTests` в интерфейсе**

В `src/SzDiag.Agent/IHubLink.cs` заменить строку `void OnRunTests(Func<string, Task> handler);` на:

```csharp
    /// <summary>Подписка на команду прогона тестов от hub (sz, filter → callback).</summary>
    void OnRunTests(Func<string, string?, Task> handler);
```

- [ ] **Step 2: Обновить `SignalRHubLink`**

В `src/SzDiag.Agent/SignalRHubLink.cs` заменить метод `OnRunTests` на:

```csharp
    public void OnRunTests(Func<string, string?, Task> handler)
        => _conn.On<string, string?>(HubRoutes.RunTests, (sz, filter) => handler(sz, filter));
```

- [ ] **Step 3: Обновить фейк `CapturingLink`**

В `tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs` заменить строку
`public void OnRunTests(Func<string, Task> handler) { }` на:

```csharp
        public void OnRunTests(Func<string, string?, Task> handler) { }
```

- [ ] **Step 4: Обновить фейк `FakeHubLink`**

В `tests/SzDiag.Agent.Tests/AgentSessionTests.cs`:
- заменить `private Func<string, Task>? _onRunTests;` на `private Func<string, string?, Task>? _onRunTests;`
- заменить `public void OnRunTests(Func<string, Task> handler) => _onRunTests = handler;` на
  `public void OnRunTests(Func<string, string?, Task> handler) => _onRunTests = handler;`
- заменить `public Task FireRunTests(string sz) => _onRunTests!(sz);` на
  `public Task FireRunTests(string sz, string? filter = null) => _onRunTests!(sz, filter);`

- [ ] **Step 5: Разводка в `Program.cs` (агент)**

В `src/SzDiag.Agent/Program.cs` после строки с `await session.StartAsync();` и следующим за ней `Announce($"СЗ {sz}: доступ открыт ● online...` добавить пуш стартовой активности:

```csharp
try { await link.ReportActivityAsync(sz, "— готов", null); } catch { /* статус не критичен */ }
```

Затем заменить весь блок `link.OnRunTests(async runSz => { ... });` (внутри `if (File.Exists(suitePath)) {...}`) на:

```csharp
    link.OnRunTests(async (runSz, filter) =>
    {
        var scope = string.IsNullOrWhiteSpace(filter) ? "полный прогон" : $"фильтр {filter}";
        Announce($"Прогон тестов для СЗ {runSz} ({scope})…", $"[grey]Прогон тестов для СЗ {runSz} ({scope})…[/]");
        try
        {
            var outcome = await reportRunner.RunAndUploadAsync(runSz, filter);
            if (!outcome.Ran)
            {
                var ids = string.Join(", ", outcome.AvailableIds);
                Announce($"Не найдено шагов по фильтру '{filter}'. Доступные: {ids}",
                    $"[yellow]Не найдено шагов по фильтру '{Markup.Escape(filter ?? "")}'.[/] Доступные: {Markup.Escape(ids)}");
                await link.ReportActivityAsync(runSz, "— готов", null);
            }
            else
            {
                Announce("Отчёт залит на hub.", "[green]Отчёт залит на hub.[/]");
                var mark = outcome.AllClean ? "✓" : "⚠";
                await link.ReportActivityAsync(runSz, $"готов · последний: {outcome.RanLabel} {mark}", null);
            }
        }
        catch (Exception ex)
        {
            Announce($"Ошибка прогона: {ex.Message}", $"[red]Ошибка прогона:[/] {Markup.Escape(ex.Message)}");
            try { await link.ReportActivityAsync(runSz, "готов · последний: ошибка ⚠", null); } catch { }
        }
    });
```

- [ ] **Step 6: Собрать и прогнать все тесты — зелёные**

Run: `dotnet test`
Expected: PASS (весь солюшен).

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Agent/IHubLink.cs src/SzDiag.Agent/SignalRHubLink.cs src/SzDiag.Agent/Program.cs tests/SzDiag.Agent.Tests/TestReportRunnerTests.cs tests/SzDiag.Agent.Tests/AgentSessionTests.cs
git commit -m "feat(agent): OnRunTests с фильтром + пуш активности (старт/idle/итог)"
```

---

### Task 11: Документация

**Files:**
- Modify: `docs/TESTING.md`

- [ ] **Step 1: Обновить раздел про запуск тестов**

В `docs/TESTING.md` в разделе «## 3. Хост: диагностика через CLI» после строки
`.\szcli test run 156864     # ...` добавить:

```markdown
.\szcli test run 156864 occt        # только OCCT
.\szcli test run 156864 tm5,furmark # подмножество шагов (id через запятую)
```

И в описании таблицы `list`/`watch` (п.3) добавить строку про новый столбец:

```markdown
Столбец «Активность» показывает, что идёт на машине: `Тест OCCT 5мин 44сек`
(время тикает), `готов · последний: TM5 ✓` в простое, `—` для offline.
```

- [ ] **Step 2: Коммит**

```bash
git add docs/TESTING.md
git commit -m "docs: точечный запуск тестов + столбик активности в TESTING.md"
```

---

## Итоговая проверка (после всех задач)

- [ ] `dotnet build` — без ошибок/варнингов по изменённым файлам.
- [ ] `dotnet test` — все тесты зелёные (было ~76, стало ~76 + новые).
- [ ] Ручная проверка (по желанию, из `docs/TESTING.md`): `szcli test run <СЗ> occt` гоняет только OCCT; `szcli watch` показывает `Тест OCCT <время>` во время прогона и `готов · последний: OCCT ✓` после.

## Обратная совместимость (для ревью)

- `szcli test run <СЗ>` без фильтра = весь набор (старое поведение).
- Новые поля `SessionInfo` (`Activity`, `ActivitySince`) имеют дефолты — существующие конструкторы/JSON не ломаются.
- `Id` у `TestStep` — опциональный, добавлен в конец записи (позиционные вызовы не смещаются).
- `RunTests` со вторым аргументом: агент и hub деплоятся одним билдом (`build-dist.ps1`), рассинхрона версий в проде нет.
