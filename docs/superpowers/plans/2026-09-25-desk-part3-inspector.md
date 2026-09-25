# SzDiag Desk, часть 3 — инспектор СЗ, действия, статус hub — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Правая панель Desk становится рабочим инспектором выбранной СЗ: вкладки «Обзор / Вырубоны / Задачи / Сенсоры / Журнал / Железо / Действия». Действия — `diag run`, `test run --config`, `freeze`/`unfreeze`, заметка, `sz fetch`, `close`. На карточке СЗ — бейдж `🧊` (Windows Update заморожен). В статусбаре — версия пакета агента, последний бэкап kb и состояние туннеля (`GET /api/status`).

**Architecture:** Hub получает `HubStatusTracker`: в него отчитываются `KbBackupService` и `HubTunnelService`, наружу он отдаётся через `GET /api/status`. Desk: `InspectorViewModel` держит набор вкладок (`IInspectorTab`) и опрашивает **только видимую** — по интервалу вкладки, раз в секунду из окна. Данные вкладок берутся из `/api` (вырубоны, задачи, сенсоры через exec), из kb с диска (журнал) и из `szcli`, запущенного подпроцессом (паспорт железа, все действия). Бейдж `🧊` — тот же признак, что у `szcli list`: файл прежних значений `cli\freeze\<СЗ>.json` рядом с `szcli`.

**Tech Stack:** .NET 8, ASP.NET Core minimal API (hub), Avalonia 11.3.22, CommunityToolkit.Mvvm 8.4.2, xunit 2.5.3, Avalonia.Headless.XUnit 11.3.22.

**Spec:** [docs/superpowers/specs/2026-09-25-desk-gui-design.md](../specs/2026-09-25-desk-gui-design.md) — разделы «Источники данных GUI», «Действия инспектора», «Статусбар». Части 1–2 уже в ветке: [часть 1](2026-09-25-desk-part1-window.md), [часть 2](2026-09-25-desk-part2-sessions.md).

**Что покрывает этот план:** этап 5 спеки. Этап 6 (`ask_peer`/`peers`, бейдж `≈` «похожая СЗ» по паспорту железа) — **часть 4**, отдельным планом: там свой протокол между сессиями и свои лимиты, и проверять его имеет смысл на двух живых СЗ.

**Решения плана поверх спеки** (задача 12 вносит их в спеку):
- **Действия и паспорт железа — подпроцессом `szcli`**, а не прямыми вызовами `IHubApiClient`, как написано в спеке. Причина: половина логики живёт только в CLI. `freeze` хранит прежние значения в `cli\freeze\<СЗ>.json` рядом с `szcli`, и `unfreeze` из терминала должен их найти. У `close` есть защиты (мало наблюдения, остатки на клиенте, `--force "причина"`). У `sz fetch` — своя сессия захвата. `hw passport` — это скрипт из CLI. Повторять это в Desk значит получить две разные реализации одной команды. Вывод `szcli` показывается как есть, журнал СЗ ведёт hub.
- **Сенсоры — синхронный exec хвоста CSV `lhmmon`**, раз в 5 с и только пока вкладка открыта. Под полной нагрузкой exec может не ответить. Тогда вкладка остаётся на прошлых данных и прямо пишет, что агент не ответил. Как и в CLI, это не считается признаком вырубона.
- **«Сенсоры не пишутся»** определяется так: последняя строка CSV не менялась 30 с по часам **бокса**. Время в строках идёт по часам **клиента**, а в WinPE они смещены на пояс (бэклог п.287).

## Global Constraints

- Целевой фреймворк — **net8.0**; файлы `*.csproj`/`*.ps1` — **UTF-8 с BOM**.
- Комментарии и пользовательский текст — **на русском**, комментарий объясняет «почему».
- Имена маршрутов — только из `SzDiag.Contracts` (`HubStatusRoutes.Status`); строки на концах не хардкодить.
- Признаки живости — только `LastHeartbeat`/`BootTime`; молчание exec под нагрузкой — **не** признак вырубона.
- Разрушительные действия (`close`, `close --force`, `unfreeze`) — только после явного подтверждения в окне.
- Опрос вкладок инспектора — **только видимой**: вырубоны — при открытии и при смене `⚡N`; задачи — 3 с; сенсоры — 5 с; журнал — по изменению файла; железо — при открытии и кнопкой.
- Тема — токены части 1 (`Tokens.axaml`); новые цвета не заводить.
- Рецепты и правки со слешами (`C:\OCCT\…`, `\\.\`) — писать инструментом Write/Edit, **не** bash-heredoc (CLAUDE.md, «Рецепты»): heredoc в Git Bash схлопывает `\\`.
- `dotnet` из PATH бокса — только рантайм 10: тесты гонять через `"C:\Program Files\dotnet\dotnet.exe"`.

## Review Focus

- **Под полной нагрузкой exec не отвечает** — вкладки «Задачи» и «Сенсоры» должны остаться на прошлых данных и сказать «агент не ответил», а не очиститься и не повесить окно. → `JobsTabViewModelTests.Timeout_KeepsRowsAndSaysSo` (задача 7), `SensorsTabViewModelTests.Timeout_KeepsValuesAndSaysSo` (задача 8).
- **Скрытая вкладка не опрашивается** — открытый инспектор на «Обзоре» не должен каждые 3–5 с дёргать exec у клиента под нагрузкой. → `InspectorViewModelTests.HiddenTab_NeverPolled` (задача 5).
- **`close`/`unfreeze` случайным кликом** — без подтверждения команда не уходит. → `ActionsViewModelTests.Close_RunsOnlyAfterConfirm`, `Unfreeze_RunsOnlyAfterConfirm` (задача 10).
- **`dist` не собран — `szcli` не найден** — «Железо» и «Действия» должны объяснить, что делать, а не падать. → `SzcliRunnerTests.NoExe_ExplainsBuildDist` (задача 4).
- **Старый hub без `/api/status`** — статусбар работает как раньше, без деталей и без красного. → `HubApiClientStatusTests.OldHub_Null` (задача 2), `StatusBarViewModelTests.Details_NoStatus_Hidden` (задача 3).

---

## Карта файлов

**Новое:**
- `src/SzDiag.Contracts/HubStatus.cs`, `src/SzDiag.Contracts/SensorPaths.cs`
- `src/SzDiag.Hub/HubStatusTracker.cs`
- `tests/SzDiag.Hub.Tests/HubStatusTests.cs`
- `tests/SzDiag.HubClient.Tests/HubApiClientStatusTests.cs`
- `src/SzDiag.Desk/Services/SzcliRunner.cs`, `FreezeProbe.cs`, `DeskTools.cs`
- `src/SzDiag.Desk/ViewModels/Inspector/` — `IInspectorTab.cs`, `InspectorViewModel.cs`, `RebootsTabViewModel.cs`, `JobsTabViewModel.cs`, `SensorsTabViewModel.cs`, `Sparkline.cs`, `JournalTabViewModel.cs`, `HardwareTabViewModel.cs`, `ActionsViewModel.cs`
- `src/SzDiag.Desk/Views/Inspector/` — `RebootsTabView.axaml(.cs)`, `JobsTabView.axaml(.cs)`, `SensorsTabView.axaml(.cs)`, `JournalTabView.axaml(.cs)`, `HardwareTabView.axaml(.cs)`, `ActionsView.axaml(.cs)`
- `tests/SzDiag.Desk.Tests/` — `ManualClock.cs`, `FakeInspectorTab.cs`, `FakeSzcliRunner.cs`, `SzcliRunnerTests.cs`, `InspectorViewModelTests.cs`, `MainViewModelInspectorTests.cs`, `RebootsTabViewModelTests.cs`, `JobsTabViewModelTests.cs`, `SensorsTabViewModelTests.cs`, `JournalTabViewModelTests.cs`, `HardwareTabViewModelTests.cs`, `ActionsViewModelTests.cs`, `InspectorSmokeTests.cs`
- `docs/live-checklist-2026-09-25-desk-part3.md`

**Перенос:** `src/SzDiag.Cli/CliXml.cs` → `src/SzDiag.HubClient/CliXml.cs` (Desk декодирует им вывод задач, как CLI).

**Изменения:** `src/SzDiag.Hub/{KbBackupService,HubTunnelService,ManagementApi,Program}.cs`, `src/SzDiag.HubClient/{IHubApiClient,HubApiClient}.cs`, `src/SzDiag.Cli/SensorsCommand.cs`, `tests/SzDiag.Cli.Tests/CliXmlTests.cs`, `tests/SzDiag.Hub.Tests/{KbBackupServiceTests,HubTunnelServiceTests}.cs`, `src/SzDiag.Desk/Services/{HubSnapshot,HubPoller}.cs`, `src/SzDiag.Desk/ViewModels/{StatusBarViewModel,MainViewModel,SzItemViewModel}.cs`, `src/SzDiag.Desk/Views/{InspectorView.axaml,SzListView.axaml,StatusBarView.axaml,MainWindow.axaml.cs}`, `src/SzDiag.Desk/App.axaml.cs`, `tests/SzDiag.Desk.Tests/{FakeHubApi,StatusBarViewModelTests}.cs`, `CLAUDE.md`, `docs/dev-knowledge-base.md`, спека.

---

### Task 1: Hub — `HubStatusTracker` и `GET /api/status`

**Files:**
- Create: `src/SzDiag.Contracts/HubStatus.cs`, `src/SzDiag.Hub/HubStatusTracker.cs`, `tests/SzDiag.Hub.Tests/HubStatusTests.cs`
- Modify: `src/SzDiag.Hub/KbBackupService.cs`, `src/SzDiag.Hub/HubTunnelService.cs`, `src/SzDiag.Hub/ManagementApi.cs`, `src/SzDiag.Hub/Program.cs`

**Interfaces:**
- Produces (Contracts): `record HubStatus(string? AgentPackageVersion, KbBackupStatus KbBackup, TunnelStatus Tunnel)`, `record KbBackupStatus(bool Enabled, DateTimeOffset? LastRunAt, string? Outcome, string? Message)`, `record TunnelStatus(string State, DateTimeOffset? Since)`, `static class TunnelStates { Off, NotFound, Running, Restarting }` (строки `off`, `not-found`, `running`, `restarting`), `static class HubStatusRoutes { const string Status = "/api/status"; }`.
- Produces (Hub): `HubStatusTracker(TimeProvider time)` — `void KbBackupEnabled(bool enabled)`, `void KbBackupRan(KbBackupResult r)`, `void KbBackupCrashed(string message)`, `void Tunnel(string state)`, `HubStatus Snapshot(string? agentPackageVersion)`. У `KbBackupService` и `HubTunnelService` — необязательный параметр конструктора `HubStatusTracker? status = null`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Hub.Tests/HubStatusTests.cs`:
```csharp
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Hub;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Hub.Tests;

public class HubStatusTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szstatus-{Guid.NewGuid():N}");

    public HubStatusTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_dir, "agent-dist"));
        File.WriteAllText(Path.Combine(_dir, "agent-dist", "version.txt"), "76a6189\n");
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={Path.Combine(_dir, "hub.db")}")
             .UseSetting("Hub:KnowledgeBaseRoot", Path.Combine(_dir, "kb"))
             .UseSetting("Hub:AgentDistRoot", Path.Combine(_dir, "agent-dist"))
             .UseSetting("Hub:KbBackup:Enabled", "false")
             .WithoutSystemLogging());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Status_ReportsAgentPackage_KbBackupOff_TunnelOff()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");

        var s = (await c.GetFromJsonAsync<HubStatus>(HubStatusRoutes.Status))!;

        Assert.Equal("76a6189", s.AgentPackageVersion);
        Assert.False(s.KbBackup.Enabled);
        Assert.Equal(TunnelStates.Off, s.Tunnel.State);
    }

    [Fact]
    public async Task Status_NoToken_Unauthorized()
        => Assert.Equal(System.Net.HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(HubStatusRoutes.Status)).StatusCode);

    [Fact]
    public void Tracker_KbBackupOutcomeAndTunnelState()
    {
        var time = new ManualTime();
        var t = new HubStatusTracker(time);
        t.KbBackupEnabled(true);
        t.KbBackupRan(new KbBackupResult(KbBackupOutcome.Pushed, 3, "выгружено"));
        t.Tunnel(TunnelStates.Running);

        var s = t.Snapshot(null);
        Assert.True(s.KbBackup.Enabled);
        Assert.Equal("Pushed", s.KbBackup.Outcome);
        Assert.Equal(time.Now, s.KbBackup.LastRunAt);
        Assert.Equal(TunnelStates.Running, s.Tunnel.State);
        Assert.Equal(time.Now, s.Tunnel.Since);

        t.KbBackupCrashed("git сломался");
        Assert.Equal("Failed", t.Snapshot(null).KbBackup.Outcome);
        Assert.Equal("git сломался", t.Snapshot(null).KbBackup.Message);
    }

    [Fact]
    public async Task KbBackupService_ReportsEachRun()
    {
        var tracker = new HubStatusTracker(TimeProvider.System);
        var opts = new HubOptions { KbBackup = new KbBackupOptions { Enabled = true, Interval = TimeSpan.FromHours(1) } };
        var service = new KbBackupService(new OkBackup(), Options.Create(opts), NullLogger<KbBackupService>.Instance, tracker);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        var kb = tracker.Snapshot(null).KbBackup;
        Assert.True(kb.Enabled);
        Assert.Equal("NoChanges", kb.Outcome);
        Assert.NotNull(kb.LastRunAt);
    }

    [Fact]
    public async Task TunnelService_MissingCloudflared_ReportsNotFound()
    {
        var tracker = new HubStatusTracker(TimeProvider.System);
        var service = new HubTunnelService(
            new HubTunnelOptions { Enabled = true, ExecutablePath = Path.Combine(_dir, "нет-cloudflared.exe") },
            NullLogger<HubTunnelService>.Instance, _dir, tracker);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(TunnelStates.NotFound, tracker.Snapshot(null).Tunnel.State);
    }

    private sealed class OkBackup : IKbBackup
    {
        public Task<KbBackupResult> RunAsync(CancellationToken ct)
            => Task.FromResult(new KbBackupResult(KbBackupOutcome.NoChanges, 0, "изменений нет"));
    }
}
```
(`ManualTime` — уже есть в `tests/SzDiag.Hub.Tests/ManualTime.cs` из части 1.)

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~HubStatus`
Expected: ошибка компиляции — `HubStatus`, `HubStatusTracker` не найдены.

- [ ] **Step 3: Контракт**

`src/SzDiag.Contracts/HubStatus.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>`GET /api/status` — то, что статусбар Desk показывает помимо /healthz: какой пакет
/// агента раздаёт hub, когда последний раз ушёл бэкап kb, жив ли туннель (спека 2026-09-25).
/// Раньше всё это видно было только в консоли hub.</summary>
/// <param name="AgentPackageVersion">Содержимое agent-dist/version.txt; null — пакета нет.</param>
public sealed record HubStatus(string? AgentPackageVersion, KbBackupStatus KbBackup, TunnelStatus Tunnel);

/// <param name="Outcome">Имя исхода последнего прогона (`NoChanges`, `Pushed`, `CommittedNotPushed`,
/// `Failed`) строкой — Contracts не ссылается на SzDiag.Kb; null — прогона ещё не было.</param>
public sealed record KbBackupStatus(bool Enabled, DateTimeOffset? LastRunAt, string? Outcome, string? Message);

/// <param name="State">Одно из <see cref="TunnelStates"/>.</param>
/// <param name="Since">С какого момента туннель в этом состоянии.</param>
public sealed record TunnelStatus(string State, DateTimeOffset? Since);

public static class TunnelStates
{
    public const string Off = "off";
    public const string NotFound = "not-found";
    public const string Running = "running";
    public const string Restarting = "restarting";
}

public static class HubStatusRoutes
{
    public const string Status = "/api/status";
}
```

- [ ] **Step 4: Трекер и отчёты сервисов**

`src/SzDiag.Hub/HubStatusTracker.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Kb;

namespace SzDiag.Hub;

/// <summary>Живое состояние фоновых служб hub для `GET /api/status`: бэкап kb и туннель сами
/// отчитываются сюда, эндпоинт только читает. In-memory: после рестарта hub «прогона ещё не было».</summary>
public sealed class HubStatusTracker(TimeProvider time)
{
    private readonly object _gate = new();
    private KbBackupStatus _kb = new(false, null, null, null);
    private TunnelStatus _tunnel = new(TunnelStates.Off, null);

    public void KbBackupEnabled(bool enabled)
    {
        lock (_gate) _kb = _kb with { Enabled = enabled };
    }

    public void KbBackupRan(KbBackupResult r)
    {
        lock (_gate) _kb = _kb with { LastRunAt = time.GetUtcNow(), Outcome = r.Outcome.ToString(), Message = r.Message };
    }

    public void KbBackupCrashed(string message)
    {
        lock (_gate)
            _kb = _kb with { LastRunAt = time.GetUtcNow(), Outcome = nameof(KbBackupOutcome.Failed), Message = message };
    }

    public void Tunnel(string state)
    {
        lock (_gate) _tunnel = new TunnelStatus(state, time.GetUtcNow());
    }

    public HubStatus Snapshot(string? agentPackageVersion)
    {
        lock (_gate) return new HubStatus(agentPackageVersion, _kb, _tunnel);
    }
}
```
`src/SzDiag.Hub/KbBackupService.cs`:
- поле `private readonly HubStatusTracker? _status;`;
- конструктор `public KbBackupService(IKbBackup backup, IOptions<HubOptions> options, ILogger<KbBackupService> logger, HubStatusTracker? status = null)` + `_status = status;`;
- в `ExecuteAsync` первой строкой `_status?.KbBackupEnabled(_options.Enabled);`;
- в `RunSafeAsync` сразу после `var result = await _backup.RunAsync(ct);` — `_status?.KbBackupRan(result);`;
- в `catch (Exception ex)` первой строкой — `_status?.KbBackupCrashed(ex.Message);`.

`src/SzDiag.Hub/HubTunnelService.cs`:
- поле `private readonly HubStatusTracker? _status;`;
- конструкторы:
```csharp
    public HubTunnelService(IOptions<HubOptions> options, ILogger<HubTunnelService> logger,
        HubStatusTracker? status = null)
        : this(options.Value.Tunnel, logger, AppContext.BaseDirectory, status)
    {
    }

    public HubTunnelService(HubTunnelOptions options, ILogger<HubTunnelService> logger, string baseDir,
        HubStatusTracker? status = null)
    {
        _options = options;
        _logger = logger;
        _baseDir = baseDir;
        _status = status;
    }
```
- в `ExecuteAsync`: `if (!_options.Enabled) { _status?.Tunnel(TunnelStates.Off); return; }`; в ветке `exe is null` перед `return` — `_status?.Tunnel(TunnelStates.NotFound);`; перед `_logger.LogWarning("туннель: cloudflared завершился …` — `_status?.Tunnel(TunnelStates.Restarting);`;
- в `RunOnceAsync` после `WritePid(process.Id);` — `_status?.Tunnel(TunnelStates.Running);`;
- `using SzDiag.Contracts;`, если его нет.

- [ ] **Step 5: Эндпоинт и DI**

`src/SzDiag.Hub/ManagementApi.cs` — рядом с `/transfers`:
```csharp
        // Детали статусбара Desk (спека 2026-09-25): пакет агента, бэкап kb, туннель.
        group.MapGet("/status", (HubStatusTracker status, IOptions<HubOptions> options) =>
        {
            var path = Path.Combine(options.Value.AgentDistRoot, "version.txt");
            var version = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            return Results.Ok(status.Snapshot(string.IsNullOrEmpty(version) ? null : version));
        });
```
(`using Microsoft.Extensions.Options;` — если нет; путь группы `/api`, итог совпадает с `HubStatusRoutes.Status`.)

`src/SzDiag.Hub/Program.cs` — перед `AddHostedService<KbBackupService>()`:
```csharp
builder.Services.AddSingleton(new HubStatusTracker(TimeProvider.System));
```

- [ ] **Step 6: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Hub.Tests`
Expected: всё зелёное, включая 5 новых. Старые `KbBackupServiceTests`/`HubTunnelServiceTests` компилируются без правок (параметр трекера необязательный).

- [ ] **Step 7: Commit**

```bash
git add src/SzDiag.Contracts/HubStatus.cs src/SzDiag.Hub tests/SzDiag.Hub.Tests/HubStatusTests.cs
git commit -m "feat(hub): /api/status — пакет агента, бэкап kb, туннель"
```

---

### Task 2: HubClient — статус, `CliXml`, путь CSV сенсоров

**Files:**
- Create: `src/SzDiag.Contracts/SensorPaths.cs`, `tests/SzDiag.HubClient.Tests/HubApiClientStatusTests.cs`
- Move: `src/SzDiag.Cli/CliXml.cs` → `src/SzDiag.HubClient/CliXml.cs`
- Modify: `src/SzDiag.HubClient/IHubApiClient.cs`, `HubApiClient.cs`, `src/SzDiag.Cli/SensorsCommand.cs`, `tests/SzDiag.Cli.Tests/CliXmlTests.cs`, `tests/SzDiag.Desk.Tests/FakeHubApi.cs`

**Interfaces:**
- Consumes: `HubStatus`, `HubStatusRoutes` (задача 1).
- Produces: `Task<HubStatus?> IHubApiClient.GetStatusAsync(CancellationToken ct = default)` (404 старого hub → null); `SzDiag.HubClient.CliXml` (`Looks`, `Decode`); `SensorPaths.LhmCsv = @"C:\OCCT\sensors.csv"`; в `FakeHubApi` — настраиваемые `Status`, `Reboots`, `Jobs`, `JobStatus`, `Exec`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.HubClient.Tests/HubApiClientStatusTests.cs`:
```csharp
using System.Net;
using System.Text;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.HubClient.Tests;

public class HubApiClientStatusTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static HubApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new Stub(respond)) { BaseAddress = new Uri("http://hub") }, "mgmt");

    [Fact]
    public async Task Parses()
    {
        var c = Client(r =>
        {
            Assert.Equal("/api/status", r.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"agentPackageVersion":"76a6189","kbBackup":{"enabled":true,"lastRunAt":"2026-09-25T12:00:00Z","outcome":"Pushed","message":"ok"},"tunnel":{"state":"running","since":null}}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        var s = (await c.GetStatusAsync())!;
        Assert.Equal("76a6189", s.AgentPackageVersion);
        Assert.Equal("Pushed", s.KbBackup.Outcome);
        Assert.Equal(TunnelStates.Running, s.Tunnel.State);
    }

    [Fact]
    public async Task OldHub_Null()
        => Assert.Null(await Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetStatusAsync());
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.HubClient.Tests --filter FullyQualifiedName~Status`
Expected: ошибка компиляции — `GetStatusAsync` нет.

- [ ] **Step 3: Реализация**

`src/SzDiag.HubClient/IHubApiClient.cs` — после `GetHealthAsync`:
```csharp
    /// <summary>`/api/status` — пакет агента, бэкап kb, туннель. null — старый hub без эндпоинта:
    /// статусбар тогда просто без деталей.</summary>
    Task<HubStatus?> GetStatusAsync(CancellationToken ct = default);
```
`src/SzDiag.HubClient/HubApiClient.cs` — после `GetHealthAsync`:
```csharp
    public async Task<HubStatus?> GetStatusAsync(CancellationToken ct = default)
    {
        using var cts = Short(ct);
        using var resp = await _http.GetAsync(HubStatusRoutes.Status, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<HubStatus>(cts.Token);
    }
```
`tests/SzDiag.Desk.Tests/FakeHubApi.cs` — добавить настраиваемые ответы и заменить соответствующие заглушки `No<…>()`:
```csharp
    public Func<HubStatus?> Status { get; set; } = () => null;
    public Func<string, RebootTimeline?> Reboots { get; set; } = _ => null;
    public Func<string, ExecJobStatus?> Jobs { get; set; } = _ => null;
    public Func<string, string, ExecJobStatus?> JobStatus { get; set; } = (_, _) => null;
    public Func<string, string, ExecResult?> Exec { get; set; } = (_, _) => null;

    public Task<HubStatus?> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(Status());
    public Task<RebootTimeline?> GetRebootsAsync(string sz, CancellationToken ct = default) => Task.FromResult(Reboots(sz));
    public Task<ExecJobStatus?> ExecJobsAsync(string sz, CancellationToken ct = default) => Task.FromResult(Jobs(sz));
    public Task<ExecJobStatus?> ExecStatusAsync(string sz, string jobId, int tailLines, CancellationToken ct = default)
        => Task.FromResult(JobStatus(sz, jobId));
    public Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null, CancellationToken ct = default,
        bool detached = false, bool isolated = false, bool asSystem = false) => Task.FromResult(Exec(sz, script));
```
(прежние строки `GetRebootsAsync`, `ExecJobsAsync`, `ExecStatusAsync`, `ExecAsync` с `No<…>()` удалить.)

Перенос `CliXml`:
```bash
git mv src/SzDiag.Cli/CliXml.cs src/SzDiag.HubClient/CliXml.cs
```
В перенесённом файле `namespace SzDiag.Cli;` → `namespace SzDiag.HubClient;`. CLI видит его через `GlobalUsings.cs` (часть 1). В `tests/SzDiag.Cli.Tests/CliXmlTests.cs` строку `using SzDiag.Cli;` заменить на `using SzDiag.HubClient;`.

`src/SzDiag.Contracts/SensorPaths.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>Где на клиенте лежит CSV внешнего наблюдателя `lhmmon`. Одно место на CLI
/// (`sensors`) и Desk (вкладка «Сенсоры») — иначе разъедутся при первой смене пути.</summary>
public static class SensorPaths
{
    public const string LhmCsv = @"C:\OCCT\sensors.csv";
}
```
В `src/SzDiag.Cli/SensorsCommand.cs` строку `private const string LhmCsvPath = @"C:\OCCT\sensors.csv";` заменить на `private const string LhmCsvPath = SensorPaths.LhmCsv;` (правка через Edit — в строке обратный слеш).

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" build` и `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.HubClient.Tests` и `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Cli.Tests --filter "FullyQualifiedName!~AllRepoClientRecipes_NoFalseConcatWarning"` и `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: сборка без ошибок, всё зелёное (`ScriptLintTests.AllRepoClientRecipes_NoFalseConcatWarning` падает и до ветки — исключён).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Contracts/SensorPaths.cs src/SzDiag.HubClient src/SzDiag.Cli/SensorsCommand.cs src/SzDiag.Cli/CliXml.cs tests/SzDiag.HubClient.Tests/HubApiClientStatusTests.cs tests/SzDiag.Cli.Tests/CliXmlTests.cs tests/SzDiag.Desk.Tests/FakeHubApi.cs
git commit -m "feat(hubclient): статус hub, CliXml и путь CSV сенсоров — общие для CLI и Desk"
```

---

### Task 3: Статусбар — детали из `/api/status`

**Files:**
- Modify: `src/SzDiag.Desk/Services/HubSnapshot.cs`, `src/SzDiag.Desk/Services/HubPoller.cs`, `src/SzDiag.Desk/ViewModels/StatusBarViewModel.cs`, `src/SzDiag.Desk/Views/StatusBarView.axaml`
- Test: `tests/SzDiag.Desk.Tests/StatusBarViewModelTests.cs`, `tests/SzDiag.Desk.Tests/HubPollerTests.cs`

**Interfaces:**
- Consumes: `IHubApiClient.GetStatusAsync` (задача 2).
- Produces: `HubSnapshot.Status` (`HubStatus?`, последний параметр записи со значением по умолчанию `null`); `StatusBarViewModel.DetailsText` (`string?`), `DetailsWarn` (`bool`), `static (string? Text, bool Warn) StatusBarViewModel.FormatDetails(HubStatus? s)`.

- [ ] **Step 1: Failing tests**

Дописать в `tests/SzDiag.Desk.Tests/StatusBarViewModelTests.cs`:
```csharp
    private static HubStatus St(KbBackupStatus kb, string tunnel = TunnelStates.Off, string? agent = "76a6189")
        => new(agent, kb, new TunnelStatus(tunnel, null));

    [Fact]
    public void Details_NoStatus_Hidden()
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(null);
        Assert.Null(text);
        Assert.False(warn);
    }

    [Fact]
    public void Details_AgentKbTunnel()
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(
            St(new KbBackupStatus(true, Now, "Pushed", "ok"), TunnelStates.Running));
        Assert.StartsWith("агент 76a6189 · kb ", text);
        Assert.Contains("✓", text);
        Assert.EndsWith("· туннель ✓", text);
        Assert.False(warn);
    }

    [Theory]
    [InlineData(false, null, "kb: бэкап выключен", false)]
    [InlineData(true, null, "kb: бэкапа ещё не было", false)]
    [InlineData(true, "CommittedNotPushed", "kb: не выгружен в remote", true)]
    [InlineData(true, "Failed", "kb: бэкап упал", true)]
    public void Details_KbStates(bool enabled, string? outcome, string expected, bool expectedWarn)
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(
            St(new KbBackupStatus(enabled, outcome is null ? null : Now, outcome, null), agent: null));
        Assert.Equal(expected, text);
        Assert.Equal(expectedWarn, warn);
    }

    [Fact]
    public void Details_TunnelDown_Warns()
    {
        var (text, warn) = StatusBarViewModel.FormatDetails(
            St(new KbBackupStatus(false, null, null, null), TunnelStates.Restarting, agent: null));
        Assert.EndsWith("туннель ✗ перезапуск", text);
        Assert.True(warn);
    }

    [Fact]
    public void Apply_SetsDetails()
    {
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { SessionsOkAt = Now, HubVersion = "1.14",
            Status = St(new KbBackupStatus(false, null, null, null)) }, Now);
        Assert.Equal("агент 76a6189 · kb: бэкап выключен", vm.DetailsText);
    }
```
Дописать в `tests/SzDiag.Desk.Tests/HubPollerTests.cs`:
```csharp
    [Fact]
    public async Task Poll_Health_AlsoFetchesStatus()
    {
        var status = new HubStatus("v1", new KbBackupStatus(false, null, null, null), new TunnelStatus(TunnelStates.Off, null));
        var api = new FakeHubApi
        {
            Health = () => new HealthzResponse(30, 1, 1, 1, 1, 0, DateTimeOffset.UtcNow),
            Status = () => status,
        };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Health, default);
        Assert.Same(status, p.Current.Status);
    }
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~StatusBar|FullyQualifiedName~HubPoller"`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/Services/HubSnapshot.cs` — последним параметром записи: `, HubStatus? Status = null`. `Empty` не меняется (семь аргументов).

`src/SzDiag.Desk/Services/HubPoller.cs` — ветка `PollKind.Health`:
```csharp
                case PollKind.Health:
                    var health = await api.GetHealthAsync(ct);
                    var version = health is null ? Current.HubVersion : await api.GetHubVersionAsync(ct);
                    Update(s => s with { Health = health, HubVersion = version });
                    if (health is not null)
                    {
                        // Отдельно и без влияния на «hub жив»: /api/status — детали, старый hub его не знает.
                        var status = await api.GetStatusAsync(ct);
                        Update(s => s with { Status = status });
                    }
                    break;
```
`src/SzDiag.Desk/ViewModels/StatusBarViewModel.cs` — `using SzDiag.Contracts;`, свойства:
```csharp
    [ObservableProperty] private string? _detailsText;
    [ObservableProperty] private bool _detailsWarn;
```
в `Apply` первой строкой:
```csharp
        (DetailsText, DetailsWarn) = FormatDetails(s.Status);
```
и метод:
```csharp
    /// <summary>Пакет агента, последний бэкап kb, туннель. Предупреждение — когда kb не уехал в
    /// remote или туннель не держится: оба случая иначе видны только в консоли hub.</summary>
    public static (string? Text, bool Warn) FormatDetails(HubStatus? s)
    {
        if (s is null) return (null, false);
        var parts = new List<string>();
        var warn = false;
        if (!string.IsNullOrEmpty(s.AgentPackageVersion)) parts.Add($"агент {s.AgentPackageVersion}");

        var kb = s.KbBackup;
        if (!kb.Enabled) parts.Add("kb: бэкап выключен");
        else if (kb.LastRunAt is null) parts.Add("kb: бэкапа ещё не было");
        else if (kb.Outcome is "Pushed" or "NoChanges") parts.Add($"kb {kb.LastRunAt.Value.ToLocalTime():HH:mm} ✓");
        else if (kb.Outcome == "CommittedNotPushed") { parts.Add("kb: не выгружен в remote"); warn = true; }
        else { parts.Add("kb: бэкап упал"); warn = true; }

        switch (s.Tunnel.State)
        {
            case TunnelStates.Running: parts.Add("туннель ✓"); break;
            case TunnelStates.NotFound: parts.Add("туннель: нет cloudflared"); warn = true; break;
            case TunnelStates.Restarting: parts.Add("туннель ✗ перезапуск"); warn = true; break;
        }
        return (string.Join(" · ", parts), warn);
    }
```
`src/SzDiag.Desk/Views/StatusBarView.axaml` — во внешний `Grid` колонку `ColumnDefinitions="*,Auto,Auto"`, текст токенов перенести в `Grid.Column="2"`, в `Grid.Column="1"`:
```xml
    <TextBlock Grid.Column="1" Text="{Binding DetailsText}" Classes="secondary" Margin="0,0,18,0"
               IsVisible="{Binding DetailsText, Converter={x:Static ObjectConverters.IsNotNull}}"
               Foreground="{Binding DetailsWarn, Converter={x:Static v:Converters.WarnToBrush}}" />
```
(в корень `UserControl` — `xmlns:v="using:SzDiag.Desk.Views"`). В `src/SzDiag.Desk/Views/Converters.cs`:
```csharp
    public static readonly IValueConverter WarnToBrush =
        new FuncValueConverter<bool, IBrush>(w => w ? Res("Warn") : Res("Text.Secondary"));
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk tests/SzDiag.Desk.Tests
git commit -m "feat(desk): статусбар — пакет агента, бэкап kb, туннель"
```

---

### Task 4: `szcli` подпроцессом и признак заморозки

**Files:**
- Create: `src/SzDiag.Desk/Services/SzcliRunner.cs`, `FreezeProbe.cs`, `DeskTools.cs`
- Create: `tests/SzDiag.Desk.Tests/SzcliRunnerTests.cs`, `FakeSzcliRunner.cs`, `ManualClock.cs`

**Interfaces:**
- Produces: `record SzcliResult(int ExitCode, string Output)`; `interface ISzcliRunner { string? Location { get; } Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct); }`; `SzcliRunner(Func<string?> szcliCmd)` + `static string? ExeFor(string? szcliCmd)`, `static string StripAnsi(string s)`, коды: `-1` — szcli не найден, `-2` — прервано; `FreezeProbe(Func<string?> szcliCmd)` — `bool IsFrozen(string sz)`; `record DeskTools(IHubApiClient Api, ISzcliRunner Szcli, KbPaths Kb, Action<Action> Ui)`; в тестах `FakeSzcliRunner` (`Calls`, `Respond`, `Gate`) и `ManualClock : TimeProvider` (`Now`, `Advance`).

- [ ] **Step 1: Обвязка тестов**

`tests/SzDiag.Desk.Tests/ManualClock.cs`:
```csharp
namespace SzDiag.Desk.Tests;

public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
```
`tests/SzDiag.Desk.Tests/FakeSzcliRunner.cs`:
```csharp
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public sealed class FakeSzcliRunner : ISzcliRunner
{
    public List<IReadOnlyList<string>> Calls { get; } = new();
    public Func<IReadOnlyList<string>, SzcliResult> Respond { get; set; } = _ => new SzcliResult(0, "ok");

    /// <summary>Не null — вызов ждёт, пока тест его не отпустит.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public string? Location => @"C:\dist\host\szcli.cmd";

    public async Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        Calls.Add(args);
        if (Gate is not null) await Gate.Task.WaitAsync(ct);
        return Respond(args);
    }
}
```

- [ ] **Step 2: Failing tests**

`tests/SzDiag.Desk.Tests/SzcliRunnerTests.cs`:
```csharp
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

/// <summary>Настоящий подпроцесс: вместо SzDiag.Cli.exe — копия cmd.exe в раскладке dist
/// (`host\szcli.cmd` + `host\cli\SzDiag.Cli.exe`).</summary>
public class SzcliRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szcli-" + Guid.NewGuid().ToString("N"));
    private string Cmd => Path.Combine(_dir, "szcli.cmd");

    public SzcliRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cli"));
        File.WriteAllText(Cmd, "@echo off");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(_dir, "cli", "SzDiag.Cli.exe"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Runs_CapturesOutputAndExitCode_NoColor()
    {
        var r = await new SzcliRunner(() => Cmd).RunAsync(new[] { "/d", "/c", "echo hello& echo %NO_COLOR%& exit 3" }, default);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("hello", r.Output);
        Assert.Contains("1", r.Output);   // NO_COLOR=1: Spectre не красит, в окне нет ESC-мусора
    }

    [Fact]
    public async Task NoExe_ExplainsBuildDist()
    {
        var r = await new SzcliRunner(() => null).RunAsync(new[] { "list" }, default);
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("build-dist", r.Output);
    }

    [Fact]
    public async Task Cancel_KillsAndSaysSo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var r = await new SzcliRunner(() => Cmd).RunAsync(new[] { "/d", "/c", "ping -n 30 127.0.0.1 >nul" }, cts.Token);
        Assert.Equal(-2, r.ExitCode);
        Assert.Contains("прервано", r.Output);
    }

    [Fact]
    public void StripAnsi_RemovesEscapes()
        => Assert.Equal("СЗ 161432 ok", SzcliRunner.StripAnsi("\u001b[32mСЗ 161432\u001b[0m ok"));

    [Fact]
    public void FreezeProbe_SameFileAsSzcliList()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cli", "freeze"));
        File.WriteAllText(Path.Combine(_dir, "cli", "freeze", "161432.json"), "{}");
        var probe = new FreezeProbe(() => Cmd);
        Assert.True(probe.IsFrozen("161432"));
        Assert.False(probe.IsFrozen("161501"));
        Assert.False(new FreezeProbe(() => null).IsFrozen("161432"));
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~SzcliRunner`
Expected: ошибка компиляции.

- [ ] **Step 4: Реализация**

`src/SzDiag.Desk/Services/SzcliRunner.cs`:
```csharp
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace SzDiag.Desk.Services;

public sealed record SzcliResult(int ExitCode, string Output);

public interface ISzcliRunner
{
    /// <summary>Путь к szcli.cmd; null — не найден.</summary>
    string? Location { get; }

    Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>Действия инспектора и паспорт железа — тем же szcli, что в терминале: freeze хранит
/// прежние значения рядом с szcli, у close свои защиты, у sz fetch своя сессия захвата. Вторая
/// реализация тех же команд в Desk разошлась бы с первой при первой правке.</summary>
public sealed partial class SzcliRunner(Func<string?> szcliCmd) : ISzcliRunner
{
    public const int NotFound = -1;
    public const int Cancelled = -2;

    public string? Location => szcliCmd();

    /// <summary>szcli.cmd — обёртка над `cli\SzDiag.Cli.exe`; exe зовём напрямую, без cmd /c.</summary>
    public static string? ExeFor(string? szcliCmd)
    {
        if (szcliCmd is null) return null;
        var exe = Path.Combine(Path.GetDirectoryName(szcliCmd)!, "cli", "SzDiag.Cli.exe");
        return File.Exists(exe) ? exe : null;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex Ansi();

    public static string StripAnsi(string s) => Ansi().Replace(s, "");

    public async Task<SzcliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var exe = ExeFor(szcliCmd());
        if (exe is null)
            return new SzcliResult(NotFound,
                "szcli не найден: собери dist (tools\\build-dist.ps1) — Desk зовёт те же команды, что и терминал.");

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Spectre при NO_COLOR не пишет ESC-последовательности — в окне им не место.
        psi.Environment["NO_COLOR"] = "1";

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            await p.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new SzcliResult(Cancelled, Combine(await stdout, await stderr) + "\n[прервано оператором]");
        }
        return new SzcliResult(p.ExitCode, Combine(await stdout, await stderr));
    }

    private static string Combine(string stdout, string stderr)
        => StripAnsi(stderr.Length == 0 ? stdout : $"{stdout}\n{stderr}").TrimEnd();
}
```
`src/SzDiag.Desk/Services/FreezeProbe.cs`:
```csharp
namespace SzDiag.Desk.Services;

/// <summary>Заморожен ли Windows Update на СЗ — тот же признак, что у `szcli list`: файл прежних
/// значений `cli\freeze\&lt;СЗ&gt;.json` рядом с szcli (его заводит freeze и снимает unfreeze).</summary>
public sealed class FreezeProbe(Func<string?> szcliCmd)
{
    public bool IsFrozen(string sz)
        => szcliCmd() is { } cmd
           && File.Exists(Path.Combine(Path.GetDirectoryName(cmd)!, "cli", "freeze", $"{sz}.json"));
}
```
`src/SzDiag.Desk/Services/DeskTools.cs`:
```csharp
using SzDiag.HubClient;
using SzDiag.Kb;

namespace SzDiag.Desk.Services;

/// <param name="Ui">Выполнить действие в UI-потоке (FileSystemWatcher зовёт с пула).</param>
public sealed record DeskTools(IHubApiClient Api, ISzcliRunner Szcli, KbPaths Kb, Action<Action> Ui);
```

- [ ] **Step 5: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~SzcliRunner`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Desk/Services tests/SzDiag.Desk.Tests/SzcliRunnerTests.cs tests/SzDiag.Desk.Tests/FakeSzcliRunner.cs tests/SzDiag.Desk.Tests/ManualClock.cs
git commit -m "feat(desk): szcli подпроцессом и признак заморозки WU"
```

---

### Task 5: Инспектор — вкладки и опрос только видимой, бейдж `🧊`

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/Inspector/IInspectorTab.cs`, `InspectorViewModel.cs`
- Create: `tests/SzDiag.Desk.Tests/FakeInspectorTab.cs`, `InspectorViewModelTests.cs`, `MainViewModelInspectorTests.cs`
- Modify: `src/SzDiag.Desk/ViewModels/MainViewModel.cs`, `src/SzDiag.Desk/ViewModels/SzItemViewModel.cs`

**Interfaces:**
- Consumes: `FreezeProbe` (задача 4), `ManualClock` (задача 4).
- Produces: `interface IInspectorTab { string Title; TimeSpan? Interval; bool RefreshOnReboot => false; Task RefreshAsync(string sz, CancellationToken ct); void Clear(); }`; `InspectorViewModel(IReadOnlyList<IInspectorTab?> tabs, TimeProvider time)` — `string? Sz`, `int SelectedIndex`, `IInspectorTab? Current`, `void Select(string? sz)`, `void OnRebootCountChanged()`, `Task TickAsync()`, `Task RunLoopAsync(CancellationToken ct)`; индекс 0 — «Обзор» (`null` в списке); `MainViewModel(…, ChatServices? chat = null, InspectorViewModel? inspector = null, FreezeProbe? freeze = null)` + свойство `Inspector`; `SzItemViewModel.IsFrozen`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/FakeInspectorTab.cs`:
```csharp
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public sealed class FakeInspectorTab(TimeSpan? interval, bool onReboot = false) : IInspectorTab
{
    public string Title => "fake";
    public TimeSpan? Interval => interval;
    public bool RefreshOnReboot => onReboot;
    public List<string> Refreshed { get; } = new();
    public int Cleared { get; private set; }
    public TaskCompletionSource? Gate { get; set; }

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        Refreshed.Add(sz);
        if (Gate is not null) await Gate.Task;
    }

    public void Clear() => Cleared++;
}
```
`tests/SzDiag.Desk.Tests/InspectorViewModelTests.cs`:
```csharp
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class InspectorViewModelTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeInspectorTab _jobs = new(TimeSpan.FromSeconds(3));
    private readonly FakeInspectorTab _reboots = new(null, onReboot: true);

    private InspectorViewModel New() => new(new IInspectorTab?[] { null, _jobs, _reboots }, _clock);

    [Fact]
    public void Select_RefreshesVisibleTabOnly()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        Assert.Equal(new[] { "161432" }, _jobs.Refreshed);
        Assert.Empty(_reboots.Refreshed);
    }

    [Fact]
    public async Task Tick_RespectsInterval()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        await vm.TickAsync();
        Assert.Single(_jobs.Refreshed);

        _clock.Advance(TimeSpan.FromSeconds(3));
        await vm.TickAsync();
        Assert.Equal(2, _jobs.Refreshed.Count);
    }

    [Fact]
    public async Task HiddenTab_NeverPolled()
    {
        // Опрос exec под нагрузкой дорог: открытый на «Обзоре» инспектор не дёргает ни одну вкладку.
        var vm = New();
        vm.Select("161432");
        for (var i = 0; i < 5; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(10));
            await vm.TickAsync();
        }
        Assert.Empty(_jobs.Refreshed);
        Assert.Empty(_reboots.Refreshed);
    }

    [Fact]
    public async Task NoIntervalTab_OnlyOnOpen()
    {
        var vm = New();
        vm.Select("161432");
        vm.SelectedIndex = 2;
        _clock.Advance(TimeSpan.FromMinutes(5));
        await vm.TickAsync();
        Assert.Single(_reboots.Refreshed);
    }

    [Fact]
    public void OtherSz_ClearsAllTabs_RefreshesVisible()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        vm.Select("161501");
        Assert.Equal(2, _jobs.Cleared);
        Assert.Equal(2, _reboots.Cleared);
        Assert.Equal(new[] { "161432", "161501" }, _jobs.Refreshed);
    }

    [Fact]
    public void RebootCountChanged_RefreshesRebootTab()
    {
        var vm = New();
        vm.SelectedIndex = 2;
        vm.Select("161432");
        vm.OnRebootCountChanged();
        Assert.Equal(2, _reboots.Refreshed.Count);
    }

    [Fact]
    public async Task InFlight_NoDoubleRefresh()
    {
        _jobs.Gate = new TaskCompletionSource();
        var vm = New();
        vm.SelectedIndex = 1;
        vm.Select("161432");
        _clock.Advance(TimeSpan.FromSeconds(10));
        await Task.WhenAny(vm.TickAsync(), Task.Delay(100));
        Assert.Single(_jobs.Refreshed);
        _jobs.Gate.SetResult();
    }

    [Fact]
    public void NoSz_NothingRefreshed()
    {
        var vm = New();
        vm.SelectedIndex = 1;
        Assert.Empty(_jobs.Refreshed);
    }
}
```
`tests/SzDiag.Desk.Tests/MainViewModelInspectorTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class MainViewModelInspectorTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly FakeInspectorTab _reboots = new(null, onReboot: true);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szinsp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SessionInfo S(string sz, int reboots = 0) => new(sz, "10.0.0.5", "PC", SessionStatus.Online,
        _clock.Now.AddHours(-1), _clock.Now, RebootCount: reboots);

    private HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = _clock.Now };

    private MainViewModel New(InspectorViewModel inspector, FreezeProbe? freeze = null)
        => new(new HubPoller(new FakeHubApi(), _clock), new DeskUiState(), _clock, null, inspector, freeze);

    [Fact]
    public void Selected_DrivesInspector()
    {
        var inspector = new InspectorViewModel(new IInspectorTab?[] { null, _reboots }, _clock);
        var vm = New(inspector);
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        Assert.Equal("161432", inspector.Sz);
        vm.Selected = null;
        Assert.Null(inspector.Sz);
    }

    [Fact]
    public void RebootOfSelected_RefreshesRebootTab()
    {
        var inspector = new InspectorViewModel(new IInspectorTab?[] { null, _reboots }, _clock) { SelectedIndex = 1 };
        var vm = New(inspector);
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        vm.Apply(Snap(S("161432", reboots: 1)));
        Assert.Equal(2, _reboots.Refreshed.Count);
    }

    [Fact]
    public void FreezeBadge_FromProbe()
    {
        var cmd = Path.Combine(_dir, "szcli.cmd");
        Directory.CreateDirectory(Path.Combine(_dir, "cli", "freeze"));
        File.WriteAllText(Path.Combine(_dir, "cli", "freeze", "161432.json"), "{}");
        var vm = New(new InspectorViewModel(new IInspectorTab?[] { null }, _clock), new FreezeProbe(() => cmd));

        vm.Apply(Snap(S("161432"), S("161501")));

        Assert.True(vm.Items.Single(i => i.Sz == "161432").IsFrozen);
        Assert.False(vm.Items.Single(i => i.Sz == "161501").IsFrozen);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~Inspector"`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/Inspector/IInspectorTab.cs`:
```csharp
namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Вкладка инспектора. Обновляется только видимая — под нагрузкой exec-канал клиента
/// и так узкий (спека, «Источники данных GUI»).</summary>
public interface IInspectorTab
{
    string Title { get; }

    /// <summary>Как часто обновлять, пока вкладка видна; null — только при открытии и по кнопке.</summary>
    TimeSpan? Interval { get; }

    /// <summary>Обновить заново при смене ⚡N выбранной СЗ.</summary>
    bool RefreshOnReboot => false;

    /// <summary>Ошибки обрабатывает сама (показывает на вкладке) — наружу не бросает.</summary>
    Task RefreshAsync(string sz, CancellationToken ct);

    /// <summary>Выбрана другая СЗ — забыть данные прежней.</summary>
    void Clear();
}
```
`src/SzDiag.Desk/ViewModels/Inspector/InspectorViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Правая панель: вкладки выбранной СЗ. Обновляет только видимую — по её интервалу,
/// раз в секунду из окна (<see cref="RunLoopAsync"/>). Индекс 0 — «Обзор» (null: данные в
/// карточке СЗ, опрашивать нечего).</summary>
public sealed partial class InspectorViewModel : ObservableObject
{
    private readonly IReadOnlyList<IInspectorTab?> _tabs;
    private readonly TimeProvider _time;
    private readonly Dictionary<int, DateTimeOffset> _refreshedAt = new();
    private readonly HashSet<int> _inFlight = new();
    private CancellationTokenSource _szCts = new();

    public InspectorViewModel(IReadOnlyList<IInspectorTab?> tabs, TimeProvider time)
    {
        _tabs = tabs;
        _time = time;
    }

    public string? Sz { get; private set; }

    [ObservableProperty] private int _selectedIndex;

    public IInspectorTab? Current => SelectedIndex >= 0 && SelectedIndex < _tabs.Count ? _tabs[SelectedIndex] : null;

    partial void OnSelectedIndexChanged(int value) => _ = RefreshIfDueAsync();

    public void Select(string? sz)
    {
        if (sz == Sz) return;
        Sz = sz;
        _szCts.Cancel();
        _szCts = new CancellationTokenSource();
        _refreshedAt.Clear();
        foreach (var tab in _tabs) tab?.Clear();
        _ = RefreshIfDueAsync();
    }

    public void OnRebootCountChanged()
    {
        for (var i = 0; i < _tabs.Count; i++)
            if (_tabs[i] is { RefreshOnReboot: true }) _refreshedAt.Remove(i);
        _ = RefreshIfDueAsync();
    }

    public Task TickAsync() => RefreshIfDueAsync();

    public async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
            await TickAsync();
        }
    }

    private async Task RefreshIfDueAsync()
    {
        var index = SelectedIndex;
        var tab = Current;
        var sz = Sz;
        if (tab is null || sz is null || _inFlight.Contains(index)) return;

        var now = _time.GetUtcNow();
        if (_refreshedAt.TryGetValue(index, out var at) && (tab.Interval is not { } every || now - at < every)) return;

        _inFlight.Add(index);
        _refreshedAt[index] = now;
        try
        {
            await tab.RefreshAsync(sz, _szCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Выбрали другую СЗ посреди обновления.
        }
        catch (Exception ex)
        {
            DeskLog.Write($"инспектор, вкладка «{tab.Title}»: {ex.Message}");
        }
        finally
        {
            _inFlight.Remove(index);
        }
    }
}
```
`src/SzDiag.Desk/ViewModels/SzItemViewModel.cs` — после `HasSession`:
```csharp
    /// <summary>Windows Update заморожен (бейдж 🧊): не забыть unfreeze до закрытия.</summary>
    [ObservableProperty] private bool _isFrozen;
```
`src/SzDiag.Desk/ViewModels/MainViewModel.cs`:
- `using SzDiag.Desk.ViewModels.Inspector;`;
- поле `private readonly FreezeProbe? _freeze;`;
- конструктор: `public MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time, ChatServices? chat = null, InspectorViewModel? inspector = null, FreezeProbe? freeze = null)`; **до** строки `if (chat is null) return;` добавить `Inspector = inspector;` и `_freeze = freeze;`;
- свойство `public InspectorViewModel? Inspector { get; }`;
- в `OnSelectedChanged` первой строкой: `Inspector?.Select(value?.Sz);`;
- в `Apply` сразу после строки `if (selectedSz is not null && Items.All(i => i.Sz != selectedSz)) Selected = null;`:
```csharp
        if (_freeze is not null)
            foreach (var item in Items) item.IsFrozen = _freeze.IsFrozen(item.Sz);
        // ⚡N выбранной СЗ вырос — вкладка вырубонов перечитывается сама.
        if (Selected is { } sel && before.TryGetValue(sel.Sz, out var was) && sel.RebootCount > was.RebootCount)
            Inspector?.OnRebootCountChanged();
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное (11 новых).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels tests/SzDiag.Desk.Tests/FakeInspectorTab.cs tests/SzDiag.Desk.Tests/InspectorViewModelTests.cs tests/SzDiag.Desk.Tests/MainViewModelInspectorTests.cs
git commit -m "feat(desk): инспектор — вкладки с опросом только видимой, бейдж заморозки"
```

---

### Task 6: Вкладка «Вырубоны»

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/Inspector/RebootsTabViewModel.cs`, `tests/SzDiag.Desk.Tests/RebootsTabViewModelTests.cs`

**Interfaces:**
- Consumes: `IHubApiClient.GetRebootsAsync`, `RebootEvent`, `ShutdownKind.Describe`, `BugcheckCodes.Format`, `RebootSource`.
- Produces: `record RebootRow(string When, string Kind, string Detail, bool IsFailure)`; `RebootsTabViewModel(IHubApiClient api) : IInspectorTab` — `ObservableCollection<RebootRow> Rows`, `string Summary`; `RefreshOnReboot = true`, `Interval = null`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/RebootsTabViewModelTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class RebootsTabViewModelTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Events_NewestFirst_KindBugcheckUptimeActivity()
    {
        var api = new FakeHubApi
        {
            Reboots = sz => new RebootTimeline(sz, new[]
            {
                new RebootEvent(sz, T, null, null, 4 * 3600 + 12 * 60, "OCCT Power", ShutdownKind.HardOff),
                new RebootEvent(sz, T.AddHours(5), null, null, 1800, null, ShutdownKind.Bsod,
                    RebootSource.Journal, Bugcheck: 0x50),
            }, 4 * 3600 + 12 * 60),
        };
        var vm = new RebootsTabViewModel(api);
        await vm.RefreshAsync("161432", default);

        Assert.Equal(2, vm.Rows.Count);
        Assert.StartsWith("BSOD 0x50 PAGE_FAULT_IN_NONPAGED_AREA", vm.Rows[0].Kind);
        Assert.Contains("из журнала клиента", vm.Rows[0].Detail);
        Assert.Equal("обрыв питания", vm.Rows[1].Kind);
        Assert.Contains("продержалась 4ч 12м", vm.Rows[1].Detail);
        Assert.Contains("занята: OCCT Power", vm.Rows[1].Detail);
        Assert.Contains("событий: 2", vm.Summary);
    }

    [Fact]
    public async Task None_SaysSo()
    {
        var vm = new RebootsTabViewModel(new FakeHubApi { Reboots = sz => new RebootTimeline(sz, Array.Empty<RebootEvent>(), null) });
        await vm.RefreshAsync("161432", default);
        Assert.Empty(vm.Rows);
        Assert.StartsWith("вырубонов не зафиксировано", vm.Summary);
    }

    [Fact]
    public async Task HubError_SaysSo_KeepsRows()
    {
        var ok = true;
        var api = new FakeHubApi
        {
            Reboots = sz => ok
                ? new RebootTimeline(sz, new[] { new RebootEvent(sz, T, null, null, 60, null, ShutdownKind.HardOff) }, 60)
                : throw new HttpRequestException("refused"),
        };
        var vm = new RebootsTabViewModel(api);
        await vm.RefreshAsync("161432", default);
        ok = false;
        await vm.RefreshAsync("161432", default);

        Assert.Single(vm.Rows);
        Assert.Contains("hub не отдал", vm.Summary);
    }

    [Fact]
    public void RefreshOnReboot_NoInterval()
    {
        IInspectorTab tab = new RebootsTabViewModel(new FakeHubApi());
        Assert.True(tab.RefreshOnReboot);
        Assert.Null(tab.Interval);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~RebootsTab`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/Inspector/RebootsTabViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels.Inspector;

public sealed record RebootRow(string When, string Kind, string Detail, bool IsFailure);

/// <summary>Таймлайн вырубонов (то же, что `szcli reboots`): когда, чем был (обрыв/кнопка/BSOD —
/// по журналу клиента), сколько продержалась и чем была занята.</summary>
public sealed partial class RebootsTabViewModel(IHubApiClient api) : ObservableObject, IInspectorTab
{
    public string Title => "Вырубоны";
    public TimeSpan? Interval => null;
    public bool RefreshOnReboot => true;

    public ObservableCollection<RebootRow> Rows { get; } = new();

    [ObservableProperty] private string _summary = "";

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        RebootTimeline? t;
        try
        {
            t = await api.GetRebootsAsync(sz, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Прежний список не стираем: он верен на момент прошлого ответа.
            Summary = $"hub не отдал таймлайн: {ex.Message}";
            return;
        }

        Rows.Clear();
        var watching = t?.WatchingSince is { } since ? $" (наблюдение с {since.ToLocalTime():dd.MM HH:mm})" : "";
        if (t is null || t.Events.Count == 0)
        {
            Summary = "вырубонов не зафиксировано" + watching;
            return;
        }
        foreach (var e in t.Events.OrderByDescending(e => e.At)) Rows.Add(Row(e));
        var longest = t.MaxUptimeSeconds is { } m ? $" · дольше всего без ребута: {Span(TimeSpan.FromSeconds(m))}" : "";
        Summary = $"событий: {t.Events.Count}, отказов: {t.Events.Count(e => e.IsFailure)}{longest}{watching}";
    }

    internal static RebootRow Row(RebootEvent e)
    {
        var kind = ShutdownKind.Describe(e.Kind);
        if (e.Bugcheck is { } code and not 0) kind += " " + BugcheckCodes.Format((uint)code);
        var parts = new List<string>();
        if (e.UptimeBefore is { } up) parts.Add($"продержалась {Span(up)}");
        if (!string.IsNullOrEmpty(e.ActivityBefore)) parts.Add($"занята: {e.ActivityBefore}");
        if (e.Source == RebootSource.Journal) parts.Add("из журнала клиента");
        return new RebootRow($"{e.At.ToLocalTime():dd.MM HH:mm}", kind, string.Join(" · ", parts), e.IsFailure);
    }

    internal static string Span(TimeSpan t)
        => t.TotalHours >= 1 ? $"{(int)t.TotalHours}ч {t.Minutes:00}м" : $"{(int)t.TotalMinutes}м";

    public void Clear()
    {
        Rows.Clear();
        Summary = "";
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~RebootsTab`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels/Inspector/RebootsTabViewModel.cs tests/SzDiag.Desk.Tests/RebootsTabViewModelTests.cs
git commit -m "feat(desk): вкладка вырубонов в инспекторе"
```

---

### Task 7: Вкладка «Задачи»

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/Inspector/JobsTabViewModel.cs`, `tests/SzDiag.Desk.Tests/JobsTabViewModelTests.cs`

**Interfaces:**
- Consumes: `IHubApiClient.ExecJobsAsync`, `ExecStatusAsync`, `ExecJobStatus`, `CliXml.Decode` (задача 2).
- Produces: `record JobRow(string Id, string State, string? Script)`; `JobsTabViewModel(IHubApiClient api) : IInspectorTab` — `ObservableCollection<JobRow> Rows`, `JobRow? Selected`, `string Message`, `string Output`, `static IReadOnlyList<JobRow> Parse(string text)`, `const int OutputTail = 40`; `Interval = 3 с`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/JobsTabViewModelTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class JobsTabViewModelTests
{
    // Ровно так список отдаёт агент (ExecCommandHandler.ListJobs).
    private const string List =
        "a1b2c3  выполняется, старт 25.09 12:00:01, вывода 1024 б\n    & C:\\OCCT\\OCCTCmd.exe test\n" +
        "d4e5f6  завершена (exit 0), старт 25.09 11:00:00, вывода 12 б";

    private static ExecJobStatus Listing(string text) => new("r", "*", false, null, text, DateTimeOffset.Now, 0);

    [Fact]
    public void Parse_RowsWithScriptPreview()
    {
        var rows = JobsTabViewModel.Parse(List);
        Assert.Equal(new[] { "a1b2c3", "d4e5f6" }, rows.Select(r => r.Id));
        Assert.StartsWith("выполняется", rows[0].State);
        Assert.Equal("& C:\\OCCT\\OCCTCmd.exe test", rows[0].Script);
        Assert.Null(rows[1].Script);
        Assert.Empty(JobsTabViewModel.Parse("фоновых задач нет"));
    }

    [Fact]
    public async Task Refresh_ListsJobs_SelectedShowsOutput()
    {
        var api = new FakeHubApi
        {
            Jobs = _ => Listing(List),
            JobStatus = (_, id) => new ExecJobStatus("r", id, true, null, $"вывод {id}", DateTimeOffset.Now, 10),
        };
        var vm = new JobsTabViewModel(api);
        await vm.RefreshAsync("161432", default);
        Assert.Equal(2, vm.Rows.Count);

        vm.Selected = vm.Rows[0];
        await vm.RefreshAsync("161432", default);
        Assert.Equal("вывод a1b2c3", vm.Output);
        Assert.Equal("a1b2c3", vm.Selected!.Id);   // выбор переживает обновление списка
    }

    [Fact]
    public async Task NoJobs_SaysSo()
    {
        var vm = new JobsTabViewModel(new FakeHubApi { Jobs = _ => Listing("фоновых задач нет") });
        await vm.RefreshAsync("161432", default);
        Assert.Empty(vm.Rows);
        Assert.Equal("фоновых задач нет", vm.Message);
    }

    [Fact]
    public async Task Timeout_KeepsRowsAndSaysSo()
    {
        // Под полной нагрузкой exec-канал глохнет: это не вырубон и не повод очищать список.
        var loaded = false;
        var api = new FakeHubApi
        {
            Jobs = _ => loaded ? throw new TimeoutException("нет ответа") : Listing(List),
        };
        var vm = new JobsTabViewModel(api);
        await vm.RefreshAsync("161432", default);
        loaded = true;
        await vm.RefreshAsync("161432", default);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains("агент не ответил", vm.Message);
    }

    [Fact]
    public async Task SzOffline_SaysSo()
    {
        var vm = new JobsTabViewModel(new FakeHubApi { Jobs = _ => null });
        await vm.RefreshAsync("161432", default);
        Assert.Equal("СЗ не на связи", vm.Message);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~JobsTab`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/Inspector/JobsTabViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels.Inspector;

public sealed record JobRow(string Id, string State, string? Script);

/// <summary>Фоновые задачи агента (`szcli exec --jobs`) и хвост вывода выбранной
/// (`exec --result --tail`). Под нагрузкой exec может не ответить — тогда список остаётся
/// прежним, а вкладка говорит об этом прямо.</summary>
public sealed partial class JobsTabViewModel(IHubApiClient api) : ObservableObject, IInspectorTab
{
    public const int OutputTail = 40;

    private string? _sz;
    private bool _reselecting;

    public string Title => "Задачи";
    public TimeSpan? Interval => TimeSpan.FromSeconds(3);

    public ObservableCollection<JobRow> Rows { get; } = new();

    [ObservableProperty] private JobRow? _selected;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _output = "";

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        ExecJobStatus? list;
        try
        {
            list = await api.ExecJobsAsync(sz, ct);
        }
        catch (TimeoutException)
        {
            Message = "агент не ответил — под нагрузкой exec глохнет; список на прошлый опрос";
            return;
        }
        catch (HttpRequestException ex)
        {
            Message = $"hub: {ex.Message}";
            return;
        }
        if (list is null)
        {
            Message = "СЗ не на связи";
            return;
        }

        var rows = Parse(list.Tail);
        var keep = Selected?.Id;
        _reselecting = true;
        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        Selected = Rows.FirstOrDefault(r => r.Id == keep);
        _reselecting = false;
        Message = rows.Count == 0 ? "фоновых задач нет" : "";
        if (Selected is { } s) await LoadOutputAsync(s, ct);
    }

    partial void OnSelectedChanged(JobRow? value)
    {
        if (_reselecting) return;
        if (value is null) Output = "";
        else _ = LoadOutputAsync(value, CancellationToken.None);
    }

    private async Task LoadOutputAsync(JobRow job, CancellationToken ct)
    {
        if (_sz is not { } sz) return;
        try
        {
            var st = await api.ExecStatusAsync(sz, job.Id, OutputTail, ct);
            if (st is null) return;
            Output = CliXml.Decode(st.Tail) + (st.Error is { } e ? $"\n[ошибка: {e}]" : "");
        }
        catch (TimeoutException)
        {
            // Хвост тот же, что был: под нагрузкой это штатно.
        }
        catch (HttpRequestException ex)
        {
            Output = $"hub: {ex.Message}";
        }
    }

    [GeneratedRegex(@"^(\S+)  (.+)$")]
    private static partial Regex Head();

    /// <summary>Разбор списка агента: строка задачи «id  состояние…», под ней с отступом — начало скрипта.</summary>
    public static IReadOnlyList<JobRow> Parse(string text)
    {
        var rows = new List<JobRow>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("    ", StringComparison.Ordinal) && rows.Count > 0)
            {
                rows[^1] = rows[^1] with { Script = line.Trim() };
                continue;
            }
            var m = Head().Match(line);
            if (m.Success) rows.Add(new JobRow(m.Groups[1].Value, m.Groups[2].Value, null));
        }
        return rows;
    }

    public void Clear()
    {
        _sz = null;
        _reselecting = true;
        Rows.Clear();
        Selected = null;
        _reselecting = false;
        Message = "";
        Output = "";
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~JobsTab`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels/Inspector/JobsTabViewModel.cs tests/SzDiag.Desk.Tests/JobsTabViewModelTests.cs
git commit -m "feat(desk): вкладка фоновых задач агента"
```

---

### Task 8: Вкладка «Сенсоры»

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/Inspector/SensorsTabViewModel.cs`, `Sparkline.cs`, `tests/SzDiag.Desk.Tests/SensorsTabViewModelTests.cs`

**Interfaces:**
- Consumes: `IHubApiClient.ExecAsync`, `SensorReport.ParseAny`, `SensorSample`, `SensorPaths.LhmCsv` (задача 2), `ISzcliRunner` (задача 4).
- Produces: `static IReadOnlyList<Avalonia.Point> Sparkline.Points(IReadOnlyList<double?> values, double width, double height)`; `SensorsTabViewModel(IHubApiClient api, ISzcliRunner szcli, TimeProvider time) : IInspectorTab` — `string Status`, `bool NotWriting`, `bool HasData`, `string CpuText`, `GpuText`, `PowerText`, `VoltText`, `IReadOnlyList<Point> CpuTempLine`, `GpuTempLine`, `bool HasCpuLine`, `HasGpuLine`, `StartSensorsCommand`; константы `TailRows = 120`, `StaleAfter = 30 с`, `ChartWidth = 260`, `ChartHeight = 56`, `NoCsvMarker = "SZDIAG_NO_CSV"`; `Interval = 5 с`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/SensorsTabViewModelTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class SensorsTabViewModelTests
{
    private readonly ManualClock _clock = new();
    private readonly FakeSzcliRunner _szcli = new();

    // Формат лёгкого наблюдателя: time;cpu%;стресс;cpu°C;ram%;gpu%;gpu°C;gpu W;cpu МГц.
    private static string Csv(params (string Time, double CpuTemp, double GpuTemp)[] rows)
        => "time;cpu;stress;cputemp;ram;gpu;gputemp;gpuw;clock\n" + string.Join("\n",
            rows.Select(r => $"{r.Time};95;2;{r.CpuTemp.ToString(System.Globalization.CultureInfo.InvariantCulture)};40;97;{r.GpuTemp.ToString(System.Globalization.CultureInfo.InvariantCulture)};180;4500"));

    private static ExecResult Out(string stdout) => new("r", 0, stdout, "");

    private SensorsTabViewModel New(Func<string, string, ExecResult?> exec)
        => new(new FakeHubApi { Exec = exec }, _szcli, _clock);

    [Fact]
    public async Task Refresh_LatestValues_AndCharts()
    {
        var vm = New((_, script) =>
        {
            Assert.Contains(SensorPaths.LhmCsv, script);
            return Out(Csv(("2026-09-25 12:00:00", 70, 60), ("2026-09-25 12:00:01", 72.5, 61)));
        });
        await vm.RefreshAsync("161432", default);

        Assert.True(vm.HasData);
        Assert.False(vm.NotWriting);
        Assert.Contains("72.5 °C", vm.CpuText);
        Assert.Contains("61 °C", vm.GpuText);
        Assert.Contains("180 Вт", vm.PowerText);
        Assert.Equal(2, vm.CpuTempLine.Count);
        Assert.True(vm.HasCpuLine);
    }

    [Fact]
    public async Task NoCsv_NotWriting_OffersStart()
    {
        var vm = New((_, _) => Out(SensorsTabViewModel.NoCsvMarker));
        await vm.RefreshAsync("161432", default);
        Assert.True(vm.NotWriting);
        Assert.False(vm.HasData);
        Assert.Contains("сенсоры не пишутся", vm.Status);

        await vm.StartSensorsCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "sensors", "start", "161432" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task SameLastRowFor30sOfHostTime_NotWriting()
    {
        // Время строк — по часам клиента (в WinPE сдвинуто на пояс): «не пишутся» считаем по
        // часам бокса — последняя строка не менялась 30 с.
        var csv = Csv(("2026-09-25 03:00:00", 70, 60));
        var vm = New((_, _) => Out(csv));
        await vm.RefreshAsync("161432", default);
        Assert.False(vm.NotWriting);

        _clock.Advance(TimeSpan.FromSeconds(31));
        await vm.RefreshAsync("161432", default);
        Assert.True(vm.NotWriting);
        Assert.True(vm.HasData);

        csv = Csv(("2026-09-25 03:00:00", 70, 60), ("2026-09-25 03:00:31", 71, 60));
        await vm.RefreshAsync("161432", default);
        Assert.False(vm.NotWriting);
    }

    [Fact]
    public async Task Timeout_KeepsValuesAndSaysSo()
    {
        var fail = false;
        var vm = New((_, _) => fail ? throw new TimeoutException("нет ответа") : Out(Csv(("2026-09-25 12:00:00", 70, 60))));
        await vm.RefreshAsync("161432", default);
        fail = true;
        await vm.RefreshAsync("161432", default);

        Assert.True(vm.HasData);
        Assert.Contains("70 °C", vm.CpuText);
        Assert.Contains("агент не ответил", vm.Status);
    }

    [Fact]
    public void Sparkline_ScalesToBox_SkipsGaps()
    {
        var pts = Sparkline.Points(new double?[] { 10, null, 20, 30 }, 100, 50);
        Assert.Equal(3, pts.Count);
        Assert.Equal(0, pts[0].X);
        Assert.Equal(50, pts[0].Y);      // минимум — внизу
        Assert.Equal(100, pts[2].X);
        Assert.Equal(0, pts[2].Y);       // максимум — вверху
        Assert.Empty(Sparkline.Points(new double?[] { 5 }, 100, 50));   // из одной точки линии нет
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~SensorsTab`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация** (файлы — инструментом Write: в скрипте обратные слеши)

`src/SzDiag.Desk/ViewModels/Inspector/Sparkline.cs`:
```csharp
using Avalonia;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Точки ломаной в прямоугольнике width×height: минимум внизу, максимум вверху. Пропуски
/// (датчик не отдал значение) выкидываются, X — по порядковому номеру отсчёта.</summary>
public static class Sparkline
{
    public static IReadOnlyList<Point> Points(IReadOnlyList<double?> values, double width, double height)
    {
        var present = values.Select((v, i) => (v, i)).Where(p => p.v is not null).ToList();
        if (present.Count < 2 || values.Count < 2) return Array.Empty<Point>();
        var min = present.Min(p => p.v!.Value);
        var max = present.Max(p => p.v!.Value);
        var span = max - min;
        return present.Select(p => new Point(
                p.i * width / (values.Count - 1),
                span <= 0 ? height / 2 : height - (p.v!.Value - min) / span * height))
            .ToList();
    }
}
```
`src/SzDiag.Desk/ViewModels/Inspector/SensorsTabViewModel.cs`:
```csharp
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Живые сенсоры из CSV `lhmmon`: последний отсчёт и ломаные температур по хвосту.
/// Синхронный exec раз в 5 с и только пока вкладка открыта: под полной нагрузкой он может не
/// ответить — тогда остаются прежние значения, а статус говорит об этом прямо.</summary>
public sealed partial class SensorsTabViewModel(IHubApiClient api, ISzcliRunner szcli, TimeProvider time)
    : ObservableObject, IInspectorTab
{
    public const int TailRows = 120;
    public const double ChartWidth = 260;
    public const double ChartHeight = 56;
    public const string NoCsvMarker = "SZDIAG_NO_CSV";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    private string? _sz;
    private DateTime? _lastSampleTime;
    private DateTimeOffset _lastChangeAt;

    public string Title => "Сенсоры";
    public TimeSpan? Interval => TimeSpan.FromSeconds(5);

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _notWriting;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _voltText = "—";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCpuLine))] private IReadOnlyList<Point> _cpuTempLine = Array.Empty<Point>();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasGpuLine))] private IReadOnlyList<Point> _gpuTempLine = Array.Empty<Point>();

    public bool HasCpuLine => CpuTempLine.Count > 1;
    public bool HasGpuLine => GpuTempLine.Count > 1;

    /// <summary>Шапка + хвост CSV. Шапка нужна парсеру (по ней он узнаёт формат), а в коротком
    /// файле хвост её уже содержит — второй раз не отдаём.</summary>
    internal static string Script => $$"""
        $ErrorActionPreference = 'SilentlyContinue'
        $p = '{{SensorPaths.LhmCsv}}'
        if (-not (Test-Path $p)) { '{{NoCsvMarker}}'; return }
        $head = Get-Content $p -TotalCount 1
        $tail = @(Get-Content $p -Tail {{TailRows}})
        if ($tail.Count -gt 0 -and $tail[0] -eq $head) { $tail = $tail | Select-Object -Skip 1 }
        $head
        $tail
        """;

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        ExecResult? r;
        try
        {
            r = await api.ExecAsync(sz, Script, 15, ct);
        }
        catch (TimeoutException)
        {
            Status = "агент не ответил за 15 с — под нагрузкой exec глохнет; показаны прошлые данные";
            return;
        }
        catch (HttpRequestException ex)
        {
            Status = $"hub: {ex.Message}";
            return;
        }
        if (r is null)
        {
            Status = "СЗ не на связи";
            return;
        }
        if (r.StdOut.Contains(NoCsvMarker, StringComparison.Ordinal))
        {
            NotWriting = true;
            HasData = false;
            Status = $"сенсоры не пишутся: нет {SensorPaths.LhmCsv}";
            return;
        }

        var samples = SensorReport.ParseAny(r.StdOut).Samples;
        if (samples.Count == 0)
        {
            NotWriting = true;
            Status = "сенсоры не пишутся: в CSV нет разборчивых строк";
            return;
        }

        var last = samples[^1];
        var now = time.GetUtcNow();
        if (_lastSampleTime != last.Time)
        {
            _lastSampleTime = last.Time;
            _lastChangeAt = now;
        }
        NotWriting = now - _lastChangeAt >= StaleAfter;
        HasData = true;

        CpuText = $"CPU {F(last.CpuTempC, " °C")} · {F(last.CpuPercent, " %")} · {F(last.CpuClockMhz, " МГц")}";
        GpuText = $"GPU {F(last.GpuTempC, " °C")} · {F(last.GpuPercent, " %")}";
        PowerText = $"мощность CPU {F(last.CpuPowerW, " Вт")} · GPU {F(last.GpuPowerW, " Вт")}";
        VoltText = $"12V {F(last.Volt12, " В", "0.00")} · 5V {F(last.Volt5, " В", "0.00")}";
        CpuTempLine = Sparkline.Points(samples.Select(s => s.CpuTempC).ToList(), ChartWidth, ChartHeight);
        GpuTempLine = Sparkline.Points(samples.Select(s => s.GpuTempC).ToList(), ChartWidth, ChartHeight);
        Status = NotWriting
            ? $"сенсоры не пишутся: последняя строка не менялась {(int)(now - _lastChangeAt).TotalSeconds} с"
            : $"последняя строка {last.Time:HH:mm:ss} (часы клиента), отсчётов в хвосте: {samples.Count}";
    }

    private static string F(double? v, string unit, string format = "0.#")
        => v is { } x ? x.ToString(format, CultureInfo.InvariantCulture) + unit : "—";

    [RelayCommand]
    private async Task StartSensors()
    {
        if (_sz is not { } sz) return;
        Status = "запускаю наблюдатель (szcli sensors start)…";
        var res = await szcli.RunAsync(new[] { "sensors", "start", sz }, CancellationToken.None);
        Status = res.ExitCode == 0 ? "наблюдатель запущен — данные появятся через несколько секунд" : res.Output;
    }

    public void Clear()
    {
        _sz = null;
        _lastSampleTime = null;
        Status = "";
        NotWriting = false;
        HasData = false;
        CpuText = GpuText = PowerText = VoltText = "—";
        CpuTempLine = Array.Empty<Point>();
        GpuTempLine = Array.Empty<Point>();
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~SensorsTab`
Expected: 5 passed. Проверить файл по байтам: `grep -c 'C:\\OCCT' src/SzDiag.Contracts/SensorPaths.cs` → 1, а в `SensorsTabViewModel.cs` путь берётся только из константы.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels/Inspector/SensorsTabViewModel.cs src/SzDiag.Desk/ViewModels/Inspector/Sparkline.cs tests/SzDiag.Desk.Tests/SensorsTabViewModelTests.cs
git commit -m "feat(desk): вкладка сенсоров — последний отсчёт и температуры по хвосту CSV"
```

---

### Task 9: Вкладка «Журнал»

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/Inspector/JournalTabViewModel.cs`, `tests/SzDiag.Desk.Tests/JournalTabViewModelTests.cs`

**Interfaces:**
- Consumes: `KbPaths.Journal(sz)`, `KbPaths.SzDir(sz)`.
- Produces: `JournalTabViewModel(KbPaths kb, Action<Action> ui) : IInspectorTab, IDisposable` — `string Text`, `string FilePath`, `const int TailLines = 300`, `void Reload()`; `Interval = null` (обновление — по изменению файла).

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/JournalTabViewModelTests.cs`:
```csharp
using SzDiag.Desk.ViewModels.Inspector;
using SzDiag.Kb;

namespace SzDiag.Desk.Tests;

public class JournalTabViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "szkbj-" + Guid.NewGuid().ToString("N"));
    private readonly KbPaths _kb;

    public JournalTabViewModelTests()
    {
        _kb = new KbPaths(_root);
        Directory.CreateDirectory(_kb.SzDir("161432"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    [Fact]
    public async Task ShowsTail()
    {
        File.WriteAllLines(_kb.Journal("161432"), Enumerable.Range(1, 350).Select(i => $"строка {i}"));
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);

        var lines = vm.Text.Split('\n');
        Assert.Equal(JournalTabViewModel.TailLines, lines.Length);
        Assert.Equal("строка 51", lines[0]);
        Assert.Equal("строка 350", lines[^1]);
        Assert.Equal(_kb.Journal("161432"), vm.FilePath);
    }

    [Fact]
    public async Task Missing_SaysSo()
    {
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);
        Assert.StartsWith("журнала пока нет", vm.Text);
    }

    [Fact]
    public async Task FileChanges_Reloaded()
    {
        File.WriteAllText(_kb.Journal("161432"), "первая\n");
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);

        File.AppendAllText(_kb.Journal("161432"), "вторая\n");
        for (var i = 0; i < 50 && !vm.Text.Contains("вторая"); i++) await Task.Delay(100);
        Assert.Contains("вторая", vm.Text);
    }

    [Fact]
    public async Task OpenWhileHubWrites_NoException()
    {
        // Hub дописывает журнал своим потоком — читаем с FileShare.ReadWrite.
        var path = _kb.Journal("161432");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync("идёт запись\n"u8.ToArray());
        await writer.FlushAsync();
        using var vm = new JournalTabViewModel(_kb, a => a());
        await vm.RefreshAsync("161432", default);
        Assert.Contains("идёт запись", vm.Text);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~JournalTab`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/Inspector/JournalTabViewModel.cs`:
```csharp
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Kb;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Журнал СЗ (`kb/СЗ/&lt;сз&gt;/журнал.md`) — хвост с диска. Туда hub пишет команды CLI
/// и события машины, а `szcli note` — ручные шаги. Обновляется по изменению файла, а не по таймеру.</summary>
public sealed partial class JournalTabViewModel(KbPaths kb, Action<Action> ui) : ObservableObject, IInspectorTab, IDisposable
{
    public const int TailLines = 300;

    private FileSystemWatcher? _watcher;
    private string? _sz;

    public string Title => "Журнал";
    public TimeSpan? Interval => null;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _filePath = "";

    public Task RefreshAsync(string sz, CancellationToken ct)
    {
        if (_sz != sz)
        {
            _sz = sz;
            Watch(sz);
        }
        Reload();
        return Task.CompletedTask;
    }

    public void Reload()
    {
        if (_sz is not { } sz) return;
        var path = kb.Journal(sz);
        FilePath = path;
        if (!File.Exists(path))
        {
            Text = "журнала пока нет — он заводится с первой командой szcli по СЗ или заметкой";
            return;
        }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var lines = reader.ReadToEnd().Replace("\r", "").TrimEnd('\n').Split('\n');
            Text = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - TailLines)));
        }
        catch (IOException ex)
        {
            Text = $"журнал не прочитался: {ex.Message}";
        }
    }

    private void Watch(string sz)
    {
        _watcher?.Dispose();
        _watcher = null;
        var dir = kb.SzDir(sz);
        if (!Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir, Path.GetFileName(kb.Journal(sz)))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => ui(Reload);
        _watcher.Created += (_, _) => ui(Reload);
    }

    public void Clear()
    {
        _watcher?.Dispose();
        _watcher = null;
        _sz = null;
        Text = "";
        FilePath = "";
    }

    public void Dispose() => _watcher?.Dispose();
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~JournalTab`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels/Inspector/JournalTabViewModel.cs tests/SzDiag.Desk.Tests/JournalTabViewModelTests.cs
git commit -m "feat(desk): вкладка журнала СЗ с обновлением по изменению файла"
```

---

### Task 10: Вкладки «Железо» и «Действия»

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/Inspector/HardwareTabViewModel.cs`, `ActionsViewModel.cs`
- Create: `tests/SzDiag.Desk.Tests/HardwareTabViewModelTests.cs`, `ActionsViewModelTests.cs`

**Interfaces:**
- Consumes: `ISzcliRunner`, `SzcliResult` (задача 4), `FakeSzcliRunner`.
- Produces: `HardwareTabViewModel(ISzcliRunner szcli) : IInspectorTab` — `string Text`, `bool Busy`, `ReloadCommand` (паспорт кэшируется на СЗ); `ActionsViewModel(ISzcliRunner szcli) : IInspectorTab` — поля `DiagSections`, `TestConfig`, `TestFilter`, `NoteText`, `CloseReason`, состояние `Log`, `Running`, `IsIdle`, `PendingConfirm` (`string?`), `LastExitCode` (`int?`); команды `DiagRunCommand`, `TestRunCommand` (нужна метка), `FreezeCommand`, `UnfreezeCommand` (подтверждение), `NoteCommand` (нужен текст), `SzFetchCommand`, `CloseCommand` (подтверждение), `CloseForceCommand` (подтверждение, нужна причина), `ConfirmCommand`, `CancelConfirmCommand`, `CancelCommand`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/HardwareTabViewModelTests.cs`:
```csharp
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class HardwareTabViewModelTests
{
    [Fact]
    public async Task Passport_FromSzcli_CachedPerSz()
    {
        var szcli = new FakeSzcliRunner { Respond = a => new SzcliResult(0, $"GPU паспорт {a[2]}") };
        var vm = new HardwareTabViewModel(szcli);
        await vm.RefreshAsync("161432", default);
        Assert.Equal("GPU паспорт 161432", vm.Text);
        Assert.Equal(new[] { "hw", "passport", "161432" }, szcli.Calls.Single());

        vm.Clear();
        await vm.RefreshAsync("161432", default);
        Assert.Single(szcli.Calls);                 // тот же паспорт — из кэша

        await vm.ReloadCommand.ExecuteAsync(null);
        Assert.Equal(2, szcli.Calls.Count);         // «обновить» — снимает заново
    }

    [Fact]
    public async Task Failure_ShowsCodeAndOutput_NotCached()
    {
        var szcli = new FakeSzcliRunner { Respond = _ => new SzcliResult(1, "СЗ 161432 не найдена") };
        var vm = new HardwareTabViewModel(szcli);
        await vm.RefreshAsync("161432", default);
        Assert.StartsWith("паспорт не снялся (код 1)", vm.Text);
        await vm.RefreshAsync("161432", default);
        Assert.Equal(2, szcli.Calls.Count);
    }
}
```
`tests/SzDiag.Desk.Tests/ActionsViewModelTests.cs`:
```csharp
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class ActionsViewModelTests
{
    private readonly FakeSzcliRunner _szcli = new();

    private async Task<ActionsViewModel> New()
    {
        var vm = new ActionsViewModel(_szcli);
        await vm.RefreshAsync("161432", default);
        return vm;
    }

    [Fact]
    public async Task DiagRun_WithSections()
    {
        var vm = await New();
        vm.DiagSections = "storage, reboots";
        await vm.DiagRunCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "diag", "run", "161432", "storage", "reboots" }, _szcli.Calls.Single());
        Assert.Contains("[код выхода 0]", vm.Log);
    }

    [Fact]
    public async Task TestRun_NeedsConfigLabel()
    {
        // Метка обязательна, как в CLI: без неё «профиль против стока» через неделю нечитаем.
        var vm = await New();
        Assert.False(vm.TestRunCommand.CanExecute(null));
        vm.TestConfig = "EXPO 6000";
        vm.TestFilter = "occt";
        Assert.True(vm.TestRunCommand.CanExecute(null));
        await vm.TestRunCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "test", "run", "161432", "occt", "--config", "EXPO 6000" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task Close_RunsOnlyAfterConfirm()
    {
        var vm = await New();
        vm.CloseCommand.Execute(null);
        Assert.Empty(_szcli.Calls);
        Assert.Contains("Закрыть СЗ 161432", vm.PendingConfirm);

        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "close", "161432" }, _szcli.Calls.Single());
        Assert.Null(vm.PendingConfirm);
    }

    [Fact]
    public async Task Unfreeze_RunsOnlyAfterConfirm_CancelDropsIt()
    {
        var vm = await New();
        vm.UnfreezeCommand.Execute(null);
        vm.CancelConfirmCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Empty(_szcli.Calls);

        vm.UnfreezeCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "unfreeze", "161432" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task CloseForce_NeedsReason()
    {
        var vm = await New();
        Assert.False(vm.CloseForceCommand.CanExecute(null));
        vm.CloseReason = "клиент забрал машину";
        vm.CloseForceCommand.Execute(null);
        await vm.ConfirmCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "close", "161432", "--force", "клиент забрал машину" }, _szcli.Calls.Single());
    }

    [Fact]
    public async Task Note_ClearedOnSuccess_KeptOnFailure()
    {
        var vm = await New();
        vm.NoteText = "свап БП на 750W";
        await vm.NoteCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "note", "161432", "свап БП на 750W" }, _szcli.Calls.Single());
        Assert.Equal("", vm.NoteText);

        _szcli.Respond = _ => new SzcliResult(1, "hub не принял");
        vm.NoteText = "ещё";
        await vm.NoteCommand.ExecuteAsync(null);
        Assert.Equal("ещё", vm.NoteText);
        Assert.Equal(1, vm.LastExitCode);
    }

    [Fact]
    public async Task WhileRunning_OtherActionsIgnored()
    {
        _szcli.Gate = new TaskCompletionSource();
        var vm = await New();
        var first = vm.SzFetchCommand.ExecuteAsync(null);
        Assert.True(vm.Running);
        Assert.False(vm.IsIdle);
        await vm.FreezeCommand.ExecuteAsync(null);
        Assert.Single(_szcli.Calls);
        _szcli.Gate.SetResult();
        await first;
        Assert.True(vm.IsIdle);
    }

    [Fact]
    public async Task NoSzYet_NothingRuns()
    {
        var vm = new ActionsViewModel(_szcli);
        await vm.FreezeCommand.ExecuteAsync(null);
        Assert.Empty(_szcli.Calls);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~HardwareTab|FullyQualifiedName~ActionsViewModel"`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/Inspector/HardwareTabViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Паспорт железа — `szcli hw passport &lt;сз&gt;` (скрипт живёт в CLI). Снимается при
/// первом открытии вкладки и кнопкой: exec к клиенту, а железо за заявку не меняется само.</summary>
public sealed partial class HardwareTabViewModel(ISzcliRunner szcli) : ObservableObject, IInspectorTab
{
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private string? _sz;

    public string Title => "Железо";
    public TimeSpan? Interval => null;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _busy;

    public async Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        if (_cache.TryGetValue(sz, out var cached))
        {
            Text = cached;
            return;
        }
        await LoadAsync(sz, ct);
    }

    [RelayCommand]
    private Task Reload()
    {
        if (_sz is not { } sz) return Task.CompletedTask;
        _cache.Remove(sz);
        return LoadAsync(sz, CancellationToken.None);
    }

    private async Task LoadAsync(string sz, CancellationToken ct)
    {
        Busy = true;
        Text = "снимаю паспорт (szcli hw passport)…";
        var r = await szcli.RunAsync(new[] { "hw", "passport", sz }, ct);
        Busy = false;
        if (_sz != sz) return;   // пока снимали, выбрали другую СЗ
        if (r.ExitCode == 0)
        {
            _cache[sz] = r.Output;
            Text = r.Output;
        }
        else
        {
            Text = $"паспорт не снялся (код {r.ExitCode}):\n{r.Output}";
        }
    }

    public void Clear()
    {
        _sz = null;
        Text = "";
    }
}
```
`src/SzDiag.Desk/ViewModels/Inspector/ActionsViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels.Inspector;

/// <summary>Действия по СЗ — те же команды szcli, что в терминале (спека, «Действия инспектора»;
/// почему подпроцессом — см. <see cref="SzcliRunner"/>). Разрушительное (close, unfreeze) — только
/// после подтверждения. Пока идёт одна команда, остальные не запускаются.</summary>
public sealed partial class ActionsViewModel(ISzcliRunner szcli) : ObservableObject, IInspectorTab
{
    private string? _sz;
    private IReadOnlyList<string>? _pendingArgs;
    private CancellationTokenSource? _cts;

    public string Title => "Действия";
    public TimeSpan? Interval => null;

    [ObservableProperty] private string _diagSections = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TestRunCommand))] private string _testConfig = "";
    [ObservableProperty] private string _testFilter = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(NoteCommand))] private string _noteText = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(CloseForceCommand))] private string _closeReason = "";
    [ObservableProperty] private string _log = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _running;
    [ObservableProperty] private string? _pendingConfirm;
    [ObservableProperty] private int? _lastExitCode;

    public bool IsIdle => !Running;

    public Task RefreshAsync(string sz, CancellationToken ct)
    {
        _sz = sz;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task DiagRun()
        => Run(Args("diag", "run").Concat(Split(DiagSections)).ToList());

    private bool CanTestRun() => TestConfig.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanTestRun))]
    private Task TestRun()
    {
        var args = Args("test", "run").ToList();
        if (TestFilter.Trim() is { Length: > 0 } filter) args.Add(filter);
        args.Add("--config");
        args.Add(TestConfig.Trim());
        return Run(args);
    }

    [RelayCommand]
    private Task Freeze() => Run(Args("freeze").ToList());

    [RelayCommand]
    private void Unfreeze()
        => Ask($"Снять заморозку Windows Update с СЗ {_sz}? Обновления снова смогут перезагрузить машину.",
            Args("unfreeze").ToList());

    private bool CanNote() => NoteText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanNote))]
    private async Task Note()
    {
        await Run(Args("note").Append(NoteText.Trim()).ToList());
        if (LastExitCode == 0) NoteText = "";
    }

    [RelayCommand]
    private Task SzFetch() => Run(Args("sz", "fetch").ToList());

    [RelayCommand]
    private void Close()
        => Ask($"Закрыть СЗ {_sz}? Доступ на клиенте откатится, сессия Claude уйдёт в архив.", Args("close").ToList());

    private bool CanCloseForce() => CloseReason.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanCloseForce))]
    private void CloseForce()
        => Ask($"Закрыть СЗ {_sz} принудительно, мимо защит close? Причина уйдёт в журнал.",
            Args("close").Concat(new[] { "--force", CloseReason.Trim() }).ToList());

    [RelayCommand]
    private Task Confirm()
    {
        var args = _pendingArgs;
        _pendingArgs = null;
        PendingConfirm = null;
        return args is null ? Task.CompletedTask : Run(args);
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        _pendingArgs = null;
        PendingConfirm = null;
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Ask(string question, IReadOnlyList<string> args)
    {
        if (_sz is null) return;
        _pendingArgs = args;
        PendingConfirm = question;
    }

    /// <summary>Номер СЗ вставляется сразу после подкоманды: `szcli diag run &lt;сз&gt;`,
    /// `szcli sz fetch &lt;сз&gt;`, `szcli close &lt;сз&gt;`.</summary>
    private IEnumerable<string> Args(params string[] command) => command.Append(_sz ?? "");

    private static IEnumerable<string> Split(string s)
        => s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task Run(IReadOnlyList<string> args)
    {
        if (_sz is null || Running) return;
        Running = true;
        _cts = new CancellationTokenSource();
        var shown = "> szcli " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        Log = shown + "\n…";
        try
        {
            var r = await szcli.RunAsync(args, _cts.Token);
            LastExitCode = r.ExitCode;
            Log = $"{shown}\n{r.Output}\n[код выхода {r.ExitCode}]";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Running = false;
        }
    }

    public void Clear()
    {
        _sz = null;
        _pendingArgs = null;
        PendingConfirm = null;
        Log = "";
        LastExitCode = null;
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter "FullyQualifiedName~HardwareTab|FullyQualifiedName~ActionsViewModel"`
Expected: 10 passed.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels/Inspector/HardwareTabViewModel.cs src/SzDiag.Desk/ViewModels/Inspector/ActionsViewModel.cs tests/SzDiag.Desk.Tests/HardwareTabViewModelTests.cs tests/SzDiag.Desk.Tests/ActionsViewModelTests.cs
git commit -m "feat(desk): паспорт железа и действия по СЗ через szcli"
```

---

### Task 11: Представления инспектора и сборка

**Files:**
- Create: `src/SzDiag.Desk/Views/Inspector/{RebootsTabView,JobsTabView,SensorsTabView,JournalTabView,HardwareTabView,ActionsView}.axaml(.cs)`, `tests/SzDiag.Desk.Tests/InspectorSmokeTests.cs`
- Modify: `src/SzDiag.Desk/ViewModels/Inspector/InspectorViewModel.cs` (фабрика), `src/SzDiag.Desk/Views/InspectorView.axaml`, `src/SzDiag.Desk/Views/SzListView.axaml`, `src/SzDiag.Desk/Views/MainWindow.axaml.cs`, `src/SzDiag.Desk/App.axaml.cs`

**Interfaces:**
- Consumes: все вкладки (задачи 5–10), `DeskTools`, `SzcliRunner`, `FreezeProbe` (задача 4), `DeskClaudeHost.Szcli` (часть 2).
- Produces: `static InspectorViewModel InspectorViewModel.Create(DeskTools tools, TimeProvider time)` и свойства `Reboots`, `Jobs`, `Sensors`, `Journal`, `Hardware`, `Actions`; `x:Name="InspectorTabs"` (TabControl), `x:Name="FrozenBadge"` в шаблоне карточки.

- [ ] **Step 1: Фабрика и failing smoke tests**

В `InspectorViewModel.cs` — свойства и фабрика (индексы вкладок совпадают с порядком `TabItem` в `InspectorView.axaml`):
```csharp
    public RebootsTabViewModel? Reboots { get; private init; }
    public JobsTabViewModel? Jobs { get; private init; }
    public SensorsTabViewModel? Sensors { get; private init; }
    public JournalTabViewModel? Journal { get; private init; }
    public HardwareTabViewModel? Hardware { get; private init; }
    public ActionsViewModel? Actions { get; private init; }

    /// <summary>Порядок — как у вкладок в окне: Обзор, Вырубоны, Задачи, Сенсоры, Журнал, Железо, Действия.</summary>
    public static InspectorViewModel Create(DeskTools tools, TimeProvider time)
    {
        var reboots = new RebootsTabViewModel(tools.Api);
        var jobs = new JobsTabViewModel(tools.Api);
        var sensors = new SensorsTabViewModel(tools.Api, tools.Szcli, time);
        var journal = new JournalTabViewModel(tools.Kb, tools.Ui);
        var hardware = new HardwareTabViewModel(tools.Szcli);
        var actions = new ActionsViewModel(tools.Szcli);
        return new InspectorViewModel(new IInspectorTab?[] { null, reboots, jobs, sensors, journal, hardware, actions }, time)
        {
            Reboots = reboots, Jobs = jobs, Sensors = sensors, Journal = journal, Hardware = hardware, Actions = actions,
        };
    }
```
`tests/SzDiag.Desk.Tests/InspectorSmokeTests.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.ViewModels.Inspector;
using SzDiag.Desk.Views;
using SzDiag.Kb;

namespace SzDiag.Desk.Tests;

public class InspectorSmokeTests
{
    private static (MainWindow W, MainViewModel Vm) Open(FakeHubApi api, FakeSzcliRunner szcli, string kbRoot)
    {
        var sessions = new[] { new SessionInfo("161432", "10.0.0.5", "PC", SessionStatus.Online, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        api.Sessions = () => sessions;
        var inspector = InspectorViewModel.Create(new DeskTools(api, szcli, new KbPaths(kbRoot), a => a()), TimeProvider.System);
        var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), new DeskUiState(), TimeProvider.System,
            null, inspector);
        var w = new MainWindow(vm);
        w.Show();
        vm.Apply(HubSnapshot.Empty with { SessionsOkAt = DateTimeOffset.UtcNow, Sessions = sessions });
        return (w, vm);
    }

    [AvaloniaFact]
    public void TabsRender_RebootsTabLoads()
    {
        var api = new FakeHubApi
        {
            Reboots = sz => new RebootTimeline(sz, new[]
            {
                new RebootEvent(sz, DateTimeOffset.UtcNow, null, null, 600, "OCCT", ShutdownKind.HardOff),
            }, 600),
        };
        var (w, vm) = Open(api, new FakeSzcliRunner(), Path.GetTempPath());
        vm.Selected = vm.Items.Single();
        var tabs = w.FindControl<TabControl>("InspectorTabs")!;
        Assert.Equal(7, tabs.ItemCount);

        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Inspector!.Reboots!.Rows);
    }

    [AvaloniaFact]
    public void ActionsTab_ConfirmBeforeClose()
    {
        var szcli = new FakeSzcliRunner();
        var (w, vm) = Open(new FakeHubApi(), szcli, Path.GetTempPath());
        vm.Selected = vm.Items.Single();
        w.FindControl<TabControl>("InspectorTabs")!.SelectedIndex = 6;
        Dispatcher.UIThread.RunJobs();

        vm.Inspector!.Actions!.CloseCommand.Execute(null);
        Assert.NotNull(vm.Inspector.Actions.PendingConfirm);
        Assert.Empty(szcli.Calls);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~InspectorSmoke`
Expected: падение — `InspectorTabs` не найден (вкладок в окне ещё нет).

- [ ] **Step 3: Представления вкладок**

У каждого `Views/Inspector/*.axaml.cs` — стандартный partial:
```csharp
using Avalonia.Controls;

namespace SzDiag.Desk.Views.Inspector;

public partial class RebootsTabView : UserControl
{
    public RebootsTabView() => InitializeComponent();
}
```
(то же для `JobsTabView`, `SensorsTabView`, `JournalTabView`, `HardwareTabView`, `ActionsView`.)

`src/SzDiag.Desk/Views/Inspector/RebootsTabView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels.Inspector"
             x:Class="SzDiag.Desk.Views.Inspector.RebootsTabView"
             x:DataType="vm:RebootsTabViewModel">
  <DockPanel>
    <TextBlock DockPanel.Dock="Top" Text="{Binding Summary}" Classes="secondary" TextWrapping="Wrap" Margin="0,0,0,8" />
    <ScrollViewer>
      <ItemsControl ItemsSource="{Binding Rows}">
        <ItemsControl.ItemTemplate>
          <DataTemplate x:DataType="vm:RebootRow">
            <Border Background="{StaticResource Bg.Panel}" CornerRadius="8" Padding="9,6" Margin="0,0,0,4">
              <StackPanel Spacing="2">
                <StackPanel Orientation="Horizontal" Spacing="8">
                  <TextBlock Text="{Binding When}" FontWeight="SemiBold" />
                  <TextBlock Text="{Binding Kind}" Foreground="{StaticResource Bad}" IsVisible="{Binding IsFailure}" />
                  <TextBlock Text="{Binding Kind}" Classes="secondary" IsVisible="{Binding !IsFailure}" />
                </StackPanel>
                <TextBlock Text="{Binding Detail}" Classes="secondary" TextWrapping="Wrap" />
              </StackPanel>
            </Border>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
      </ItemsControl>
    </ScrollViewer>
  </DockPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/Inspector/JobsTabView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels.Inspector"
             x:Class="SzDiag.Desk.Views.Inspector.JobsTabView"
             x:DataType="vm:JobsTabViewModel">
  <DockPanel>
    <TextBlock DockPanel.Dock="Top" Text="{Binding Message}" Classes="secondary" TextWrapping="Wrap"
               IsVisible="{Binding Message, Converter={x:Static StringConverters.IsNotNullOrEmpty}}" Margin="0,0,0,6" />
    <ListBox DockPanel.Dock="Top" ItemsSource="{Binding Rows}" SelectedItem="{Binding Selected}" MaxHeight="220"
             Background="Transparent">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:JobRow">
          <StackPanel Spacing="1">
            <TextBlock Text="{Binding Id}" FontFamily="{StaticResource MonoFont}" FontWeight="SemiBold" />
            <TextBlock Text="{Binding State}" Classes="secondary" TextWrapping="Wrap" />
            <TextBlock Text="{Binding Script}" Classes="secondary" FontFamily="{StaticResource MonoFont}"
                       TextTrimming="CharacterEllipsis"
                       IsVisible="{Binding Script, Converter={x:Static ObjectConverters.IsNotNull}}" />
          </StackPanel>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
    <ScrollViewer Margin="0,8,0,0">
      <SelectableTextBlock Text="{Binding Output}" FontFamily="{StaticResource MonoFont}" FontSize="11" TextWrapping="Wrap" />
    </ScrollViewer>
  </DockPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/Inspector/SensorsTabView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels.Inspector"
             x:Class="SzDiag.Desk.Views.Inspector.SensorsTabView"
             x:DataType="vm:SensorsTabViewModel">
  <StackPanel Spacing="6">
    <TextBlock Text="{Binding Status}" Classes="secondary" TextWrapping="Wrap" />
    <Button Content="Запустить наблюдатель (sensors start)" Command="{Binding StartSensorsCommand}"
            IsVisible="{Binding NotWriting}" HorizontalAlignment="Left" />
    <StackPanel Spacing="3" IsVisible="{Binding HasData}">
      <TextBlock Text="{Binding CpuText}" />
      <TextBlock Text="{Binding GpuText}" />
      <TextBlock Text="{Binding PowerText}" Classes="secondary" />
      <TextBlock Text="{Binding VoltText}" Classes="secondary" />
    </StackPanel>
    <!-- Пустой график не рисуется (спека): ломаная из одной точки ничего не говорит. -->
    <StackPanel Spacing="2" IsVisible="{Binding HasCpuLine}">
      <TextBlock Classes="caps" Text="ТЕМПЕРАТУРА CPU" />
      <Border Background="{StaticResource Bg.Panel}" CornerRadius="8" Padding="6" Width="272" HorizontalAlignment="Left">
        <Polyline Points="{Binding CpuTempLine}" Stroke="{StaticResource Warn}" StrokeThickness="1.4" Height="56" Width="260" />
      </Border>
    </StackPanel>
    <StackPanel Spacing="2" IsVisible="{Binding HasGpuLine}">
      <TextBlock Classes="caps" Text="ТЕМПЕРАТУРА GPU" />
      <Border Background="{StaticResource Bg.Panel}" CornerRadius="8" Padding="6" Width="272" HorizontalAlignment="Left">
        <Polyline Points="{Binding GpuTempLine}" Stroke="{StaticResource Accent}" StrokeThickness="1.4" Height="56" Width="260" />
      </Border>
    </StackPanel>
  </StackPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/Inspector/JournalTabView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels.Inspector"
             x:Class="SzDiag.Desk.Views.Inspector.JournalTabView"
             x:DataType="vm:JournalTabViewModel">
  <DockPanel>
    <TextBlock DockPanel.Dock="Top" Text="{Binding FilePath}" Classes="secondary" FontSize="10"
               TextTrimming="CharacterEllipsis" Margin="0,0,0,6" />
    <ScrollViewer x:Name="JournalScroll">
      <SelectableTextBlock Text="{Binding Text}" FontFamily="{StaticResource MonoFont}" FontSize="11" TextWrapping="Wrap" />
    </ScrollViewer>
  </DockPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/Inspector/HardwareTabView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels.Inspector"
             x:Class="SzDiag.Desk.Views.Inspector.HardwareTabView"
             x:DataType="vm:HardwareTabViewModel">
  <DockPanel>
    <Button DockPanel.Dock="Top" Content="Снять заново" Command="{Binding ReloadCommand}"
            IsEnabled="{Binding !Busy}" HorizontalAlignment="Left" Margin="0,0,0,8" />
    <ScrollViewer>
      <SelectableTextBlock Text="{Binding Text}" FontFamily="{StaticResource MonoFont}" FontSize="11" TextWrapping="Wrap" />
    </ScrollViewer>
  </DockPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/Inspector/ActionsView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels.Inspector"
             x:Class="SzDiag.Desk.Views.Inspector.ActionsView"
             x:DataType="vm:ActionsViewModel">
  <ScrollViewer>
    <StackPanel Spacing="10">
      <!-- Подтверждение разрушительного действия: одно на всю вкладку, поверх кнопок. -->
      <Border IsVisible="{Binding PendingConfirm, Converter={x:Static ObjectConverters.IsNotNull}}"
              BorderBrush="{StaticResource Warn}" BorderThickness="1" Background="#1fff9f0a" CornerRadius="10" Padding="10,8">
        <StackPanel Spacing="6">
          <TextBlock Text="{Binding PendingConfirm}" TextWrapping="Wrap" />
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Content="Да" Command="{Binding ConfirmCommand}" />
            <Button Content="Нет" Command="{Binding CancelConfirmCommand}" />
          </StackPanel>
        </StackPanel>
      </Border>

      <StackPanel Spacing="10" IsEnabled="{Binding IsIdle}">
        <StackPanel Spacing="4">
          <TextBlock Classes="caps" Text="ДИАГНОСТИКА" />
          <TextBox Text="{Binding DiagSections}" Watermark="секции через пробел (пусто — все)" />
          <Button Content="diag run" Command="{Binding DiagRunCommand}" />
        </StackPanel>
        <StackPanel Spacing="4">
          <TextBlock Classes="caps" Text="ПРОГОН" />
          <TextBox Text="{Binding TestConfig}" Watermark="метка конфигурации (обязательно): EXPO 6000 / сток" />
          <TextBox Text="{Binding TestFilter}" Watermark="фильтр тестов (необязательно)" />
          <Button Content="test run" Command="{Binding TestRunCommand}" />
        </StackPanel>
        <StackPanel Spacing="4">
          <TextBlock Classes="caps" Text="WINDOWS UPDATE" />
          <StackPanel Orientation="Horizontal" Spacing="8">
            <Button Content="freeze" Command="{Binding FreezeCommand}" />
            <Button Content="unfreeze…" Command="{Binding UnfreezeCommand}" />
          </StackPanel>
        </StackPanel>
        <StackPanel Spacing="4">
          <TextBlock Classes="caps" Text="ЗАМЕТКА В ЖУРНАЛ" />
          <TextBox Text="{Binding NoteText}" Watermark="свап БП, правка BIOS, осмотр…" AcceptsReturn="False" />
          <Button Content="записать" Command="{Binding NoteCommand}" />
        </StackPanel>
        <StackPanel Spacing="4">
          <TextBlock Classes="caps" Text="ЗАЯВКА" />
          <Button Content="sz fetch (данные из ERP, несколько минут)" Command="{Binding SzFetchCommand}" />
          <Button Content="close…" Command="{Binding CloseCommand}" />
          <TextBox Text="{Binding CloseReason}" Watermark="причина для close --force" />
          <Button Content="close --force…" Command="{Binding CloseForceCommand}" />
        </StackPanel>
      </StackPanel>

      <Button Content="прервать команду" Command="{Binding CancelCommand}" IsVisible="{Binding Running}" HorizontalAlignment="Left" />
      <SelectableTextBlock Text="{Binding Log}" FontFamily="{StaticResource MonoFont}" FontSize="11" TextWrapping="Wrap" />
    </StackPanel>
  </ScrollViewer>
</UserControl>
```

- [ ] **Step 4: Инспектор в окне, бейдж, сборка**

`src/SzDiag.Desk/Views/InspectorView.axaml` — полная замена:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             xmlns:v="using:SzDiag.Desk.Views"
             xmlns:iv="using:SzDiag.Desk.Views.Inspector"
             x:Class="SzDiag.Desk.Views.InspectorView"
             x:DataType="vm:MainViewModel">
  <UserControl.Styles>
    <!-- 7 вкладок в 290 px: мелкий шрифт и перенос заголовков в две строки. -->
    <Style Selector="TabItem">
      <Setter Property="FontSize" Value="12" />
      <Setter Property="MinHeight" Value="0" />
      <Setter Property="Padding" Value="8,4" />
    </Style>
  </UserControl.Styles>
  <Panel>
    <TextBlock IsVisible="{Binding Selected, Converter={x:Static ObjectConverters.IsNull}}"
               Text="Выбери СЗ слева" Classes="secondary" HorizontalAlignment="Center" VerticalAlignment="Center" />
    <TabControl x:Name="InspectorTabs" Margin="8,6"
                IsVisible="{Binding Selected, Converter={x:Static ObjectConverters.IsNotNull}}"
                SelectedIndex="{Binding Inspector.SelectedIndex, FallbackValue=0}">
      <TabControl.ItemsPanel>
        <ItemsPanelTemplate>
          <WrapPanel />
        </ItemsPanelTemplate>
      </TabControl.ItemsPanel>
      <TabItem Header="Обзор">
        <Border Background="{StaticResource Bg.Panel}" CornerRadius="10" Padding="11" Margin="0,6,0,0" VerticalAlignment="Top">
          <StackPanel Spacing="4">
            <TextBlock Text="{Binding Selected.Sz}" FontWeight="SemiBold" FontSize="14" />
            <TextBlock Text="{Binding Selected.Liveness, Converter={x:Static v:Converters.LivenessToText}}"
                       Foreground="{Binding Selected.Liveness, Converter={x:Static v:Converters.LivenessToBrush}}" />
            <TextBlock Classes="secondary" Text="Windows Update заморожен — не забудь unfreeze" TextWrapping="Wrap"
                       IsVisible="{Binding Selected.IsFrozen, FallbackValue=False}" />
            <TextBlock Classes="secondary" Text="{Binding Selected.Info.Hostname, StringFormat='хост: {0}'}" />
            <TextBlock Classes="secondary" Text="{Binding Selected.Info.LanIp, StringFormat='LAN: {0}'}" />
            <TextBlock Classes="secondary" Text="{Binding Selected.Info.AccessMode, StringFormat='доступ: {0}', TargetNullValue='доступ: direct'}" />
            <TextBlock Classes="secondary" Text="{Binding Selected.BootTimeLocal, StringFormat='загрузка: {0:dd.MM HH:mm}'}" />
            <TextBlock Classes="secondary" Text="{Binding Selected.RebootCount, StringFormat='вырубонов за сессию: {0}'}" />
            <TextBlock Classes="secondary" TextWrapping="Wrap" Text="{Binding Selected.Info.Activity, StringFormat='занята: {0}'}" />
          </StackPanel>
        </Border>
      </TabItem>
      <TabItem Header="Вырубоны"><iv:RebootsTabView DataContext="{Binding Inspector.Reboots}" Margin="0,6,0,0" /></TabItem>
      <TabItem Header="Задачи"><iv:JobsTabView DataContext="{Binding Inspector.Jobs}" Margin="0,6,0,0" /></TabItem>
      <TabItem Header="Сенсоры"><iv:SensorsTabView DataContext="{Binding Inspector.Sensors}" Margin="0,6,0,0" /></TabItem>
      <TabItem Header="Журнал"><iv:JournalTabView DataContext="{Binding Inspector.Journal}" Margin="0,6,0,0" /></TabItem>
      <TabItem Header="Железо"><iv:HardwareTabView DataContext="{Binding Inspector.Hardware}" Margin="0,6,0,0" /></TabItem>
      <TabItem Header="Действия"><iv:ActionsView DataContext="{Binding Inspector.Actions}" Margin="0,6,0,0" /></TabItem>
    </TabControl>
  </Panel>
</UserControl>
```
`src/SzDiag.Desk/Views/SzListView.axaml` — в шаблоне карточки `Grid ColumnDefinitions="Auto,*,Auto,Auto"` → `"Auto,*,Auto,Auto,Auto"`, точку сессии перенести в `Grid.Column="4"`, в `Grid.Column="3"` добавить:
```xml
            <TextBlock x:Name="FrozenBadge" Grid.Column="3" Text="🧊" FontSize="11" Margin="6,0,0,0"
                       VerticalAlignment="Center" IsVisible="{Binding IsFrozen}"
                       ToolTip.Tip="Windows Update заморожен — не забудь unfreeze до close" />
```
`src/SzDiag.Desk/Views/MainWindow.axaml.cs` — в `Opened` после запуска опроса hub: `_ = vm.Inspector?.RunLoopAsync(_stop.Token);`.

`src/SzDiag.Desk/App.axaml.cs` — после создания `chat`:
```csharp
            var szcli = new Func<string?>(() => claude.Szcli);
            var kbRoot = Path.IsPathRooted(opts.KbRoot) ? opts.KbRoot : Path.Combine(AppContext.BaseDirectory, opts.KbRoot);
            var tools = new DeskTools(api, new SzcliRunner(szcli), new KbPaths(kbRoot), a => Dispatcher.UIThread.Post(a));
            var inspector = InspectorViewModel.Create(tools, TimeProvider.System);
```
и `new MainViewModel(new HubPoller(api, TimeProvider.System), ui, TimeProvider.System, chat, inspector, new FreezeProbe(szcli))` (+ `using SzDiag.Desk.ViewModels.Inspector;`, `using SzDiag.Kb;`).

- [ ] **Step 5: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное, включая 2 новых дымовых и дымовые тесты частей 1–2.

- [ ] **Step 6: Живая проверка**

Временный hub из сборки на `127.0.0.1:5188` (свои kb/db в scratchpad, `Hub__KbBackup__Enabled=false`, как в частях 1–2) и Desk из `bin` с `appsettings.json` на него. Проверить глазами и скриншотом:
1. Статусбар: «агент … · kb: бэкап выключен» (детали из `/api/status`).
2. СЗ нет — инспектор «Выбери СЗ слева». Вкладки на живой СЗ — в чек-лист (задача 12).
3. 7 вкладок помещаются в 290 px (перенос в две строки допустим); переключение не дёргает exec (в логе hub нет запросов `/exec`, пока открыт «Обзор»).
После проверки остановить hub и убрать временные файлы из `bin`.

- [ ] **Step 7: Commit**

```bash
git add src/SzDiag.Desk tests/SzDiag.Desk.Tests/InspectorSmokeTests.cs
git commit -m "feat(desk): инспектор в окне — вкладки, действия, бейдж заморозки"
```

---

### Task 12: Документация, спека, чек-лист

**Files:**
- Modify: `CLAUDE.md`, `docs/dev-knowledge-base.md`, `docs/superpowers/specs/2026-09-25-desk-gui-design.md`
- Create: `docs/live-checklist-2026-09-25-desk-part3.md`

- [ ] **Step 1: Спека**

В `docs/superpowers/specs/2026-09-25-desk-gui-design.md`:
- раздел «Действия инспектора»: заменить «Действия идут через `SzDiag.HubClient` теми же вызовами, что CLI» на «Действия и паспорт железа — подпроцессом `szcli` (`SzcliRunner`): `freeze` хранит прежние значения рядом с szcli, у `close` свои защиты, у `sz fetch` своя сессия захвата — вторая реализация в Desk разошлась бы с первой. Вывод показывается как есть (`NO_COLOR=1`), журнал СЗ ведёт hub.»;
- таблица «Источники данных GUI», строка «сенсоры»: «синхронный exec хвоста `C:\OCCT\sensors.csv` (`SensorPaths.LhmCsv`) раз в 5 с, пока вкладка открыта; «не пишутся» — последняя строка не менялась 30 с по часам бокса (часы клиента в WinPE сдвинуты)»;
- строка «железо»: «`szcli hw passport` при первом открытии, кэш на СЗ, кнопка «снять заново»»;
- статус: «этапы 1–5 реализованы (планы частей 1–3), этап 6 — часть 4».

- [ ] **Step 2: CLAUDE.md и база знаний**

`CLAUDE.md`, пункт `SzDiag.Desk` — дописать: «Справа — инспектор выбранной СЗ: вкладки Обзор / Вырубоны / Задачи / Сенсоры / Журнал / Железо / Действия (опрашивается только видимая); действия и паспорт — подпроцессом `szcli`; бейдж `🧊` — заморозка WU. Статусбар — `/healthz` + `/api/status` (пакет агента, бэкап kb, туннель).»

`docs/dev-knowledge-base.md`: в разделе протокола — `GET /api/status` (`HubStatus`: `AgentPackageVersion` из `agent-dist/version.txt`, `KbBackup` — `Enabled/LastRunAt/Outcome/Message`, `Tunnel` — `State` из `TunnelStates`, `Since`; отчитываются `KbBackupService`/`HubTunnelService` в `HubStatusTracker`); в разделе «Сессии Claude в Desk» — подраздел «Инспектор»: вкладки и их источники/частоты, `IInspectorTab`, опрос только видимой, `SzcliRunner` (`cli\SzDiag.Cli.exe` рядом с `szcli.cmd`, коды `-1` не найден / `-2` прервано), `FreezeProbe` (`cli\freeze\<СЗ>.json`), `CliXml` и `SensorPaths` теперь в HubClient/Contracts.

- [ ] **Step 3: Живой чек-лист**

`docs/live-checklist-2026-09-25-desk-part3.md`:
```markdown
# Живой чек-лист: SzDiag Desk, часть 3 — инспектор (2026-09-25)

- [ ] Статусбар: «агент <версия> · kb HH:MM ✓ · туннель ✓» на боевом hub; при сломанном push kb — оранжевым «kb: не выгружен в remote».
- [ ] Вырубоны: таймлайн совпадает с `szcli reboots <СЗ>`; после вырубона под тестом вкладка перечиталась сама (⚡N вырос).
- [ ] Задачи: фоновая задача `exec --detach` видна со строкой скрипта; хвост вывода выбранной обновляется каждые 3 с.
- [ ] Задачи под OCCT: «агент не ответил…», список не очищается, окно не виснет.
- [ ] Сенсоры: при запущенном `lhmmon` — температуры и ломаные; `lhmmon` остановлен → через 30 с «сенсоры не пишутся» + кнопка запуска работает.
- [ ] Журнал: `szcli note <СЗ> "…"` из терминала появляется во вкладке без перезапуска.
- [ ] Железо: паспорт совпадает с `szcli hw passport <СЗ>`; повторное открытие — мгновенно (кэш).
- [ ] Действия: `freeze` → бейдж 🧊 на карточке ≤ 2 с; `unfreeze` спрашивает подтверждение; `close` без наблюдения — отказ с текстом CLI, `close --force` с причиной — закрывает; `sz fetch` — вывод CLI целиком.
- [ ] Пока открыт «Обзор», в логе hub нет запросов `/api/sessions/<СЗ>/exec` от Desk.
```

- [ ] **Step 4: Полный прогон**

Run: `"C:\Program Files\dotnet\dotnet.exe" build` и `"C:\Program Files\dotnet\dotnet.exe" test --filter "FullyQualifiedName!~AllRepoClientRecipes_NoFalseConcatWarning"`
Expected: сборка без ошибок и без новых предупреждений; все тесты зелёные.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md docs/dev-knowledge-base.md docs/superpowers/specs/2026-09-25-desk-gui-design.md docs/live-checklist-2026-09-25-desk-part3.md
git commit -m "docs(desk): инспектор — спека, база знаний, живой чек-лист"
```

---

## Дальше

- **Часть 4** — `ask_peer`/`peers` в `DeskMcpServer` (сначала выжимка kb соседа, живой вопрос — глубина 1, 20 в час, таймаут 5 минут), фиолетовая метка пары в списке, бейдж `≈` «похожая СЗ» по паспорту железа (CPU, плата, профиль/частота памяти — кэш `hw passport` из вкладки «Железо»).
