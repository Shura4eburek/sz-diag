# SzDiag Desk, часть 2 — сессии Claude и чат — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** В центре окна Desk — чат с сессией Claude Code, привязанной к выбранной СЗ (1 СЗ = 1 сессия): лента с текстом, карточками инструментов и разрешений, очередь сообщений, «■ стоп», `--resume`, «открыть в терминале», архив закрытых СЗ, токены за день в статусбаре.

**Architecture:** Новое ядро `SzDiag.Claude` (без UI и без знания о СЗ — ключ сессии просто строка): парсер stream-json на фикстурах спайка, обвязка процесса `claude`, `ClaudeSession` (состояния, очередь, прерывание, журнал), `SessionManager`, `PermissionBroker` и MCP-сервер `DeskMcpServer` с тулзой `permission_prompt`. Desk получает `DeskClaudeHost` (сборка ядра под конфиг Desk), модели ленты и чата, и представления. Всё про СЗ (вводная для Claude, заметки о вырубонах, архив по закрытию СЗ) живёт в Desk.

**Tech Stack:** .NET 8, Avalonia 11.3.22, CommunityToolkit.Mvvm 8.4.2, ModelContextProtocol.AspNetCore 2.2.0 (streamable HTTP, stateless), Markdown.Avalonia.Tight 11.0.3, xunit 2.5.3, Avalonia.Headless.XUnit 11.3.22.

**Spec:** [docs/superpowers/specs/2026-09-25-desk-gui-design.md](../specs/2026-09-25-desk-gui-design.md) + [итоги спайка](../specs/2026-09-25-desk-spike-notes.md). Часть 1 (окно, список СЗ, статусбар, передачи) — [2026-09-25-desk-part1-window.md](2026-09-25-desk-part1-window.md), уже в ветке.

**Что покрывает этот план:** этап 4 спеки (`SzDiag.Claude` + чат). Вкладки инспектора, действия, `ask_peer`/`peers`, бейджи `🧊`/`≈` — часть 3.

**Решения плана поверх спеки** (задача 11 вносит их в спеку):
- **Процесс `claude` стартует при первом сообщении**, а не при открытии чата: `claude -p` в stream-json до первого сообщения ничего не делает, а поднятый впустую процесс — это SessionStart-хуки и память. Открытие чата только показывает ленту.
- **Очередь — у Desk**, не у `claude`: следующее сообщение уходит после `result` текущего хода, а «■» возвращает неотправленное в поле ввода (как Esc в терминальном Claude Code).
- **Лента переживает перезапуск Desk** через свой журнал `sessions\<ключ>.jsonl` рядом с exe: сырые строки stream-json (без служебных) + строки Desk (`desk_user`, `desk_note`, `desk_permission_*`, `desk_crash`). `--resume` историю в stdout не повторяет — без журнала после перезапуска лента была бы пустой.
- **Напоминание о висящем разрешении** — мигание окна в панели задач сразу и каждые 10 минут (`FlashWindowEx`), а не всплывающее уведомление Windows: у Avalonia 11 нативных тостов нет, а мигание делает то же без зависимостей.
- **Профиль `claude`** (`CLAUDE_CONFIG_DIR`) — параметр `ClaudeConfigDir` в конфиге Desk. На боксе профиль задаёт обёртка `claude2.cmd`, а не переменная пользователя: Desk, запущенный из Проводника, иначе поднял бы `claude` с чужим профилем (`~/.claude`).
- **Закрытые СЗ** — секция «АРХИВ» под списком заявок: сессии из `desk-sessions.json`, чьей СЗ больше нет в `/api/sessions`.

## Global Constraints

- Целевой фреймворк — **net8.0** во всех новых проектах; файлы `*.csproj`/`*.ps1` — **UTF-8 с BOM**.
- Комментарии и пользовательский текст — **на русском**, комментарий объясняет «почему».
- Пути конфига, журналов, `desk-sessions.json`, `desk-tokens.json`, `run\` — от `AppContext.BaseDirectory` Desk, не от CWD.
- `SzDiag.Claude` не ссылается ни на один проект решения и не знает про СЗ: ключ сессии — строка, всё про СЗ — в `SzDiag.Desk`.
- Запуск `claude` — строго: `-p --input-format stream-json --output-format stream-json --verbose --permission-mode default --permission-prompt-tool mcp__desk__permission_prompt --mcp-config <файл> --append-system-prompt <вводная> [--resume <session_id>]`; рабочий каталог — корень репозитория sz-diag.
- stdin процесса — **UTF-8 без BOM**; сообщение пользователя — `{"type":"user","message":{"role":"user","content":"…"}}`; прерывание — `{"type":"control_request","request_id":"…","request":{"subtype":"interrupt"}}`.
- В `--mcp-config` у сервера `desk` — `"timeout": 86400000` (сутки): без него `claude` обрывает `permission_prompt` после 300 с молчания (спайк).
- MCP-сервер Desk — только `127.0.0.1`, случайный порт, токен на запуск в заголовке `X-Desk-Token`, путь `/mcp/<ключ>`.
- Строго **1 ключ = 1 сессия**; автозапуска сессий нет (токены).
- Тема и раскладка — токены части 1 (`Tokens.axaml`); новые цвета не заводить.
- `dotnet` в PATH бокса (`~/.dotnet`) — только с рантаймом 10: тесты гонять через `"C:\Program Files\dotnet\dotnet.exe"`.

## Review Focus

- **Desk запущен из Проводника, `CLAUDE_CONFIG_DIR` не задан** — `claude` должен подняться с профилем из `ClaudeConfigDir`, а не с `~/.claude`. → тесты `ClaudeLaunchTests.ToStartInfo_SetsConfigDir` (задача 2), `DeskClaudeHostTests.LaunchFor_WritesMcpConfig_PassesConfigDirAndResume` (задача 6).
- **Первое же сообщение кириллицей** — BOM в stdin ломает разбор первой строки у `claude`. → `ClaudeLaunchTests.ToStartInfo_StdinUtf8WithoutBom` (задача 2).
- **Ход завис (инструмент не возвращается), оператор жмёт «■»** — окно не должно висеть: через 10 с процесс останавливается, разговор продолжается `--resume`. → `ClaudeSessionTests.Interrupt_NoResult_StopsProcessAfterTimeout` (задача 5).
- **Первый опрос передач пришёл раньше первого опроса списка СЗ** — пустой список СЗ означает «ещё не знаю», сессии нельзя отправлять в архив. → `MainViewModelChatTests.Apply_BeforeFirstSessionsPoll_ArchivesNothing` (задача 9).
- **Desk закрывают, пока висит разрешение и идёт ход** — запрос получает отказ, процессы останавливаются, ни одна сессия не помечается упавшей; остановка не блокирует UI-поток дедлоком. → `ClaudeSessionTests.StopAll_DeniesPendingAndStopsProcesses` (задача 5); остановка из `App` — через `Task.Run` (задача 10).

---

## Карта файлов

**Новое:**
- `src/SzDiag.Claude/` — `SzDiag.Claude.csproj`, `ClaudeEvents.cs`, `Json.cs`, `StreamJsonParser.cs`, `DeskLines.cs`, `ClaudeInput.cs`, `ClaudeLaunch.cs`, `ClaudeLocator.cs`, `IClaudeProcess.cs`, `ClaudeProcess.cs`, `TranscriptStore.cs`, `SessionIndex.cs`, `TokenLedger.cs`, `PermissionBroker.cs`, `DeskMcpServer.cs`, `ClaudeSession.cs`, `SessionManager.cs`.
- `tests/SzDiag.Claude.Tests/` — `SzDiag.Claude.Tests.csproj`, `Fixture.cs`, `StreamJsonParserTests.cs`, `ClaudeLaunchTests.cs`, `ClaudeProcessTests.cs`, `StoresTests.cs`, `PermissionBrokerTests.cs`, `DeskMcpServerTests.cs`, `FakeClaudeProcess.cs`, `SessionHarness.cs`, `ClaudeSessionTests.cs` (фикстуры `Fixtures/*.jsonl` уже есть).
- `src/SzDiag.Desk/Services/` — `SzBriefing.cs`, `DeskClaudeHost.cs`, `TerminalLauncher.cs`, `ChatServices.cs`, `WindowAttention.cs`.
- `src/SzDiag.Desk/ViewModels/` — `FeedItems.cs`, `ToolSummary.cs`, `FeedBuilder.cs`, `ChatViewModel.cs`, `ArchivedItemViewModel.cs`.
- `src/SzDiag.Desk/Views/ChatView.axaml(.cs)`.
- `tests/SzDiag.Desk.Tests/` — `Fixture.cs`, `FakeClaudeProcess.cs`, `ChatHarness.cs`, `DeskClaudeHostTests.cs`, `FeedBuilderTests.cs`, `ChatViewModelTests.cs`, `MainViewModelChatTests.cs`, `ChatWindowSmokeTests.cs`.
- `docs/live-checklist-2026-09-25-desk-part2.md`.

**Изменения:**
- `src/SzDiag.Desk/SzDiag.Desk.csproj`, `Services/DeskOptions.cs`, `ViewModels/MainViewModel.cs`, `ViewModels/SzItemViewModel.cs`, `ViewModels/StatusBarViewModel.cs`, `Views/Converters.cs`, `Views/MainWindow.axaml(.cs)`, `Views/SzListView.axaml`, `Views/StatusBarView.axaml`, `App.axaml.cs`, `appsettings.json`.
- `tests/SzDiag.Desk.Tests/SzDiag.Desk.Tests.csproj`.
- `SzDiag.sln`, `tools/build-dist.ps1`, `CLAUDE.md`, `docs/dev-knowledge-base.md`, спека.

**Солюшен:** `dotnet sln add` раздувает `SzDiag.sln` платформами x64/x86 на ~170 строк. Проекты добавлять **в стиле существующих записей** (только `Any CPU`, вложение в папку `src`/`tests` через `NestedProjects`): `Project(...)`-запись перед `Global`, четыре строки `Debug|Any CPU`/`Release|Any CPU` (`ActiveCfg`, `Build.0`) в `ProjectConfigurationPlatforms`, строка `{guid} = {папка}` в `NestedProjects`. GUID папки `src` — `{ACFEDCEF-A9E9-4E8C-BE54-420707CB7F75}`, `tests` — `{F8025C56-47C2-44C8-9112-20121BCA7F58}`. Итоговый diff `SzDiag.sln` — 7 строк на проект.

---

### Task 1: `SzDiag.Claude` — события и парсер stream-json

**Files:**
- Create: `src/SzDiag.Claude/SzDiag.Claude.csproj`, `ClaudeEvents.cs`, `Json.cs`, `StreamJsonParser.cs`, `DeskLines.cs`
- Create: `tests/SzDiag.Claude.Tests/SzDiag.Claude.Tests.csproj`, `Fixture.cs`, `StreamJsonParserTests.cs`
- Modify: `SzDiag.sln`

**Interfaces:**
- Produces: иерархия `abstract record ClaudeEvent { DateTimeOffset? At { get; init; } }` и наследники `SystemInit(string SessionId, string? Model, string? Cwd, string? PermissionMode, IReadOnlyList<string> Tools, IReadOnlyList<McpServerStatus> McpServers)`, `AssistantText(string MessageId, string Text, string? ParentToolUseId)`, `ToolUse(string MessageId, string Id, string Name, JsonElement Input, string? ParentToolUseId)`, `ToolResult(string ToolUseId, string Text, bool IsError, bool Denied, string? ParentToolUseId)`, `TurnResult(string SessionId, bool IsError, bool Interrupted, string? Text, TokenUsage Usage, decimal CostUsd, long DurationMs, int NumTurns)`, `ControlResponse(string RequestId, bool Success, string? Error)`, `ServiceEvent(string Type, string? Subtype)`, `UnknownEvent(string Type, string Raw)`, `ParseError(string Raw, string Message)`, `DeskUserMessage(string Text)`, `DeskNote(string Text)`, `PermissionAsked(string RequestId, string ToolName, JsonElement Input, string? ToolUseId)`, `PermissionAnswered(string RequestId, bool Allowed)`, `ProcessCrashed(int? ExitCode, IReadOnlyList<string> StderrTail)`; `record TokenUsage(long Input, long Output, long CacheRead, long CacheCreation)` с `Zero`, `Total`, `Add`; `static IReadOnlyList<ClaudeEvent> StreamJsonParser.Parse(string line, DateTimeOffset receivedAt)`; `static string? DeskLines.Serialize(ClaudeEvent e)`, `const string DeskLines.Prefix = "desk_"`.

- [ ] **Step 1: Проекты** (UTF-8 с BOM)

`src/SzDiag.Claude/SzDiag.Claude.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <!-- MCP-сервер для permission_prompt живёт в процессе Desk (Kestrel на 127.0.0.1). -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```
`tests/SzDiag.Claude.Tests/SzDiag.Claude.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <None Include="Fixtures\*.jsonl" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SzDiag.Claude\SzDiag.Claude.csproj" />
  </ItemGroup>

</Project>
```
Оба проекта добавить в `SzDiag.sln` (см. «Солюшен» выше): `SzDiag.Claude` → папка `src`, `SzDiag.Claude.Tests` → `tests`.

- [ ] **Step 2: Помощник фикстур и failing tests**

`tests/SzDiag.Claude.Tests/Fixture.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Сырой stdout `claude` из спайка (Fixtures/*.jsonl) — парсер и сессия проверяются на
/// настоящих строках, а не на придуманных.</summary>
internal static class Fixture
{
    public static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static IReadOnlyList<string> Lines(string name)
        => File.ReadAllLines(PathOf(name)).Where(l => l.Trim().Length > 0).ToList();

    public static IReadOnlyList<ClaudeEvent> Events(string name)
        => Lines(name).SelectMany(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch)).ToList();

    /// <summary>Первая строка фикстуры, из которой разобралось событие нужного вида.</summary>
    public static string Line(string name, Func<ClaudeEvent, bool> match)
        => Lines(name).First(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch).Any(match));
}
```
`tests/SzDiag.Claude.Tests/StreamJsonParserTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class StreamJsonParserTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private const string SpikeSession = "8c8e3bf5-ea25-4879-a3dc-566095eba936";

    [Fact]
    public void SimpleTurn_InitTextResult()
    {
        var ev = Fixture.Events("simple-turn.jsonl");
        Assert.Equal(SpikeSession, Assert.Single(ev.OfType<SystemInit>()).SessionId);
        Assert.Equal("Привет", Assert.Single(ev.OfType<AssistantText>()).Text);
        var r = Assert.Single(ev.OfType<TurnResult>());
        Assert.False(r.IsError);
        Assert.False(r.Interrupted);
        Assert.Equal("Привет", r.Text);
        Assert.Equal(5, r.Usage.Output);
        Assert.True(r.CostUsd > 0);
        Assert.Contains(ev, e => e is ServiceEvent { Subtype: "hook_started" });
    }

    [Fact]
    public void ToolTurn_UseAndResultPaired()
    {
        var ev = Fixture.Events("tool-turn.jsonl");
        var use = Assert.Single(ev.OfType<ToolUse>());
        Assert.Equal("PowerShell", use.Name);
        Assert.Contains("szcli", use.Input.GetProperty("command").GetString());
        Assert.NotEqual(DateTimeOffset.UnixEpoch, use.At);   // время из поля timestamp, а не момент приёма
        var res = Assert.Single(ev.OfType<ToolResult>());
        Assert.Equal(use.Id, res.ToolUseId);
        Assert.False(res.IsError);
        Assert.False(res.Denied);
        Assert.Contains("szcli 1.0.0", res.Text);
    }

    [Fact]
    public void PermissionDeny_ResultMarkedDenied()
    {
        var res = Assert.Single(Fixture.Events("permission-deny-turn.jsonl").OfType<ToolResult>());
        Assert.True(res.IsError);
        Assert.True(res.Denied);
        Assert.Equal("отклонено", res.Text);
    }

    [Fact]
    public void PermissionAllow_ResultNotDenied()
    {
        var res = Assert.Single(Fixture.Events("permission-turn.jsonl").OfType<ToolResult>());
        Assert.False(res.Denied);
        Assert.Contains("File created successfully", res.Text);
    }

    [Fact]
    public void Interrupt_ControlResponseAndAbortedTurn_ThenNextTurn()
    {
        var ev = Fixture.Events("interrupt-turn.jsonl");
        var ctl = Assert.Single(ev.OfType<ControlResponse>());
        Assert.Equal("int-1", ctl.RequestId);
        Assert.True(ctl.Success);
        var results = ev.OfType<TurnResult>().ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results[0].Interrupted);
        Assert.True(results[0].IsError);
        Assert.Equal("Жив.", results[1].Text);
        Assert.Equal(2, ev.OfType<SystemInit>().Count());   // init приходит на каждый ход (спайк)
    }

    [Fact]
    public void Resume_KeepsSessionId()
        => Assert.Equal(SpikeSession, Assert.Single(Fixture.Events("resume-init.jsonl").OfType<SystemInit>()).SessionId);

    [Fact]
    public void BrokenLine_ParseError()
        => Assert.IsType<ParseError>(Assert.Single(StreamJsonParser.Parse("{не json", T0)));

    [Fact]
    public void EmptyLine_Nothing() => Assert.Empty(StreamJsonParser.Parse("   ", T0));

    [Fact]
    public void UnknownType_KeptRaw()
    {
        var u = Assert.IsType<UnknownEvent>(Assert.Single(StreamJsonParser.Parse("""{"type":"new_thing","x":1}""", T0)));
        Assert.Equal("new_thing", u.Type);
        Assert.Contains("\"x\":1", u.Raw);
    }

    [Fact]
    public void Bom_Stripped()
        => Assert.IsType<ServiceEvent>(Assert.Single(StreamJsonParser.Parse("\uFEFF{\"type\":\"rate_limit_event\"}", T0)));

    [Fact]
    public void NoTimestamp_UsesReceivedAt()
        => Assert.Equal(T0, StreamJsonParser.Parse("""{"type":"rate_limit_event"}""", T0)[0].At);

    [Fact]
    public void DeskLines_RoundTrip()
    {
        var at = new DateTimeOffset(2026, 9, 25, 12, 1, 2, TimeSpan.Zero);

        var user = DeskLines.Serialize(new DeskUserMessage("проверь SMART") { At = at })!;
        var u = Assert.IsType<DeskUserMessage>(Assert.Single(StreamJsonParser.Parse(user, T0)));
        Assert.Equal("проверь SMART", u.Text);
        Assert.Equal(at, u.At);

        var crash = DeskLines.Serialize(new ProcessCrashed(1, new[] { "boom" }) { At = at })!;
        var c = Assert.IsType<ProcessCrashed>(Assert.Single(StreamJsonParser.Parse(crash, T0)));
        Assert.Equal(1, c.ExitCode);
        Assert.Equal("boom", Assert.Single(c.StderrTail));

        using var doc = JsonDocument.Parse("""{"command":"dir"}""");
        var asked = DeskLines.Serialize(new PermissionAsked("r1", "Bash", doc.RootElement.Clone(), "t1") { At = at })!;
        var a = Assert.IsType<PermissionAsked>(Assert.Single(StreamJsonParser.Parse(asked, T0)));
        Assert.Equal("dir", a.Input.GetProperty("command").GetString());
        Assert.Equal("t1", a.ToolUseId);

        var answered = DeskLines.Serialize(new PermissionAnswered("r1", true) { At = at })!;
        Assert.True(Assert.IsType<PermissionAnswered>(Assert.Single(StreamJsonParser.Parse(answered, T0))).Allowed);
    }

    [Fact]
    public void DeskLines_NotDeskEvent_Null() => Assert.Null(DeskLines.Serialize(new ServiceEvent("x", null)));
}
```

- [ ] **Step 3: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: ошибка компиляции — `StreamJsonParser`, `ClaudeEvent` не найдены.

- [ ] **Step 4: Реализация**

`src/SzDiag.Claude/ClaudeEvents.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Событие сессии: пришедшее из stream-json `claude` или добавленное самим Desk
/// (сообщение пользователя, заметка, разрешение, падение процесса). Одна иерархия на оба
/// источника: лента и журнал сессии строятся из одного потока.</summary>
public abstract record ClaudeEvent
{
    /// <summary>Поле `timestamp` строки, если `claude` его прислал, иначе момент приёма.</summary>
    public DateTimeOffset? At { get; init; }
}

public sealed record McpServerStatus(string Name, string Status);

/// <summary>`system/init` — приходит на каждый ход, а не раз на процесс (спайк).</summary>
public sealed record SystemInit(string SessionId, string? Model, string? Cwd, string? PermissionMode,
    IReadOnlyList<string> Tools, IReadOnlyList<McpServerStatus> McpServers) : ClaudeEvent;

/// <param name="ParentToolUseId">Не null — событие субагента, а не главного хода.</param>
public sealed record AssistantText(string MessageId, string Text, string? ParentToolUseId) : ClaudeEvent;

public sealed record ToolUse(string MessageId, string Id, string Name, JsonElement Input, string? ParentToolUseId) : ClaudeEvent;

/// <param name="Denied">Инструмент не выполнялся — отказ в разрешении (`tool_result_meta.non_execution_kind`).</param>
public sealed record ToolResult(string ToolUseId, string Text, bool IsError, bool Denied, string? ParentToolUseId) : ClaudeEvent;

public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheCreation)
{
    public static TokenUsage Zero { get; } = new(0, 0, 0, 0);

    public long Total => Input + Output + CacheRead + CacheCreation;

    public TokenUsage Add(TokenUsage o)
        => new(Input + o.Input, Output + o.Output, CacheRead + o.CacheRead, CacheCreation + o.CacheCreation);
}

/// <param name="Interrupted">Ход прерван control-запросом `interrupt` (`terminal_reason: aborted_streaming`).</param>
public sealed record TurnResult(string SessionId, bool IsError, bool Interrupted, string? Text,
    TokenUsage Usage, decimal CostUsd, long DurationMs, int NumTurns) : ClaudeEvent;

public sealed record ControlResponse(string RequestId, bool Success, string? Error) : ClaudeEvent;

/// <summary>Хуки, rate limit, счётчик thinking, фоновые задачи — в ленту не идут.</summary>
public sealed record ServiceEvent(string Type, string? Subtype) : ClaudeEvent;

/// <summary>Неизвестный тип не теряется: лента покажет свёрнутую карточку `raw`.</summary>
public sealed record UnknownEvent(string Type, string Raw) : ClaudeEvent;

/// <summary>Строка не разобралась — пропуск + лог (спека, «Ошибки»).</summary>
public sealed record ParseError(string Raw, string Message) : ClaudeEvent;

public sealed record DeskUserMessage(string Text) : ClaudeEvent;

public sealed record DeskNote(string Text) : ClaudeEvent;

public sealed record PermissionAsked(string RequestId, string ToolName, JsonElement Input, string? ToolUseId) : ClaudeEvent;

public sealed record PermissionAnswered(string RequestId, bool Allowed) : ClaudeEvent;

public sealed record ProcessCrashed(int? ExitCode, IReadOnlyList<string> StderrTail) : ClaudeEvent;
```
`src/SzDiag.Claude/Json.cs`:
```csharp
using System.Globalization;
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Терпимое чтение полей: stream-json — чужой формат, отсутствующее поле не повод падать.</summary>
internal static class Json
{
    public static string? Str(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static bool Bool(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static long Long(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? n
            : 0;

    public static DateTimeOffset? Time(JsonElement o)
        => Str(o, "timestamp") is { } s
           && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t
            : null;
}
```
`src/SzDiag.Claude/StreamJsonParser.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Строка stdout `claude -p --output-format stream-json` → события. Схема — по фикстурам
/// спайка (docs/superpowers/specs/2026-09-25-desk-spike-notes.md). Блоки одного ответа приходят
/// отдельными строками с одинаковым `message.id`, поэтому строка может дать несколько событий.</summary>
public static class StreamJsonParser
{
    public static IReadOnlyList<ClaudeEvent> Parse(string line, DateTimeOffset receivedAt)
    {
        var text = line.TrimStart('\uFEFF').Trim();
        if (text.Length == 0) return Array.Empty<ClaudeEvent>();

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new ClaudeEvent[] { new ParseError(line, ex.Message) { At = receivedAt } };
        }
        if (root.ValueKind != JsonValueKind.Object)
            return new ClaudeEvent[] { new ParseError(line, "строка — не JSON-объект") { At = receivedAt } };

        var type = Json.Str(root, "type") ?? "";
        var at = Json.Time(root) ?? receivedAt;
        IEnumerable<ClaudeEvent> events = type switch
        {
            "system" => new[] { SystemEvent(root) },
            "assistant" => Assistant(root),
            "user" => User(root),
            "result" => new[] { Result(root) },
            "control_response" => new[] { Control(root) },
            "rate_limit_event" => new ClaudeEvent[] { new ServiceEvent(type, null) },
            _ when type.StartsWith(DeskLines.Prefix, StringComparison.Ordinal) => new[] { DeskLines.Parse(type, root) },
            _ => new ClaudeEvent[] { new UnknownEvent(type, text) },
        };
        return events.Select(e => e with { At = at }).ToList();
    }

    private static ClaudeEvent SystemEvent(JsonElement root)
    {
        var subtype = Json.Str(root, "subtype");
        if (subtype != "init") return new ServiceEvent("system", subtype);

        var tools = root.TryGetProperty("tools", out var t) && t.ValueKind == JsonValueKind.Array
            ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : new List<string>();
        var servers = root.TryGetProperty("mcp_servers", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => new McpServerStatus(Json.Str(x, "name") ?? "", Json.Str(x, "status") ?? "")).ToList()
            : new List<McpServerStatus>();
        return new SystemInit(Json.Str(root, "session_id") ?? "", Json.Str(root, "model"), Json.Str(root, "cwd"),
            Json.Str(root, "permissionMode"), tools, servers);
    }

    private static IEnumerable<ClaudeEvent> Assistant(JsonElement root)
    {
        var parent = Json.Str(root, "parent_tool_use_id");
        if (!root.TryGetProperty("message", out var msg)) yield break;
        var messageId = Json.Str(msg, "id") ?? "";
        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;

        foreach (var block in content.EnumerateArray())
        {
            switch (Json.Str(block, "type"))
            {
                case "text" when Json.Str(block, "text") is { Length: > 0 } text:
                    yield return new AssistantText(messageId, text, parent);
                    break;
                case "tool_use":
                    var input = block.TryGetProperty("input", out var i) ? i.Clone() : default;
                    yield return new ToolUse(messageId, Json.Str(block, "id") ?? "", Json.Str(block, "name") ?? "", input, parent);
                    break;
                // thinking в режиме -p приходит пустым (только подпись) — показывать нечего.
            }
        }
    }

    private static IEnumerable<ClaudeEvent> User(JsonElement root)
    {
        var parent = Json.Str(root, "parent_tool_use_id");
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array) yield break;

        var denied = DeniedIds(root);
        foreach (var block in content.EnumerateArray())
        {
            // Текстовые блоки («[Request interrupted by user]») — эхо, не событие ленты:
            // прерывание и так видно по result с terminal_reason.
            if (Json.Str(block, "type") != "tool_result") continue;
            var id = Json.Str(block, "tool_use_id") ?? "";
            yield return new ToolResult(id, ResultText(block), Json.Bool(block, "is_error"), denied.Contains(id), parent);
        }
    }

    private static HashSet<string> DeniedIds(JsonElement root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("tool_result_meta", out var meta) && meta.ValueKind == JsonValueKind.Array)
            foreach (var m in meta.EnumerateArray())
                if (Json.Str(m, "non_execution_kind") is not null && Json.Str(m, "id") is { } id)
                    ids.Add(id);
        return ids;
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return c.GetRawText();
        // MCP-инструменты отдают массив блоков: текст склеиваем, остальное помечаем типом.
        return string.Join("\n", c.EnumerateArray().Select(b =>
            Json.Str(b, "type") == "text" ? Json.Str(b, "text") ?? "" : $"[{Json.Str(b, "type")}]"));
    }

    private static ClaudeEvent Result(JsonElement root)
    {
        var usage = TokenUsage.Zero;
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            usage = new TokenUsage(Json.Long(u, "input_tokens"), Json.Long(u, "output_tokens"),
                Json.Long(u, "cache_read_input_tokens"), Json.Long(u, "cache_creation_input_tokens"));
        var cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetDecimal()
            : 0m;
        return new TurnResult(Json.Str(root, "session_id") ?? "", Json.Bool(root, "is_error"),
            Json.Str(root, "terminal_reason") == "aborted_streaming", Json.Str(root, "result"),
            usage, cost, Json.Long(root, "duration_ms"), (int)Json.Long(root, "num_turns"));
    }

    private static ClaudeEvent Control(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var r)) return new ControlResponse("", false, "нет поля response");
        return new ControlResponse(Json.Str(r, "request_id") ?? "", Json.Str(r, "subtype") == "success", Json.Str(r, "error"));
    }
}
```
`src/SzDiag.Claude/DeskLines.cs`:
```csharp
using System.Globalization;
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Строки журнала сессии, которые пишет сам Desk (тип `desk_*`). Журнал — сырой
/// stream-json вперемешку с ними, и читается тем же парсером: `--resume` историю в stdout не
/// повторяет, так что ленту после перезапуска Desk восстанавливает только этот журнал.</summary>
public static class DeskLines
{
    public const string Prefix = "desk_";

    public static string? Serialize(ClaudeEvent e)
    {
        var at = (e.At ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
        object? line = e switch
        {
            DeskUserMessage m => new { type = "desk_user", text = m.Text, timestamp = at },
            DeskNote n => new { type = "desk_note", text = n.Text, timestamp = at },
            PermissionAsked p => new
            {
                type = "desk_permission_asked", request_id = p.RequestId, tool_name = p.ToolName,
                input = p.Input.ValueKind == JsonValueKind.Undefined ? (object?)null : p.Input,
                tool_use_id = p.ToolUseId, timestamp = at,
            },
            PermissionAnswered a => new { type = "desk_permission_answered", request_id = a.RequestId, allowed = a.Allowed, timestamp = at },
            ProcessCrashed c => new { type = "desk_crash", exit_code = c.ExitCode, stderr = c.StderrTail, timestamp = at },
            _ => null,
        };
        return line is null ? null : JsonSerializer.Serialize(line);
    }

    internal static ClaudeEvent Parse(string type, JsonElement root) => type switch
    {
        "desk_user" => new DeskUserMessage(Json.Str(root, "text") ?? ""),
        "desk_note" => new DeskNote(Json.Str(root, "text") ?? ""),
        "desk_permission_asked" => new PermissionAsked(Json.Str(root, "request_id") ?? "", Json.Str(root, "tool_name") ?? "",
            root.TryGetProperty("input", out var i) ? i.Clone() : default, Json.Str(root, "tool_use_id")),
        "desk_permission_answered" => new PermissionAnswered(Json.Str(root, "request_id") ?? "", Json.Bool(root, "allowed")),
        "desk_crash" => new ProcessCrashed(
            root.TryGetProperty("exit_code", out var c) && c.ValueKind == JsonValueKind.Number ? (int?)c.GetInt32() : null,
            root.TryGetProperty("stderr", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                : new List<string>()),
        _ => new UnknownEvent(type, root.GetRawText()),
    };
}
```

- [ ] **Step 5: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: 13 passed. Если сборка падает на `NU1605`/конфликте версий `Microsoft.Extensions.*` (MCP SDK тянет 10.x) — это ошибка, а не предупреждение: поднять конфликтующие прямые ссылки до версии из сообщения и записать в ledger.

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Claude tests/SzDiag.Claude.Tests/SzDiag.Claude.Tests.csproj tests/SzDiag.Claude.Tests/Fixture.cs tests/SzDiag.Claude.Tests/StreamJsonParserTests.cs SzDiag.sln
git commit -m "feat(claude): ядро сессий — события и парсер stream-json"
```

---

### Task 2: Запуск процесса `claude`

**Files:**
- Create: `src/SzDiag.Claude/ClaudeInput.cs`, `ClaudeLaunch.cs`, `ClaudeLocator.cs`, `IClaudeProcess.cs`, `ClaudeProcess.cs`
- Test: `tests/SzDiag.Claude.Tests/ClaudeLaunchTests.cs`, `ClaudeProcessTests.cs`

**Interfaces:**
- Produces: `static string ClaudeInput.UserMessage(string text)`, `static string ClaudeInput.Interrupt(string requestId)`; `record ClaudeLaunch(string Executable, string WorkDir, string? ConfigDir, string? ResumeSessionId, string AppendSystemPrompt, string McpConfigPath)` с `const string PermissionTool`, `IReadOnlyList<string> Arguments()`, `ProcessStartInfo ToStartInfo()`; `static string? ClaudeLocator.Resolve(string? configured, string? pathVariable)`; `interface IClaudeProcess` (`event Action<string>? OutputLine`, `event Action<int?>? Exited`, `bool IsRunning`, `IReadOnlyList<string> StderrTail`, `void Start(ClaudeLaunch launch)`, `Task WriteLineAsync(string line)`, `Task StopAsync(TimeSpan grace)`); `sealed class ClaudeProcess : IClaudeProcess` + `void Start(ProcessStartInfo psi)`, `const int StderrLines = 200`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Claude.Tests/ClaudeLaunchTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class ClaudeLaunchTests
{
    private static ClaudeLaunch L(string? resume = null, string? configDir = "C:\\cfg")
        => new("C:\\bin\\claude.exe", "C:\\repo", configDir, resume, "вводная СЗ 161432", "C:\\run\\161432.mcp.json");

    private static string After(IReadOnlyList<string> args, string flag) => args[args.ToList().IndexOf(flag) + 1];

    [Fact]
    public void Arguments_StreamJsonPermissionsMcp()
    {
        var a = L().Arguments();
        Assert.Equal(new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" }, a.Take(6));
        Assert.Equal("default", After(a, "--permission-mode"));
        Assert.Equal("mcp__desk__permission_prompt", After(a, "--permission-prompt-tool"));
        Assert.Equal("C:\\run\\161432.mcp.json", After(a, "--mcp-config"));
        Assert.Equal("вводная СЗ 161432", After(a, "--append-system-prompt"));
        Assert.DoesNotContain("--resume", a);
    }

    [Fact]
    public void Arguments_Resume() => Assert.Equal("sid-1", After(L("sid-1").Arguments(), "--resume"));

    [Fact]
    public void ToStartInfo_SetsConfigDir()
    {
        // Профиль на боксе задаёт обёртка claude2.cmd, а не переменная пользователя: Desk из
        // Проводника без этого поднял бы claude с чужим профилем.
        var psi = L().ToStartInfo();
        Assert.Equal("C:\\cfg", psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("C:\\repo", psi.WorkingDirectory);
        Assert.Equal("C:\\bin\\claude.exe", psi.FileName);
        Assert.Equal(L().Arguments(), psi.ArgumentList);
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void ToStartInfo_NoConfigDir_KeepsInherited()
        => Assert.Equal(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            L(configDir: null).ToStartInfo().Environment["CLAUDE_CONFIG_DIR"]);

    [Fact]
    public void ToStartInfo_StdinUtf8WithoutBom()
    {
        // BOM в начале stdin сломал бы разбор первой же строки у claude.
        var enc = L().ToStartInfo().StandardInputEncoding!;
        Assert.Equal(65001, enc.CodePage);
        Assert.Empty(enc.GetPreamble());
    }

    [Fact]
    public void UserMessage_Shape()
    {
        var m = JsonDocument.Parse(ClaudeInput.UserMessage("проверь \"SMART\"")).RootElement;
        Assert.Equal("user", m.GetProperty("type").GetString());
        Assert.Equal("user", m.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("проверь \"SMART\"", m.GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public void Interrupt_Shape()
    {
        var m = JsonDocument.Parse(ClaudeInput.Interrupt("int-7")).RootElement;
        Assert.Equal("control_request", m.GetProperty("type").GetString());
        Assert.Equal("int-7", m.GetProperty("request_id").GetString());
        Assert.Equal("interrupt", m.GetProperty("request").GetProperty("subtype").GetString());
    }

    [Fact]
    public void Locator_FindsInPath_AndHonoursConfigured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "szclaude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "claude.exe");
            File.WriteAllText(exe, "");
            Assert.Equal(exe, ClaudeLocator.Resolve(null, $"C:\\нет{Path.PathSeparator}{dir}"));
            Assert.Equal(exe, ClaudeLocator.Resolve(exe, null));
            Assert.Null(ClaudeLocator.Resolve(Path.Combine(dir, "другой.exe"), dir));   // явный путь не подменяем PATH
            Assert.Null(ClaudeLocator.Resolve("", "C:\\нет"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
```
`tests/SzDiag.Claude.Tests/ClaudeProcessTests.cs`:
```csharp
using System.Diagnostics;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Обвязка процесса на обычных программах Windows: sort.exe читает stdin до EOF и
/// печатает строки, cmd пишет в stderr, ping живёт 30 с и не читает stdin.</summary>
public class ClaudeProcessTests
{
    private static ProcessStartInfo Psi(string exe, params string[] args)
    {
        var p = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) p.ArgumentList.Add(a);
        return p;
    }

    [Fact]
    public async Task WriteLines_CloseStdin_ReadOutput_Exit()
    {
        var p = new ClaudeProcess();
        var lines = new List<string>();
        var exited = new TaskCompletionSource<int?>();
        p.OutputLine += l => { lock (lines) lines.Add(l); };
        p.Exited += c => exited.TrySetResult(c);

        p.Start(Psi("sort.exe"));
        Assert.True(p.IsRunning);
        await p.WriteLineAsync("b");
        await p.WriteLineAsync("a");
        await p.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "a", "b" }, lines);
        Assert.False(p.IsRunning);
    }

    [Fact]
    public async Task Stderr_KeptInTail()
    {
        var p = new ClaudeProcess();
        var exited = new TaskCompletionSource<int?>();
        p.Exited += c => exited.TrySetResult(c);
        p.Start(Psi("cmd.exe", "/d", "/c", "echo oops 1>&2"));
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(p.StderrTail, l => l.Contains("oops"));
    }

    [Fact]
    public async Task Stop_KillsProcessThatIgnoresStdin()
    {
        var p = new ClaudeProcess();
        p.Start(Psi("ping.exe", "-n", "30", "127.0.0.1"));
        var sw = Stopwatch.StartNew();
        await p.StopAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(p.IsRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"остановка заняла {sw.Elapsed}");
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter "FullyQualifiedName~ClaudeLaunch|FullyQualifiedName~ClaudeProcess"`
Expected: ошибка компиляции — `ClaudeLaunch`, `ClaudeProcess` не найдены.

- [ ] **Step 3: Реализация**

`src/SzDiag.Claude/ClaudeInput.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Claude;

/// <summary>Строки stdin для `claude --input-format stream-json` (форма подтверждена спайком).</summary>
public static class ClaudeInput
{
    public static string UserMessage(string text)
        => JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = text } });

    /// <summary>Прерывание хода: процесс остаётся жив, ход закрывается `result` с
    /// `terminal_reason: aborted_streaming`.</summary>
    public static string Interrupt(string requestId)
        => JsonSerializer.Serialize(new { type = "control_request", request_id = requestId, request = new { subtype = "interrupt" } });
}
```
`src/SzDiag.Claude/ClaudeLaunch.cs`:
```csharp
using System.Diagnostics;
using System.Text;

namespace SzDiag.Claude;

/// <summary>Всё, с чем запускается один процесс `claude` сессии.</summary>
/// <param name="WorkDir">Корень репозитория sz-diag: там CLAUDE.md, .claude/settings.json, скиллы.</param>
/// <param name="ConfigDir">CLAUDE_CONFIG_DIR — профиль (логин, память); null — унаследовать.</param>
public sealed record ClaudeLaunch(string Executable, string WorkDir, string? ConfigDir, string? ResumeSessionId,
    string AppendSystemPrompt, string McpConfigPath)
{
    public const string PermissionTool = "mcp__desk__permission_prompt";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<string> Arguments()
    {
        var a = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            // Без явного default у пользователя действует auto, и permission_prompt не зовётся вовсе (спайк).
            "--permission-mode", "default",
            "--permission-prompt-tool", PermissionTool,
            "--mcp-config", McpConfigPath,
            "--append-system-prompt", AppendSystemPrompt,
        };
        if (ResumeSessionId is { Length: > 0 } id)
        {
            a.Add("--resume");
            a.Add(id);
        }
        return a;
    }

    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var arg in Arguments()) psi.ArgumentList.Add(arg);
        if (ConfigDir is { Length: > 0 } dir) psi.Environment["CLAUDE_CONFIG_DIR"] = dir;
        return psi;
    }
}
```
`src/SzDiag.Claude/ClaudeLocator.cs`:
```csharp
namespace SzDiag.Claude;

public static class ClaudeLocator
{
    /// <summary>Явный путь из конфига — только он (молча подменить его найденным в PATH значило бы
    /// запустить не тот claude); пусто — ищем claude.exe в PATH. Нативный установщик кладёт именно
    /// exe, а .cmd-обёртки пришлось бы запускать через cmd /c.</summary>
    public static string? Resolve(string? configured, string? pathVariable)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? configured : null;
        foreach (var dir in (pathVariable ?? "").Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "claude.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
```
`src/SzDiag.Claude/IClaudeProcess.cs`:
```csharp
namespace SzDiag.Claude;

/// <summary>Процесс `claude` одной сессии. Интерфейс — ради фейка в тестах сессии.</summary>
public interface IClaudeProcess
{
    /// <summary>Строка stdout (без перевода строки). Вызывается с фонового потока.</summary>
    event Action<string>? OutputLine;

    /// <summary>Процесс завершился (код, если известен) — после того как stdout дочитан.</summary>
    event Action<int?>? Exited;

    bool IsRunning { get; }

    /// <summary>Последние строки stderr — для карточки падения.</summary>
    IReadOnlyList<string> StderrTail { get; }

    void Start(ClaudeLaunch launch);

    Task WriteLineAsync(string line);

    /// <summary>Закрыть stdin, дать <paramref name="grace"/> на выход, затем убить всё дерево.</summary>
    Task StopAsync(TimeSpan grace);
}
```
`src/SzDiag.Claude/ClaudeProcess.cs`:
```csharp
using System.Diagnostics;

namespace SzDiag.Claude;

public sealed class ClaudeProcess : IClaudeProcess
{
    public const int StderrLines = 200;

    private readonly object _gate = new();
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private Task? _pump;

    public event Action<string>? OutputLine;
    public event Action<int?>? Exited;

    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false }; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public IReadOnlyList<string> StderrTail
    {
        get { lock (_gate) return _stderr.ToList(); }
    }

    public void Start(ClaudeLaunch launch) => Start(launch.ToStartInfo());

    /// <summary>Отдельно от <see cref="ClaudeLaunch"/> — чтобы обвязку можно было проверить на
    /// обычной консольной программе.</summary>
    public void Start(ProcessStartInfo psi)
    {
        if (_process is not null) throw new InvalidOperationException("процесс уже запущен");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_gate)
            {
                _stderr.Enqueue(e.Data);
                while (_stderr.Count > StderrLines) _stderr.Dequeue();
            }
        };
        p.Start();
        p.BeginErrorReadLine();
        _process = p;
        _pump = Task.Run(() => PumpAsync(p));
    }

    private async Task PumpAsync(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                OutputLine?.Invoke(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // stdout закрылся при kill — дальше просто ждём выхода.
        }
        await p.WaitForExitAsync().ConfigureAwait(false);
        int? code = null;
        try { code = p.ExitCode; }
        catch (InvalidOperationException) { }
        Exited?.Invoke(code);
    }

    public async Task WriteLineAsync(string line)
    {
        var p = _process ?? throw new InvalidOperationException("процесс не запущен");
        await p.StandardInput.WriteLineAsync(line).ConfigureAwait(false);
        await p.StandardInput.FlushAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(TimeSpan grace)
    {
        var p = _process;
        if (p is null) return;
        try { p.StandardInput.Close(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { }

        using var cts = new CancellationTokenSource(grace);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // claude запускает дочерние процессы (bash, MCP-серверы) — убиваем всё дерево.
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (_pump is not null) await _pump.ConfigureAwait(false);
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: всё зелёное (13 из задачи 1 + 8 + 3).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Claude tests/SzDiag.Claude.Tests/ClaudeLaunchTests.cs tests/SzDiag.Claude.Tests/ClaudeProcessTests.cs
git commit -m "feat(claude): запуск процесса claude в режиме stream-json"
```

---

### Task 3: Журнал сессии, реестр сессий, токены за день

**Files:**
- Create: `src/SzDiag.Claude/TranscriptStore.cs`, `SessionIndex.cs`, `TokenLedger.cs`
- Test: `tests/SzDiag.Claude.Tests/StoresTests.cs`

**Interfaces:**
- Consumes: `StreamJsonParser.Parse`, `TokenUsage` (задача 1).
- Produces: `TranscriptStore(string dir)` — `string PathFor(string key)`, `void Append(string key, string rawLine)`, `IReadOnlyList<ClaudeEvent> Load(string key)`; `record SessionRecord(string Key, string? SessionId, DateTimeOffset CreatedAt, bool Archived)`; `SessionIndex` — `static SessionIndex Load(string path)`, `IReadOnlyList<SessionRecord> All`, `SessionRecord? Get(string key)`, `void Put(SessionRecord r)`; `TokenLedger(string path, TimeProvider time)` — `TokenUsage Today`, `decimal CostToday`, `void Add(TokenUsage usage, decimal costUsd)`, `event Action? Changed`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Claude.Tests/StoresTests.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class StoresTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szclaude-" + Guid.NewGuid().ToString("N"));

    public StoresTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    [Fact]
    public void Transcript_AppendAndLoad()
    {
        var t = new TranscriptStore(Path.Combine(_dir, "sessions"));
        t.Append("161432", DeskLines.Serialize(new DeskUserMessage("привет"))!);
        t.Append("161432", Fixture.Line("simple-turn.jsonl", e => e is AssistantText) + "\r\n");

        var ev = t.Load("161432");
        Assert.Equal("привет", Assert.IsType<DeskUserMessage>(ev[0]).Text);
        Assert.Equal("Привет", Assert.IsType<AssistantText>(ev[1]).Text);
        Assert.Empty(t.Load("161501"));
    }

    [Fact]
    public void Transcript_KeyWithBadChars_StaysInDir()
    {
        var t = new TranscriptStore(_dir);
        Assert.Equal(_dir, Path.GetDirectoryName(t.PathFor("..\\x:y")));
    }

    [Fact]
    public void Index_PutGetReload()
    {
        var path = Path.Combine(_dir, "desk-sessions.json");
        var idx = SessionIndex.Load(path);
        idx.Put(new SessionRecord("161501", null, DateTimeOffset.UnixEpoch, false));
        idx.Put(new SessionRecord("161432", "sid", DateTimeOffset.UnixEpoch, true));

        var again = SessionIndex.Load(path);
        Assert.Equal(new[] { "161432", "161501" }, again.All.Select(r => r.Key));
        Assert.Equal("sid", again.Get("161432")!.SessionId);
        Assert.True(again.Get("161432")!.Archived);
        Assert.Null(again.Get("999999"));
    }

    [Fact]
    public void Index_CorruptFile_Empty()
    {
        var path = Path.Combine(_dir, "desk-sessions.json");
        File.WriteAllText(path, "{битый");
        Assert.Empty(SessionIndex.Load(path).All);
    }

    [Fact]
    public void Ledger_SumsPersistsAndRollsOverAtMidnight()
    {
        var path = Path.Combine(_dir, "desk-tokens.json");
        var clock = new Clock();
        var l = new TokenLedger(path, clock);
        var changed = 0;
        l.Changed += () => changed++;
        l.Add(new TokenUsage(100, 20, 1000, 50), 0.10m);
        l.Add(new TokenUsage(1, 2, 3, 4), 0.02m);

        Assert.Equal(1180, l.Today.Total);
        Assert.Equal(0.12m, l.CostToday);
        Assert.Equal(2, changed);
        Assert.Equal(1180, new TokenLedger(path, clock).Today.Total);

        clock.Now = clock.Now.AddDays(1);
        Assert.Equal(0, l.Today.Total);
        Assert.Equal(0m, l.CostToday);
    }

    [Fact]
    public void Ledger_CorruptFile_StartsFromZero()
    {
        var path = Path.Combine(_dir, "desk-tokens.json");
        File.WriteAllText(path, "мусор");
        Assert.Equal(0, new TokenLedger(path, new Clock()).Today.Total);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter FullyQualifiedName~StoresTests`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Claude/TranscriptStore.cs`:
```csharp
using System.Text;

namespace SzDiag.Claude;

/// <summary>Журнал сессии `&lt;dir&gt;\&lt;ключ&gt;.jsonl`: сырые строки stream-json (без служебных) и
/// строки Desk (<see cref="DeskLines"/>). Из него лента восстанавливается после перезапуска Desk.
/// Ошибка записи не роняет сессию — теряется только история.</summary>
public sealed class TranscriptStore(string dir)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private readonly object _gate = new();

    public string PathFor(string key)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string(key.Select(c => bad.Contains(c) || c == '.' ? '_' : c).ToArray());
        return Path.Combine(dir, safe + ".jsonl");
    }

    public void Append(string key, string rawLine)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(PathFor(key), rawLine.TrimEnd('\r', '\n') + "\n", Utf8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<ClaudeEvent> Load(string key)
    {
        var path = PathFor(key);
        lock (_gate)
        {
            if (!File.Exists(path)) return Array.Empty<ClaudeEvent>();
            try
            {
                return File.ReadLines(path, Utf8)
                    .SelectMany(l => StreamJsonParser.Parse(l, DateTimeOffset.MinValue))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<ClaudeEvent>();
            }
        }
    }
}
```
`src/SzDiag.Claude/SessionIndex.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Claude;

/// <param name="SessionId">session_id из первого init; до первого хода — null. При --resume он
/// не меняется (спайк), поэтому хранится один раз.</param>
public sealed record SessionRecord(string Key, string? SessionId, DateTimeOffset CreatedAt, bool Archived);

/// <summary>Реестр `ключ → session_id` (`desk-sessions.json`). Битый файл — пустой реестр, а не
/// падение окна: сессии можно начать заново, а разговоры остаются в профиле claude.</summary>
public sealed class SessionIndex
{
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, SessionRecord> _items;

    private SessionIndex(string path, Dictionary<string, SessionRecord> items)
    {
        _path = path;
        _items = items;
    }

    public static SessionIndex Load(string path)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<SessionRecord>>(File.ReadAllText(path)) ?? new();
            var items = new Dictionary<string, SessionRecord>(StringComparer.Ordinal);
            foreach (var r in list) items[r.Key] = r;
            return new SessionIndex(path, items);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new SessionIndex(path, new Dictionary<string, SessionRecord>(StringComparer.Ordinal));
        }
    }

    public IReadOnlyList<SessionRecord> All
    {
        get { lock (_gate) return _items.Values.OrderBy(r => r.Key, StringComparer.Ordinal).ToList(); }
    }

    public SessionRecord? Get(string key)
    {
        lock (_gate) return _items.GetValueOrDefault(key);
    }

    public void Put(SessionRecord r)
    {
        lock (_gate)
        {
            _items[r.Key] = r;
            try
            {
                var full = Path.GetFullPath(_path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                var tmp = full + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_items.Values.ToList()));
                File.Move(tmp, full, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
```
`src/SzDiag.Claude/TokenLedger.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Claude;

internal sealed record TokenDay(DateOnly Date, TokenUsage Usage, decimal CostUsd);

/// <summary>Токены и стоимость за текущие сутки (по локальным часам бокса) по всем сессиям —
/// для статусбара. Файл `desk-tokens.json`: переживает перезапуск Desk в течение дня.</summary>
public sealed class TokenLedger
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private TokenDay _day;

    public TokenLedger(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        _day = Load() ?? new TokenDay(TodayDate(), TokenUsage.Zero, 0m);
    }

    public event Action? Changed;

    public TokenUsage Today
    {
        get { lock (_gate) { Roll(); return _day.Usage; } }
    }

    public decimal CostToday
    {
        get { lock (_gate) { Roll(); return _day.CostUsd; } }
    }

    public void Add(TokenUsage usage, decimal costUsd)
    {
        lock (_gate)
        {
            Roll();
            _day = _day with { Usage = _day.Usage.Add(usage), CostUsd = _day.CostUsd + costUsd };
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_day)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private DateOnly TodayDate() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private void Roll()
    {
        var today = TodayDate();
        if (_day.Date != today) _day = new TokenDay(today, TokenUsage.Zero, 0m);
    }

    private TokenDay? Load()
    {
        try { return JsonSerializer.Deserialize<TokenDay>(File.ReadAllText(_path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter FullyQualifiedName~StoresTests`
Expected: 6 passed.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Claude tests/SzDiag.Claude.Tests/StoresTests.cs
git commit -m "feat(claude): журнал сессии, реестр сессий и токены за день"
```

---

### Task 4: `PermissionBroker` и MCP-сервер `permission_prompt`

**Files:**
- Create: `src/SzDiag.Claude/PermissionBroker.cs`, `DeskMcpServer.cs`
- Test: `tests/SzDiag.Claude.Tests/PermissionBrokerTests.cs`, `DeskMcpServerTests.cs`

**Interfaces:**
- Produces: `record PendingPermission(string RequestId, string Key, string ToolName, JsonElement Input, string? ToolUseId, DateTimeOffset At)`; `record PermissionVerdict(bool Allow, JsonElement? UpdatedInput, string? Message)` с `string ToJson()`; `PermissionBroker(TimeProvider time)` — `event Action<PendingPermission>? Requested`, `event Action<string, bool>? Resolved`, `Task<PermissionVerdict> AskAsync(string key, string toolName, JsonElement input, string? toolUseId, CancellationToken ct)`, `bool Resolve(string requestId, bool allow, string? message = null)`, `void DenyAll(string key, string message)`, `IReadOnlyList<PendingPermission> Pending(string key)`, `int PendingCount`; `DeskMcpServer` — `const string TokenHeader = "X-Desk-Token"`, `string Token`, `string? BaseUrl`, `Task StartAsync(PermissionBroker broker, CancellationToken ct = default)`, `string EndpointFor(string key)`, `string McpConfigJson(string key)`, `ValueTask DisposeAsync()`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Claude.Tests/PermissionBrokerTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class PermissionBrokerTests
{
    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
    private readonly PermissionBroker _b = new(TimeProvider.System);

    [Fact]
    public async Task Ask_WaitsUntilResolved_AllowEchoesInput()
    {
        PendingPermission? seen = null;
        _b.Requested += p => seen = p;
        var ask = _b.AskAsync("161432", "Bash", J("""{"command":"dir"}"""), "t1", default);

        Assert.False(ask.IsCompleted);
        Assert.Equal(1, _b.PendingCount);
        Assert.Equal("161432", seen!.Key);
        Assert.True(_b.Resolve(seen.RequestId, true));

        var v = await ask;
        Assert.True(v.Allow);
        Assert.Equal("dir", v.UpdatedInput!.Value.GetProperty("command").GetString());
        Assert.Equal(0, _b.PendingCount);
        Assert.False(_b.Resolve(seen.RequestId, true));   // повторный ответ — no-op
    }

    [Fact]
    public async Task Cancel_Denies()
    {
        using var cts = new CancellationTokenSource();
        var ask = _b.AskAsync("161432", "Bash", J("{}"), null, cts.Token);
        cts.Cancel();
        var v = await ask;
        Assert.False(v.Allow);
        Assert.Equal("запрос отменён", v.Message);
    }

    [Fact]
    public async Task DenyAll_OnlyThatKey()
    {
        var a = _b.AskAsync("161432", "Bash", J("{}"), null, default);
        var b = _b.AskAsync("161501", "Bash", J("{}"), null, default);
        _b.DenyAll("161432", "сессия остановлена");

        Assert.Equal("сессия остановлена", (await a).Message);
        Assert.False(b.IsCompleted);
        Assert.Single(_b.Pending("161501"));
        Assert.Empty(_b.Pending("161432"));
    }

    [Fact]
    public void Verdict_Json()
    {
        var allow = JsonDocument.Parse(new PermissionVerdict(true, J("""{"a":1}"""), null).ToJson()).RootElement;
        Assert.Equal("allow", allow.GetProperty("behavior").GetString());
        Assert.Equal(1, allow.GetProperty("updatedInput").GetProperty("a").GetInt32());

        var deny = JsonDocument.Parse(new PermissionVerdict(false, null, "нет").ToJson()).RootElement;
        Assert.Equal("deny", deny.GetProperty("behavior").GetString());
        Assert.Equal("нет", deny.GetProperty("message").GetString());
    }
}
```
`tests/SzDiag.Claude.Tests/DeskMcpServerTests.cs`:
```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Сервер проверяется голым JSON-RPC: в stateless-режиме `tools/call` отвечает без
/// initialize, ответ — SSE `event: message` / `data: {...}` (проверено на спайке).</summary>
public class DeskMcpServerTests : IAsyncLifetime
{
    private readonly PermissionBroker _broker = new(TimeProvider.System);
    private readonly DeskMcpServer _server = new();
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => _server.StartAsync(_broker);

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private Task<HttpResponseMessage> CallAsync(string key, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, _server.EndpointFor(key));
        if (token is not null) req.Headers.Add(DeskMcpServer.TokenHeader, token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"permission_prompt","arguments":{"tool_name":"Write","input":{"file_path":"C:\\x.txt"},"tool_use_id":"t1"}}}""",
            Encoding.UTF8, "application/json");
        return _http.SendAsync(req);
    }

    private static async Task<JsonElement> VerdictAsync(HttpResponseMessage resp)
    {
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync();
        var data = body.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
        var text = JsonDocument.Parse(data).RootElement.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<PendingPermission> NextRequest()
    {
        var tcs = new TaskCompletionSource<PendingPermission>();
        _broker.Requested += p => tcs.TrySetResult(p);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PermissionPrompt_WaitsForOperator_Allow()
    {
        var next = NextRequest();
        var call = CallAsync("161432", _server.Token);
        var p = await next;
        Assert.Equal("161432", p.Key);                       // ключ сессии — из пути /mcp/<ключ>
        Assert.Equal("Write", p.ToolName);
        Assert.Equal("t1", p.ToolUseId);
        Assert.False(call.IsCompleted);                     // ждёт человека

        _broker.Resolve(p.RequestId, true);
        var v = await VerdictAsync(await call);
        Assert.Equal("allow", v.GetProperty("behavior").GetString());
        Assert.Equal("C:\\x.txt", v.GetProperty("updatedInput").GetProperty("file_path").GetString());
    }

    [Fact]
    public async Task PermissionPrompt_Deny()
    {
        var next = NextRequest();
        var call = CallAsync("161432", _server.Token);
        _broker.Resolve((await next).RequestId, false, "не надо");
        var v = await VerdictAsync(await call);
        Assert.Equal("deny", v.GetProperty("behavior").GetString());
        Assert.Equal("не надо", v.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WrongOrMissingToken_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("161432", "чужой")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync("161432", null)).StatusCode);
    }

    [Fact]
    public void McpConfig_HasEndpointTokenAndDayTimeout()
    {
        var desk = JsonDocument.Parse(_server.McpConfigJson("161432")).RootElement
            .GetProperty("mcpServers").GetProperty("desk");
        Assert.Equal("http", desk.GetProperty("type").GetString());
        Assert.Equal(_server.EndpointFor("161432"), desk.GetProperty("url").GetString());
        Assert.EndsWith("/mcp/161432", desk.GetProperty("url").GetString());
        Assert.StartsWith("http://127.0.0.1:", desk.GetProperty("url").GetString());
        Assert.Equal(_server.Token, desk.GetProperty("headers").GetProperty(DeskMcpServer.TokenHeader).GetString());
        Assert.Equal(86_400_000, desk.GetProperty("timeout").GetInt32());
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter "FullyQualifiedName~PermissionBroker|FullyQualifiedName~DeskMcpServer"`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Claude/PermissionBroker.cs`:
```csharp
using System.Collections.Concurrent;
using System.Text.Json;

namespace SzDiag.Claude;

public sealed record PendingPermission(string RequestId, string Key, string ToolName, JsonElement Input,
    string? ToolUseId, DateTimeOffset At);

/// <summary>Ответ для `--permission-prompt-tool` (схема — спайк).</summary>
public sealed record PermissionVerdict(bool Allow, JsonElement? UpdatedInput, string? Message)
{
    public string ToJson() => Allow
        ? JsonSerializer.Serialize(new { behavior = "allow", updatedInput = UpdatedInput })
        : JsonSerializer.Serialize(new { behavior = "deny", message = Message ?? "отклонено" });
}

/// <summary>Запросы разрешений от всех сессий: MCP-тулза ждёт здесь решения человека в окне.
/// Ждём сколько угодно, как терминал (спека): отмена приходит только от самого claude
/// (обрыв запроса) или от остановки сессии.</summary>
public sealed class PermissionBroker(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (PendingPermission P, TaskCompletionSource<PermissionVerdict> Tcs)> _pending = new();

    public event Action<PendingPermission>? Requested;

    /// <summary>Решение принято: requestId, разрешено ли.</summary>
    public event Action<string, bool>? Resolved;

    public int PendingCount => _pending.Count;

    public IReadOnlyList<PendingPermission> Pending(string key)
        => _pending.Values.Select(e => e.P).Where(p => p.Key == key).OrderBy(p => p.At).ToList();

    public async Task<PermissionVerdict> AskAsync(string key, string toolName, JsonElement input, string? toolUseId,
        CancellationToken ct)
    {
        var p = new PendingPermission(Guid.NewGuid().ToString("N"), key, toolName, input.Clone(), toolUseId, time.GetUtcNow());
        var tcs = new TaskCompletionSource<PermissionVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[p.RequestId] = (p, tcs);
        using var reg = ct.Register(() => Resolve(p.RequestId, false, "запрос отменён"));
        Requested?.Invoke(p);
        return await tcs.Task.ConfigureAwait(false);
    }

    public bool Resolve(string requestId, bool allow, string? message = null)
    {
        if (!_pending.TryRemove(requestId, out var e)) return false;
        e.Tcs.TrySetResult(allow
            ? new PermissionVerdict(true, e.P.Input, null)
            : new PermissionVerdict(false, null, message ?? "отклонено оператором"));
        Resolved?.Invoke(requestId, allow);
        return true;
    }

    public void DenyAll(string key, string message)
    {
        foreach (var (id, e) in _pending)
            if (e.P.Key == key) Resolve(id, false, message);
    }
}
```
`src/SzDiag.Claude/DeskMcpServer.cs`:
```csharp
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SzDiag.Claude;

/// <summary>MCP-сервер Desk на 127.0.0.1 со случайным портом. У каждой сессии свой путь
/// `/mcp/&lt;ключ&gt;` — сервер знает, кто зовёт (спайк: путь доезжает, ключ виден в RouteValues).
/// Stateless: запрос разрешения держится открытым, пока человек не ответит.</summary>
public sealed class DeskMcpServer : IAsyncDisposable
{
    public const string TokenHeader = "X-Desk-Token";
    public const string ServerName = "desk";

    /// <summary>Сутки: без поля timeout claude обрывает тулзу после 300 с молчания (спайк),
    /// а разрешение ждёт человека сколько угодно.</summary>
    public const int ToolTimeoutMs = 86_400_000;

    private WebApplication? _app;

    /// <summary>Токен на запуск Desk: MCP-порт слушает localhost, но к нему может постучаться
    /// любой процесс бокса.</summary>
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public string? BaseUrl { get; private set; }

    public async Task StartAsync(PermissionBroker broker, CancellationToken ct = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(broker);
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<DeskTools>();

        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers[TokenHeader] != Token)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
        });
        app.MapMcp("/mcp/{key}");
        await app.StartAsync(ct).ConfigureAwait(false);
        BaseUrl = app.Urls.First().TrimEnd('/');
        _app = app;
    }

    public string EndpointFor(string key) => $"{BaseUrl}/mcp/{Uri.EscapeDataString(key)}";

    public string McpConfigJson(string key) => JsonSerializer.Serialize(new
    {
        mcpServers = new Dictionary<string, object>
        {
            [ServerName] = new
            {
                type = "http",
                url = EndpointFor(key),
                headers = new Dictionary<string, string> { [TokenHeader] = Token },
                timeout = ToolTimeoutMs,
            },
        },
    });

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _app = null;
    }
}

[McpServerToolType]
public sealed class DeskTools(PermissionBroker broker, IHttpContextAccessor http)
{
    [McpServerTool(Name = "permission_prompt"), Description("Запрос разрешения на инструмент у оператора SzDiag Desk")]
    public async Task<string> PermissionPrompt(string tool_name, JsonElement input, string? tool_use_id = null,
        CancellationToken ct = default)
    {
        var key = http.HttpContext?.Request.RouteValues["key"] as string ?? "";
        var verdict = await broker.AskAsync(key, tool_name, input, tool_use_id, ct).ConfigureAwait(false);
        return verdict.ToJson();
    }
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter "FullyQualifiedName~PermissionBroker|FullyQualifiedName~DeskMcpServer"`
Expected: 8 passed. Если `app.Urls` после старта отдаёт `:0` вместо настоящего порта — брать адрес из `app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()` (и записать ruling).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Claude tests/SzDiag.Claude.Tests/PermissionBrokerTests.cs tests/SzDiag.Claude.Tests/DeskMcpServerTests.cs
git commit -m "feat(claude): разрешения через MCP-тулзу permission_prompt"
```

---

### Task 5: `ClaudeSession` и `SessionManager`

**Files:**
- Create: `src/SzDiag.Claude/ClaudeSession.cs`, `SessionManager.cs`
- Create: `tests/SzDiag.Claude.Tests/FakeClaudeProcess.cs`, `SessionHarness.cs`, `ClaudeSessionTests.cs`

**Interfaces:**
- Consumes: всё из задач 1–4.
- Produces:
  - `enum SessionState { Stopped, Idle, Working, WaitingPermission, Crashed, Archived }`
  - `record SessionTimeouts(TimeSpan StopGrace, TimeSpan InterruptWait)` + `static Default` (5 с, 10 с)
  - `record SessionDeps(SessionIndex Index, TranscriptStore Transcripts, TokenLedger Tokens, PermissionBroker Broker, Func<string, string?, ClaudeLaunch?> LaunchFor, Func<IClaudeProcess> ProcessFactory, TimeProvider Time, SessionTimeouts Timeouts, Action<string>? Log = null)` — `LaunchFor(key, resumeSessionId)`, null — claude не найден.
  - `ClaudeSession` — `string Key`, `SessionState State`, `TokenUsage Usage`, `string? SessionId`, `IReadOnlyList<ClaudeEvent> History`, `IReadOnlyList<string> Queued`, `event Action? Changed`, `IReadOnlyList<ClaudeEvent> Attach(Action<ClaudeEvent> listener)`, `void Detach(Action<ClaudeEvent> listener)`, `Task SendAsync(string text)`, `Task<IReadOnlyList<string>> InterruptAsync()` (возвращает снятую очередь), `Task StopAsync()`, `Task ArchiveAsync()`, `Task DetachAsync()`, `void Restart()`, `void Note(string text)`.
  - `SessionManager(SessionDeps deps)` — `IReadOnlyList<SessionRecord> Records`, `ClaudeSession Create(string key)`, `ClaudeSession? Get(string key)` (поднимает объект по записи реестра, процесс не запускает), `ClaudeSession? Peek(string key)` (только уже поднятые), `Task StopAllAsync()`, `ValueTask DisposeAsync()`.

- [ ] **Step 1: Фейковый процесс и обвязка тестов**

`tests/SzDiag.Claude.Tests/FakeClaudeProcess.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Процесс без процесса: тест сам подаёт строки stdout и решает, когда «выйти».</summary>
public sealed class FakeClaudeProcess : IClaudeProcess
{
    public List<string> Written { get; } = new();
    public List<string> Stderr { get; } = new();
    public ClaudeLaunch? Launch { get; private set; }
    public bool Stopped { get; private set; }
    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> StderrTail => Stderr;

    public event Action<string>? OutputLine;
    public event Action<int?>? Exited;

    public void Start(ClaudeLaunch launch)
    {
        Launch = launch;
        IsRunning = true;
    }

    public Task WriteLineAsync(string line)
    {
        Written.Add(line);
        return Task.CompletedTask;
    }

    public Task StopAsync(TimeSpan grace)
    {
        Stopped = true;
        if (IsRunning) Exit(0);
        return Task.CompletedTask;
    }

    public void Emit(string line) => OutputLine?.Invoke(line);

    public void Exit(int? code)
    {
        IsRunning = false;
        Exited?.Invoke(code);
    }
}
```
`tests/SzDiag.Claude.Tests/SessionHarness.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

internal sealed class SessionHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "szclaude-" + Guid.NewGuid().ToString("N"));
    public List<FakeClaudeProcess> Processes { get; } = new();
    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public TokenLedger Tokens { get; }
    public SessionIndex Index { get; }
    public TranscriptStore Transcripts { get; }
    public SessionManager Manager { get; }
    public bool ClaudeFound { get; set; } = true;

    public SessionHarness(SessionTimeouts? timeouts = null)
    {
        Directory.CreateDirectory(Dir);
        Tokens = new TokenLedger(Path.Combine(Dir, "desk-tokens.json"), TimeProvider.System);
        Index = SessionIndex.Load(Path.Combine(Dir, "desk-sessions.json"));
        Transcripts = new TranscriptStore(Path.Combine(Dir, "sessions"));
        Manager = New(timeouts);
    }

    /// <summary>Ещё один менеджер над теми же файлами — «Desk перезапустили».</summary>
    public SessionManager New(SessionTimeouts? timeouts = null) => new(new SessionDeps(
        Index, Transcripts, Tokens, Broker,
        (key, resume) => ClaudeFound ? new ClaudeLaunch("claude.exe", Dir, null, resume, "вводная " + key, "mcp.json") : null,
        () =>
        {
            var p = new FakeClaudeProcess();
            Processes.Add(p);
            return p;
        },
        TimeProvider.System, timeouts ?? SessionTimeouts.Default));

    public FakeClaudeProcess Last => Processes[^1];

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
```

- [ ] **Step 2: Failing tests**

`tests/SzDiag.Claude.Tests/ClaudeSessionTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class ClaudeSessionTests : IDisposable
{
    private const string SpikeSession = "8c8e3bf5-ea25-4879-a3dc-566095eba936";
    private readonly SessionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Init => Fixture.Line("simple-turn.jsonl", e => e is SystemInit);
    private static string Text => Fixture.Line("simple-turn.jsonl", e => e is AssistantText);
    private static string Result => Fixture.Line("simple-turn.jsonl", e => e is TurnResult);
    private static string Hook => Fixture.Line("simple-turn.jsonl", e => e is ServiceEvent { Subtype: "hook_response" });
    private static string Aborted => Fixture.Line("interrupt-turn.jsonl", e => e is TurnResult { Interrupted: true });

    private static string UserText(string line)
        => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString()!;

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Send_StartsProcess_WritesUserMessage()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("проверь диски");

        Assert.Equal(SessionState.Working, s.State);
        Assert.Null(_h.Last.Launch!.ResumeSessionId);
        Assert.Equal("проверь диски", UserText(Assert.Single(_h.Last.Written)));
        Assert.IsType<DeskUserMessage>(Assert.Single(s.History));
    }

    [Fact]
    public async Task Init_StoresSessionId_NextLaunchResumes()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Init);
        _h.Last.Emit(Result);
        Assert.Equal(SpikeSession, s.SessionId);

        await s.StopAsync();
        Assert.Equal(SessionState.Stopped, s.State);
        await s.SendAsync("ещё");

        Assert.Equal(2, _h.Processes.Count);
        Assert.Equal(SpikeSession, _h.Last.Launch!.ResumeSessionId);
    }

    [Fact]
    public async Task SendWhileWorking_Queued_SentAfterResult()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("первое");
        await s.SendAsync("второе");
        Assert.Single(_h.Last.Written);
        Assert.Equal(new[] { "второе" }, s.Queued);

        _h.Last.Emit(Result);
        await WaitUntil(() => _h.Last.Written.Count == 2);
        Assert.Equal("второе", UserText(_h.Last.Written[1]));
        Assert.Empty(s.Queued);
        Assert.Equal(SessionState.Working, s.State);
    }

    [Fact]
    public async Task Result_CountsTokens_AndGoesIdle()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Result);

        Assert.Equal(5, s.Usage.Output);
        Assert.Equal(5, _h.Tokens.Today.Output);
        Assert.Equal(SessionState.Idle, s.State);
    }

    [Fact]
    public async Task Interrupt_WritesControlRequest_ReturnsQueue()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("считай до 400");
        await s.SendAsync("потом это");

        var stop = s.InterruptAsync();
        var ctl = JsonDocument.Parse(_h.Last.Written[^1]).RootElement;
        Assert.Equal("control_request", ctl.GetProperty("type").GetString());
        Assert.Equal("interrupt", ctl.GetProperty("request").GetProperty("subtype").GetString());

        _h.Last.Emit(Aborted);
        Assert.Equal(new[] { "потом это" }, await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Empty(s.Queued);
        Assert.Equal(2, _h.Last.Written.Count);   // «потом это» в claude не ушло
    }

    [Fact]
    public async Task Interrupt_NoResult_StopsProcessAfterTimeout()
    {
        using var h = new SessionHarness(new SessionTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200)));
        var s = h.Manager.Create("161432");
        await s.SendAsync("зависни");

        await s.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Last.Stopped);
        Assert.Equal(SessionState.Stopped, s.State);
        Assert.Contains(s.History, e => e is DeskNote n && n.Text.Contains("не остановился"));
    }

    [Fact]
    public async Task UnexpectedExit_Crashed_WithStderr_DeniesPermission()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("работай");
        var ask = _h.Broker.AskAsync("161432", "Bash", J("""{"command":"dir"}"""), "t1", default);
        _h.Last.Stderr.Add("Error: not logged in");
        _h.Last.Exit(1);

        Assert.Equal(SessionState.Crashed, s.State);
        var crash = Assert.Single(s.History.OfType<ProcessCrashed>());
        Assert.Equal(1, crash.ExitCode);
        Assert.Contains("not logged in", crash.StderrTail[0]);
        Assert.False((await ask.WaitAsync(TimeSpan.FromSeconds(5))).Allow);
    }

    [Fact]
    public async Task ClaudeNotFound_Crashed_MessageKept_RestartSends()
    {
        _h.ClaudeFound = false;
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");

        Assert.Equal(SessionState.Crashed, s.State);
        Assert.Contains("claude не найден", Assert.Single(s.History.OfType<ProcessCrashed>()).StderrTail[0]);
        Assert.Equal(new[] { "привет" }, s.Queued);

        _h.ClaudeFound = true;
        s.Restart();
        await WaitUntil(() => _h.Processes.Count == 1 && _h.Last.Written.Count == 1);
        Assert.Equal("привет", UserText(_h.Last.Written[0]));
    }

    [Fact]
    public async Task Permission_WaitingState_AnswerInHistory()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("создай файл");
        var ask = _h.Broker.AskAsync("161432", "Write", J("""{"file_path":"a.txt"}"""), "t1", default);

        Assert.Equal(SessionState.WaitingPermission, s.State);
        var asked = Assert.Single(s.History.OfType<PermissionAsked>());
        _h.Broker.Resolve(asked.RequestId, true);

        Assert.True((await ask).Allow);
        Assert.Equal(SessionState.Working, s.State);
        Assert.True(Assert.Single(s.History.OfType<PermissionAnswered>()).Allowed);
    }

    [Fact]
    public async Task PermissionForUnknownKey_DeniedImmediately()
    {
        var v = await _h.Broker.AskAsync("999999", "Bash", J("{}"), null, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(v.Allow);
    }

    [Fact]
    public async Task StopAll_DeniesPendingAndStopsProcesses()
    {
        var a = _h.Manager.Create("161432");
        var b = _h.Manager.Create("161501");
        await a.SendAsync("x");
        var pa = _h.Last;
        await b.SendAsync("y");
        var pb = _h.Last;
        var ask = _h.Broker.AskAsync("161432", "Bash", J("{}"), null, default);

        await _h.Manager.StopAllAsync();

        Assert.True(pa.Stopped);
        Assert.True(pb.Stopped);
        Assert.False((await ask.WaitAsync(TimeSpan.FromSeconds(5))).Allow);
        Assert.Equal(SessionState.Stopped, a.State);
        Assert.Equal(SessionState.Stopped, b.State);
        Assert.DoesNotContain(a.History, e => e is ProcessCrashed);   // штатная остановка — не падение
    }

    [Fact]
    public async Task Archive_ThenSend_Resumes()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Init);
        _h.Last.Emit(Result);

        await s.ArchiveAsync();
        Assert.Equal(SessionState.Archived, s.State);
        Assert.True(_h.Index.Get("161432")!.Archived);

        await s.SendAsync("продолжим");
        Assert.False(_h.Index.Get("161432")!.Archived);
        Assert.Equal(SpikeSession, _h.Last.Launch!.ResumeSessionId);
    }

    [Fact]
    public void Create_Twice_SameSession()
        => Assert.Same(_h.Manager.Create("161432"), _h.Manager.Create("161432"));

    [Fact]
    public async Task History_ReplayedFromTranscript_WithoutServiceLines()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Hook);
        _h.Last.Emit(Init);
        _h.Last.Emit(Text);
        _h.Last.Emit(Result);

        var replayed = _h.New().Get("161432")!;
        Assert.IsType<DeskUserMessage>(replayed.History[0]);
        Assert.Contains(replayed.History, e => e is AssistantText { Text: "Привет" });
        Assert.DoesNotContain("hook_response", File.ReadAllText(_h.Transcripts.PathFor("161432")));
    }

    [Fact]
    public async Task Attach_GivesHistoryThenLiveEvents()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        var live = new List<ClaudeEvent>();
        var snapshot = s.Attach(live.Add);
        _h.Last.Emit(Text);

        Assert.IsType<DeskUserMessage>(Assert.Single(snapshot));
        Assert.IsType<AssistantText>(Assert.Single(live));
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests --filter FullyQualifiedName~ClaudeSessionTests`
Expected: ошибка компиляции — `SessionManager`, `SessionDeps` не найдены.

- [ ] **Step 4: Реализация**

`src/SzDiag.Claude/ClaudeSession.cs`:
```csharp
namespace SzDiag.Claude;

public enum SessionState { Stopped, Idle, Working, WaitingPermission, Crashed, Archived }

public sealed record SessionTimeouts(TimeSpan StopGrace, TimeSpan InterruptWait)
{
    public static SessionTimeouts Default { get; } = new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
}

/// <param name="LaunchFor">(ключ, session_id для --resume) → параметры запуска; null — claude не найден.</param>
public sealed record SessionDeps(
    SessionIndex Index,
    TranscriptStore Transcripts,
    TokenLedger Tokens,
    PermissionBroker Broker,
    Func<string, string?, ClaudeLaunch?> LaunchFor,
    Func<IClaudeProcess> ProcessFactory,
    TimeProvider Time,
    SessionTimeouts Timeouts,
    Action<string>? Log = null);

/// <summary>Одна сессия Claude (спека 2026-09-25: 1 СЗ = 1 сессия). Процесс поднимается при первом
/// сообщении, а не при открытии чата: до первого сообщения `claude -p` ничего не делает, а
/// поднятый впустую — это хуки SessionStart и память. Очередь сообщений — у сессии, не у `claude`:
/// так «■» может вернуть неотправленное в поле ввода.</summary>
public sealed class ClaudeSession
{
    private readonly object _gate = new();
    private readonly SessionDeps _d;
    private readonly List<ClaudeEvent> _history;
    private readonly Queue<string> _queue = new();
    private readonly HashSet<string> _permissions = new(StringComparer.Ordinal);
    private IClaudeProcess? _process;
    private bool _stopping;
    private bool _archiving;
    private TaskCompletionSource<bool>? _interrupt;
    private Action<ClaudeEvent>? _listener;

    internal ClaudeSession(string key, SessionDeps deps)
    {
        Key = key;
        _d = deps;
        _history = deps.Transcripts.Load(key).Where(e => e is not (ServiceEvent or ParseError)).ToList();
        State = deps.Index.Get(key)?.Archived == true ? SessionState.Archived : SessionState.Stopped;
    }

    public string Key { get; }

    public SessionState State { get; private set; }

    /// <summary>Токены этой сессии с запуска Desk.</summary>
    public TokenUsage Usage { get; private set; } = TokenUsage.Zero;

    public string? SessionId => _d.Index.Get(Key)?.SessionId;

    public IReadOnlyList<ClaudeEvent> History
    {
        get { lock (_gate) return _history.ToList(); }
    }

    public IReadOnlyList<string> Queued
    {
        get { lock (_gate) return _queue.ToList(); }
    }

    /// <summary>Состояние или очередь изменились. Может прийти с потока процесса — UI маршалит сам.</summary>
    public event Action? Changed;

    /// <summary>История на момент подписки + всё, что придёт после, без дыр между ними.
    /// Слушатель вызывается под локом сессии: он должен только поставить работу в очередь UI.</summary>
    public IReadOnlyList<ClaudeEvent> Attach(Action<ClaudeEvent> listener)
    {
        lock (_gate)
        {
            _listener += listener;
            return _history.ToList();
        }
    }

    public void Detach(Action<ClaudeEvent> listener)
    {
        lock (_gate) _listener -= listener;
    }

    public Task SendAsync(string text)
    {
        lock (_gate)
        {
            if (State == SessionState.Archived) SetArchivedLocked(false);
            _queue.Enqueue(text);
        }
        Changed?.Invoke();
        return PumpQueueAsync();
    }

    /// <summary>Отправить следующее из очереди, если сессия свободна.</summary>
    private async Task PumpQueueAsync()
    {
        IClaudeProcess? process = null;
        var text = "";
        bool send;
        lock (_gate)
        {
            send = _queue.Count > 0 && _interrupt is null
                   && State is SessionState.Stopped or SessionState.Idle
                   && (_process is { IsRunning: true } || TryStartLocked());
            if (send)
            {
                process = _process!;
                text = _queue.Dequeue();
                AddLocked(new DeskUserMessage(text) { At = _d.Time.GetUtcNow() });
                State = SessionState.Working;
            }
        }
        Changed?.Invoke();
        if (!send) return;

        try
        {
            await process!.WriteLineAsync(ClaudeInput.UserMessage(text)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            lock (_gate) CrashLocked(null, new[] { $"запись в claude не удалась: {ex.Message}" });
            Changed?.Invoke();
        }
    }

    private bool TryStartLocked()
    {
        var launch = _d.LaunchFor(Key, SessionId);
        if (launch is null)
        {
            CrashLocked(null, new[] { "claude не найден: укажи ClaudePath в appsettings.json Desk или добавь claude.exe в PATH" });
            return false;
        }
        var p = _d.ProcessFactory();
        p.OutputLine += OnLine;
        p.Exited += code => OnExited(p, code);
        try
        {
            p.Start(launch);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            CrashLocked(null, new[] { $"claude не запустился: {ex.Message}" });
            return false;
        }
        _process = p;
        _stopping = false;
        return true;
    }

    private void OnLine(string line)
    {
        var events = StreamJsonParser.Parse(line, _d.Time.GetUtcNow());
        // Служебные строки (хуки SessionStart — десятки КБ на запуск) в журнал не пишем.
        if (events.Any(e => e is not (ServiceEvent or ParseError))) _d.Transcripts.Append(Key, line);

        var turnEnded = false;
        lock (_gate)
        {
            foreach (var e in events)
            {
                switch (e)
                {
                    case ServiceEvent:
                        continue;
                    case ParseError pe:
                        _d.Log?.Invoke($"сессия {Key}: битая строка stream-json пропущена ({pe.Message})");
                        continue;
                    case SystemInit init:
                        RememberSessionIdLocked(init.SessionId);
                        break;
                    case TurnResult r:
                        Usage = Usage.Add(r.Usage);
                        _d.Tokens.Add(r.Usage, r.CostUsd);
                        State = SessionState.Idle;
                        turnEnded = true;
                        break;
                }
                AddLocked(e);
            }
            if (turnEnded) _interrupt?.TrySetResult(true);
        }
        Changed?.Invoke();
        if (turnEnded) _ = PumpQueueAsync();
    }

    private void OnExited(IClaudeProcess p, int? code)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(p, _process)) return;
            _process = null;
            if (_stopping)
            {
                if (State != SessionState.Archived) State = SessionState.Stopped;
            }
            else
            {
                CrashLocked(code, p.StderrTail.TakeLast(20).ToList());
            }
            _interrupt?.TrySetResult(false);
        }
        _d.Broker.DenyAll(Key, "сессия остановлена");
        Changed?.Invoke();
    }

    /// <summary>«■»: прервать текущий ход. Возвращает неотправленную очередь — её место в поле ввода.
    /// Если `claude` не закрыл ход за <see cref="SessionTimeouts.InterruptWait"/> (инструмент
    /// завис), процесс останавливается: окно не должно висеть вместе с ним.</summary>
    public async Task<IReadOnlyList<string>> InterruptAsync()
    {
        IClaudeProcess? process = null;
        TaskCompletionSource<bool>? done = null;
        List<string> back;
        bool active;
        lock (_gate)
        {
            back = _queue.ToList();
            _queue.Clear();
            active = _process is not null && State is SessionState.Working or SessionState.WaitingPermission;
            if (active)
            {
                process = _process;
                done = _interrupt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        Changed?.Invoke();
        if (!active) return back;

        // Висящий запрос разрешения держит ход — сначала отказ, потом interrupt.
        _d.Broker.DenyAll(Key, "ход прерван оператором");
        try
        {
            await process!.WriteLineAsync(ClaudeInput.Interrupt($"int-{Guid.NewGuid():N}")).ConfigureAwait(false);
            var wait = _d.Timeouts.InterruptWait;
            if (await Task.WhenAny(done!.Task, Task.Delay(wait)).ConfigureAwait(false) != done.Task)
            {
                Note($"ход не остановился за {wait.TotalSeconds:N0} с — процесс остановлен, разговор продолжится через --resume");
                await StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_gate) _interrupt = null;
        }
        return back;
    }

    /// <summary>Штатная остановка процесса (закрытие Desk, архив, «открыть в терминале»). Не падение.</summary>
    public async Task StopAsync()
    {
        IClaudeProcess? p;
        lock (_gate)
        {
            p = _process;
            _stopping = true;
        }
        _d.Broker.DenyAll(Key, "сессия остановлена");
        if (p is not null) await p.StopAsync(_d.Timeouts.StopGrace).ConfigureAwait(false);
        lock (_gate)
        {
            if (ReferenceEquals(_process, p)) _process = null;
            if (State != SessionState.Archived) State = SessionState.Stopped;
        }
        Changed?.Invoke();
    }

    /// <summary>СЗ закрыта: процесс останавливается, сессия читается как архив; новое сообщение
    /// продолжит её через --resume.</summary>
    public async Task ArchiveAsync()
    {
        lock (_gate)
        {
            if (_archiving || State == SessionState.Archived) return;
            _archiving = true;
        }
        try
        {
            await StopAsync().ConfigureAwait(false);
            lock (_gate)
            {
                SetArchivedLocked(true);
                AddLocked(new DeskNote("СЗ закрыта — сессия в архиве; новое сообщение продолжит её через --resume")
                    { At = _d.Time.GetUtcNow() });
            }
            Changed?.Invoke();
        }
        finally
        {
            lock (_gate) _archiving = false;
        }
    }

    /// <summary>Перед «открыть в терминале»: двух писателей в одну сессию быть не должно.</summary>
    public async Task DetachAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Note("сессия открыта в терминале; пока он открыт, здесь не пиши — у разговора будет два писателя");
    }

    /// <summary>После падения: снять «упала» и отправить то, что ждёт в очереди.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            if (State == SessionState.Crashed) State = SessionState.Stopped;
        }
        Changed?.Invoke();
        _ = PumpQueueAsync();
    }

    public void Note(string text)
    {
        lock (_gate) AddLocked(new DeskNote(text) { At = _d.Time.GetUtcNow() });
        Changed?.Invoke();
    }

    internal void OnPermissionAsked(PendingPermission p)
    {
        lock (_gate)
        {
            _permissions.Add(p.RequestId);
            AddLocked(new PermissionAsked(p.RequestId, p.ToolName, p.Input, p.ToolUseId) { At = p.At });
            if (State == SessionState.Working) State = SessionState.WaitingPermission;
        }
        Changed?.Invoke();
    }

    internal void OnPermissionAnswered(string requestId, bool allowed)
    {
        lock (_gate)
        {
            if (!_permissions.Remove(requestId)) return;
            AddLocked(new PermissionAnswered(requestId, allowed) { At = _d.Time.GetUtcNow() });
            if (State == SessionState.WaitingPermission && _permissions.Count == 0) State = SessionState.Working;
        }
        Changed?.Invoke();
    }

    private void AddLocked(ClaudeEvent e)
    {
        _history.Add(e);
        if (DeskLines.Serialize(e) is { } line) _d.Transcripts.Append(Key, line);
        _listener?.Invoke(e);
    }

    private void CrashLocked(int? code, IReadOnlyList<string> tail)
    {
        AddLocked(new ProcessCrashed(code, tail) { At = _d.Time.GetUtcNow() });
        State = SessionState.Crashed;
    }

    private void RememberSessionIdLocked(string sessionId)
    {
        if (sessionId.Length == 0) return;
        var r = _d.Index.Get(Key);
        if (r?.SessionId is not null) return;   // при --resume id не меняется (спайк)
        _d.Index.Put((r ?? new SessionRecord(Key, null, _d.Time.GetUtcNow(), false)) with { SessionId = sessionId });
    }

    private void SetArchivedLocked(bool archived)
    {
        var r = _d.Index.Get(Key) ?? new SessionRecord(Key, null, _d.Time.GetUtcNow(), false);
        _d.Index.Put(r with { Archived = archived });
        State = archived ? SessionState.Archived : SessionState.Stopped;
    }
}
```
`src/SzDiag.Claude/SessionManager.cs`:
```csharp
using System.Collections.Concurrent;

namespace SzDiag.Claude;

/// <summary>Правило «1 ключ = 1 сессия» и маршрутизация разрешений по ключу. Объект сессии
/// поднимается лениво по записи реестра; процесс — только при первом сообщении.</summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClaudeSession> _sessions = new(StringComparer.Ordinal);
    private readonly SessionDeps _d;

    public SessionManager(SessionDeps deps)
    {
        _d = deps;
        deps.Broker.Requested += p =>
        {
            if (Get(p.Key) is { } s) s.OnPermissionAsked(p);
            // Ключа нет в Desk — спросить некого; молчание повесило бы ход навсегда.
            else deps.Broker.Resolve(p.RequestId, false, "нет сессии Desk для этого ключа");
        };
        deps.Broker.Resolved += (id, allowed) =>
        {
            foreach (var s in _sessions.Values) s.OnPermissionAnswered(id, allowed);
        };
    }

    public IReadOnlyList<SessionRecord> Records => _d.Index.All;

    /// <summary>«Начать сессию»: запись в реестре без session_id; повторный вызов — та же сессия.</summary>
    public ClaudeSession Create(string key)
    {
        if (_d.Index.Get(key) is null) _d.Index.Put(new SessionRecord(key, null, _d.Time.GetUtcNow(), false));
        return _sessions.GetOrAdd(key, k => new ClaudeSession(k, _d));
    }

    public ClaudeSession? Get(string key)
    {
        if (_sessions.TryGetValue(key, out var s)) return s;
        return _d.Index.Get(key) is null ? null : _sessions.GetOrAdd(key, k => new ClaudeSession(k, _d));
    }

    /// <summary>Только уже поднятые — без чтения журнала с диска (для частых опросов из UI).</summary>
    public ClaudeSession? Peek(string key) => _sessions.TryGetValue(key, out var s) ? s : null;

    public Task StopAllAsync() => Task.WhenAll(_sessions.Values.Select(s => s.StopAsync()));

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);
}
```

- [ ] **Step 5: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Claude.Tests`
Expected: всё зелёное (15 новых).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Claude tests/SzDiag.Claude.Tests/FakeClaudeProcess.cs tests/SzDiag.Claude.Tests/SessionHarness.cs tests/SzDiag.Claude.Tests/ClaudeSessionTests.cs
git commit -m "feat(claude): сессия и менеджер — очередь, прерывание, журнал, архив"
```

---

### Task 6: Desk — хост ядра, вводная, терминал, конфиг

**Files:**
- Create: `src/SzDiag.Desk/Services/SzBriefing.cs`, `DeskClaudeHost.cs`, `TerminalLauncher.cs`, `ChatServices.cs`
- Create: `tests/SzDiag.Desk.Tests/DeskClaudeHostTests.cs`
- Modify: `src/SzDiag.Desk/SzDiag.Desk.csproj`, `src/SzDiag.Desk/Services/DeskOptions.cs`, `src/SzDiag.Desk/appsettings.json`, `tests/SzDiag.Desk.Tests/SzDiag.Desk.Tests.csproj`, `tools/build-dist.ps1`

**Interfaces:**
- Consumes: `SessionManager`, `SessionDeps`, `PermissionBroker`, `DeskMcpServer`, `TokenLedger`, `SessionIndex`, `TranscriptStore`, `ClaudeLaunch`, `ClaudeLocator`, `ClaudeProcess` (задачи 1–5).
- Produces: `DeskOptions.ClaudePath`, `ClaudeWorkDir`, `ClaudeConfigDir` (string, по умолчанию ""); `static string SzBriefing.For(string sz)`; `interface ITerminalLauncher { bool Open(string key, string sessionId); }`, `TerminalLauncher(string? claudeExe, string workDir, string? configDir, string scriptDir)` + `static string Script(string claudeExe, string workDir, string? configDir, string sessionId)`; `record ChatServices(SessionManager Sessions, PermissionBroker Broker, TokenLedger Tokens, ITerminalLauncher Terminal, Action<Action> Ui)`; `DeskClaudeHost` — `static Task<DeskClaudeHost> StartAsync(DeskOptions o, string baseDir)`, `ClaudeLaunch? LaunchFor(string key, string? resumeId)`, `ChatServices Services(Action<Action> ui)`, `static string FindRepoRoot(string start)`, `PermissionBroker Broker`, `SessionManager Sessions`, `TokenLedger Tokens`, `ValueTask DisposeAsync()`.

- [ ] **Step 1: Ссылки проектов**

В `src/SzDiag.Desk/SzDiag.Desk.csproj` в первый `<ItemGroup>`:
```xml
    <ProjectReference Include="..\SzDiag.Claude\SzDiag.Claude.csproj" />
```
В `tests/SzDiag.Desk.Tests/SzDiag.Desk.Tests.csproj` в `<ItemGroup>` со ссылками:
```xml
    <ProjectReference Include="..\..\src\SzDiag.Claude\SzDiag.Claude.csproj" />
```
и новый `<ItemGroup>` — фикстуры спайка общие с `SzDiag.Claude.Tests`:
```xml
  <ItemGroup>
    <None Include="..\SzDiag.Claude.Tests\Fixtures\*.jsonl" Link="Fixtures\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```
Run: `"C:\Program Files\dotnet\dotnet.exe" build src/SzDiag.Desk`
Expected: без ошибок. Если NuGet ругается `NU1605` на `Microsoft.Extensions.Configuration.*` 8.0.x против 10.x из MCP SDK — поднять `Microsoft.Extensions.Configuration.Binder`/`.Json` в Desk до версии из сообщения и записать ruling.

- [ ] **Step 2: Failing tests**

`tests/SzDiag.Desk.Tests/DeskClaudeHostTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class DeskClaudeHostTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szdesk-" + Guid.NewGuid().ToString("N"));
    private DeskClaudeHost _host = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var exe = Path.Combine(_dir, "claude.exe");
        File.WriteAllText(exe, "");
        _host = await DeskClaudeHost.StartAsync(
            new DeskOptions { ClaudePath = exe, ClaudeWorkDir = _dir, ClaudeConfigDir = "C:\\cfg2" }, _dir);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void LaunchFor_WritesMcpConfig_PassesConfigDirAndResume()
    {
        var l = _host.LaunchFor("161432", "sid-1")!;
        Assert.Equal("sid-1", l.ResumeSessionId);
        Assert.Equal("C:\\cfg2", l.ConfigDir);
        Assert.Equal(_dir, l.WorkDir);
        Assert.Contains("161432", l.AppendSystemPrompt);
        Assert.StartsWith(Path.Combine(_dir, "run"), l.McpConfigPath);

        var desk = JsonDocument.Parse(File.ReadAllText(l.McpConfigPath)).RootElement
            .GetProperty("mcpServers").GetProperty("desk");
        Assert.EndsWith("/mcp/161432", desk.GetProperty("url").GetString());
        Assert.Equal(86_400_000, desk.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task NoClaude_LaunchNull()
    {
        await using var h = await DeskClaudeHost.StartAsync(
            new DeskOptions { ClaudePath = Path.Combine(_dir, "нет.exe") }, _dir);
        Assert.Null(h.LaunchFor("161432", null));
    }

    [Fact]
    public void FindRepoRoot_WalksUpToSolution()
    {
        var nested = Path.Combine(_dir, "a", "b");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_dir, "SzDiag.sln"), "");
        Assert.Equal(_dir, DeskClaudeHost.FindRepoRoot(nested));
    }

    [Fact]
    public void Briefing_BindsSessionToSz()
    {
        var b = SzBriefing.For("161432");
        Assert.Contains("szcli", b);
        Assert.Contains("kb/СЗ/161432", b);
        Assert.Contains("161432", b.Split('\n')[0]);
    }

    [Fact]
    public void TerminalScript_SetsProfileWorkDirAndResume()
    {
        var s = TerminalLauncher.Script("C:\\bin\\claude.exe", "C:\\repo", "C:\\cfg2", "sid-1");
        Assert.StartsWith("@echo off", s);
        Assert.Contains("set \"CLAUDE_CONFIG_DIR=C:\\cfg2\"", s);
        Assert.Contains("cd /d \"C:\\repo\"", s);
        Assert.Contains("\"C:\\bin\\claude.exe\" --resume sid-1", s);
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", TerminalLauncher.Script("C:\\bin\\claude.exe", "C:\\repo", null, "sid-1"));
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~DeskClaudeHost`
Expected: ошибка компиляции.

- [ ] **Step 4: Реализация**

`src/SzDiag.Desk/Services/DeskOptions.cs` — добавить свойства после `KbRoot`:
```csharp
    /// <summary>Путь к claude.exe; пусто — поиск в PATH.</summary>
    public string ClaudePath { get; set; } = "";

    /// <summary>Рабочий каталог сессий — корень репозитория sz-diag (CLAUDE.md, скиллы, память).
    /// Пусто — вверх от exe до SzDiag.sln.</summary>
    public string ClaudeWorkDir { get; set; } = "";

    /// <summary>CLAUDE_CONFIG_DIR для claude. На боксе профиль задаёт обёртка claude2.cmd, а не
    /// переменная пользователя: без этого Desk из Проводника поднял бы claude с чужим профилем.</summary>
    public string ClaudeConfigDir { get; set; } = "";
```
`src/SzDiag.Desk/appsettings.json`:
```json
{
  "HubBaseUrl": "http://localhost:5000",
  "ManagementToken": "dev-token",
  "KbRoot": "kb",
  "ClaudePath": "",
  "ClaudeWorkDir": "",
  "ClaudeConfigDir": ""
}
```
`src/SzDiag.Desk/Services/SzBriefing.cs`:
```csharp
namespace SzDiag.Desk.Services;

/// <summary>Вводная сессии (`--append-system-prompt`): всё про СЗ живёт в Desk, ядро SzDiag.Claude
/// знает только ключ.</summary>
public static class SzBriefing
{
    public static string For(string sz) => $"""
        Ты ведёшь сервисную заявку (СЗ) {sz} в SzDiag Desk — окне оператора сервисного центра.
        Сессия привязана к одной СЗ: работай только с машиной {sz} (`szcli … {sz}`), другие СЗ не трогай.
        Оператор читает ленту в окне Desk, а не терминал: отвечай кратко, по-русски, итог — в конце хода.
        Факты по ходу пиши в kb/СЗ/{sz}/ (діагностика.md, дії.md) — на украинском, как требует CLAUDE.md.
        Разрешения на инструменты оператор подтверждает кнопкой в окне; отказ не обходи другим способом.
        """;
}
```
`src/SzDiag.Desk/Services/TerminalLauncher.cs`:
```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace SzDiag.Desk.Services;

public interface ITerminalLauncher
{
    bool Open(string key, string sessionId);
}

/// <summary>«Открыть в терминале»: `claude --resume` в Windows Terminal, без него — в обычной
/// консоли. Через .cmd-файл, а не аргументами: у уже запущенного Windows Terminal новая вкладка
/// получает окружение его процесса, и CLAUDE_CONFIG_DIR из Desk туда не доехал бы.</summary>
public sealed class TerminalLauncher(string? claudeExe, string workDir, string? configDir, string scriptDir)
    : ITerminalLauncher
{
    public static string Script(string claudeExe, string workDir, string? configDir, string sessionId)
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\r\n");
        sb.Append("chcp 65001 >nul\r\n");
        if (!string.IsNullOrEmpty(configDir)) sb.Append($"set \"CLAUDE_CONFIG_DIR={configDir}\"\r\n");
        sb.Append($"cd /d \"{workDir}\"\r\n");
        sb.Append($"\"{claudeExe}\" --resume {sessionId}\r\n");
        return sb.ToString();
    }

    public bool Open(string key, string sessionId)
    {
        if (claudeExe is null) return false;
        string path;
        try
        {
            Directory.CreateDirectory(scriptDir);
            path = Path.Combine(scriptDir, $"resume-{key}.cmd");
            File.WriteAllText(path, Script(claudeExe, workDir, configDir, sessionId), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        return TryStart("wt.exe", $"-d \"{workDir}\" cmd.exe /k \"{path}\"")
               || TryStart("cmd.exe", $"/c start \"claude {key}\" cmd.exe /k \"{path}\"");
    }

    private static bool TryStart(string exe, string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false });
            return p is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
```
`src/SzDiag.Desk/Services/ChatServices.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <param name="Ui">Выполнить действие в UI-потоке (события сессий приходят с потоков процессов).</param>
public sealed record ChatServices(SessionManager Sessions, PermissionBroker Broker, TokenLedger Tokens,
    ITerminalLauncher Terminal, Action<Action> Ui);
```
`src/SzDiag.Desk/Services/DeskClaudeHost.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <summary>Ядро SzDiag.Claude, собранное под конфиг Desk: MCP-сервер, брокер разрешений,
/// реестр и журналы сессий рядом с exe, конфиг MCP на каждый запуск процесса.</summary>
public sealed class DeskClaudeHost : IAsyncDisposable
{
    private readonly string _runDir;

    private DeskClaudeHost(DeskOptions o, string baseDir)
    {
        ClaudeExe = ClaudeLocator.Resolve(o.ClaudePath, Environment.GetEnvironmentVariable("PATH"));
        WorkDir = string.IsNullOrWhiteSpace(o.ClaudeWorkDir) ? FindRepoRoot(baseDir) : o.ClaudeWorkDir;
        ConfigDir = string.IsNullOrWhiteSpace(o.ClaudeConfigDir) ? null : o.ClaudeConfigDir;
        _runDir = Path.Combine(baseDir, "run");
        Tokens = new TokenLedger(Path.Combine(baseDir, "desk-tokens.json"), TimeProvider.System);
        Sessions = new SessionManager(new SessionDeps(
            SessionIndex.Load(Path.Combine(baseDir, "desk-sessions.json")),
            new TranscriptStore(Path.Combine(baseDir, "sessions")),
            Tokens, Broker, LaunchFor, () => new ClaudeProcess(), TimeProvider.System,
            SessionTimeouts.Default, DeskLog.Write));
    }

    public string? ClaudeExe { get; }
    public string WorkDir { get; }
    public string? ConfigDir { get; }
    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public DeskMcpServer Mcp { get; } = new();
    public TokenLedger Tokens { get; }
    public SessionManager Sessions { get; }

    public static async Task<DeskClaudeHost> StartAsync(DeskOptions o, string baseDir)
    {
        var host = new DeskClaudeHost(o, baseDir);
        await host.Mcp.StartAsync(host.Broker).ConfigureAwait(false);
        DeskLog.Write($"claude: {host.ClaudeExe ?? "не найден"}, каталог {host.WorkDir}, профиль {host.ConfigDir ?? "унаследован"}, MCP {host.Mcp.BaseUrl}");
        return host;
    }

    /// <summary>Конфиг MCP пишется заново на каждый запуск: порт и токен сервера меняются с
    /// каждым запуском Desk.</summary>
    public ClaudeLaunch? LaunchFor(string key, string? resumeId)
    {
        if (ClaudeExe is null) return null;
        Directory.CreateDirectory(_runDir);
        var config = Path.Combine(_runDir, $"{key}.mcp.json");
        File.WriteAllText(config, Mcp.McpConfigJson(key));
        return new ClaudeLaunch(ClaudeExe, WorkDir, ConfigDir, resumeId, SzBriefing.For(key), config);
    }

    public ChatServices Services(Action<Action> ui)
        => new(Sessions, Broker, Tokens, new TerminalLauncher(ClaudeExe, WorkDir, ConfigDir, _runDir), ui);

    public static string FindRepoRoot(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SzDiag.sln"))) return d.FullName;
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        await Sessions.StopAllAsync().ConfigureAwait(false);
        await Mcp.DisposeAsync().ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное (5 новых).

- [ ] **Step 6: build-dist**

В `tools/build-dist.ps1` блок `$deskCfg` заменить (правка через Edit, не heredoc — CLAUDE.md, «Рецепты»):
```powershell
# Desk — тот же hub и тот же токен, что у CLI: окно и szcli обязаны видеть одно и то же.
# Сессии Claude работают из корня репозитория (CLAUDE.md, скиллы) и с профилем, под которым
# собирали dist: на боксе его задаёт обёртка claude2.cmd, а Desk запускают из Проводника.
$claudeWork = ("$root").Replace('\', '\\')
$claudeCfg = ("$env:CLAUDE_CONFIG_DIR").Replace('\', '\\')
$deskCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb",
  "ClaudeWorkDir": "$claudeWork",
  "ClaudeConfigDir": "$claudeCfg"
}
"@
```
(строки `if ((Test-Path dist\host\desk) …` не трогать). Файл остаётся UTF-8 с BOM, CRLF.

Проверка без пересборки боевого dist (он перезаписал бы hub/агента сборкой ветки): вырезать блок в scratch-скрипт с `$root`, `$Port`, `$Token`, `$kb` и вывести `$deskCfg`:
Run: `pwsh -NoProfile -Command '$root="C:\r"; $Port=5099; $Token="t"; $kb="C:\\k"; $env:CLAUDE_CONFIG_DIR="C:\Users\x\.claude2"; <блок>; $deskCfg | ConvertFrom-Json | Format-List'`
Expected: `ClaudeWorkDir : C:\r`, `ClaudeConfigDir : C:\Users\x\.claude2`, JSON разбирается.

- [ ] **Step 7: Commit**

```bash
git add src/SzDiag.Desk/SzDiag.Desk.csproj src/SzDiag.Desk/appsettings.json src/SzDiag.Desk/Services tests/SzDiag.Desk.Tests/SzDiag.Desk.Tests.csproj tests/SzDiag.Desk.Tests/DeskClaudeHostTests.cs tools/build-dist.ps1
git commit -m "feat(desk): хост сессий Claude — MCP, вводная по СЗ, терминал, конфиг"
```

---

### Task 7: Лента — модели карточек и сборщик

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/FeedItems.cs`, `ToolSummary.cs`, `FeedBuilder.cs`
- Create: `tests/SzDiag.Desk.Tests/Fixture.cs`, `FeedBuilderTests.cs`

**Interfaces:**
- Consumes: события `SzDiag.Claude` (задача 1).
- Produces: `abstract class FeedItemViewModel : ObservableObject { DateTimeOffset? At }`; `UserFeedItem(string Text)`, `AssistantFeedItem(string Text)`, `NoteFeedItem(string Text)`, `RawFeedItem(Title, Raw)`, `CrashFeedItem(Title, Details, IRelayCommand RestartCommand)`, `enum ToolStatus { Running, Ok, Error, Denied }`, `ToolFeedItem` (`Id`, `Name`, `Summary`, `Status`, `Duration`, `SubSteps`, `Output`, `IsExpanded`, `Header`, `ToggleCommand`), `PermissionFeedItem` (`RequestId`, `ToolName`, `Summary`, `Title`, `bool? Allowed`, `IsPending`, `ResultText`, `AllowCommand`, `DenyCommand`, `internal void Expire()`); `static string ToolSummary.For(string tool, JsonElement input)`, `const int ToolSummary.Max = 100`; `FeedBuilder(Action<string, bool> answer, Action restart)` — `ObservableCollection<FeedItemViewModel> Items`, `void Add(ClaudeEvent e)`, `void ExpirePermissionsExcept(IEnumerable<string> liveIds)`, `const int MaxOutputChars = 20_000`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/Fixture.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Desk.Tests;

/// <summary>Фикстуры спайка (линк из SzDiag.Claude.Tests/Fixtures).</summary>
internal static class Fixture
{
    public static IReadOnlyList<string> Lines(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Where(l => l.Trim().Length > 0).ToList();

    public static IReadOnlyList<ClaudeEvent> Events(string name)
        => Lines(name).SelectMany(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch)).ToList();

    public static string Line(string name, Func<ClaudeEvent, bool> match)
        => Lines(name).First(l => StreamJsonParser.Parse(l, DateTimeOffset.UnixEpoch).Any(match));
}
```
`tests/SzDiag.Desk.Tests/FeedBuilderTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Claude;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class FeedBuilderTests
{
    private readonly List<(string Id, bool Allow)> _answers = new();
    private int _restarts;

    private FeedBuilder New() => new((id, allow) => _answers.Add((id, allow)), () => _restarts++);

    private FeedBuilder From(string fixture)
    {
        var f = New();
        foreach (var e in Fixture.Events(fixture)) f.Add(e);
        return f;
    }

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void SimpleTurn_OnlyAssistantText()
        => Assert.Equal("Привет", Assert.IsType<AssistantFeedItem>(Assert.Single(From("simple-turn.jsonl").Items)).Text);

    [Fact]
    public void ToolTurn_CardOk_WithSummaryOutputDuration()
    {
        var f = From("tool-turn.jsonl");
        var card = Assert.Single(f.Items.OfType<ToolFeedItem>());
        Assert.Equal(ToolStatus.Ok, card.Status);
        Assert.Contains("szcli", card.Summary);
        Assert.Contains("szcli 1.0.0", card.Output);
        Assert.StartsWith("✓ PowerShell · ", card.Header);
        Assert.NotNull(card.Duration);
        Assert.IsType<AssistantFeedItem>(f.Items[^1]);
    }

    [Fact]
    public void DenyTurn_CardDenied()
        => Assert.StartsWith("⊘ Write", Assert.Single(From("permission-deny-turn.jsonl").Items.OfType<ToolFeedItem>()).Header);

    [Fact]
    public void InterruptTurn_NoteThenNextAnswer()
    {
        var f = From("interrupt-turn.jsonl");
        Assert.Contains(f.Items, i => i is NoteFeedItem { Text: "ход прерван" });
        Assert.Equal("Жив.", Assert.IsType<AssistantFeedItem>(f.Items[^1]).Text);
    }

    [Fact]
    public void Permission_AskAnswer()
    {
        var f = New();
        f.Add(new PermissionAsked("r1", "Bash", J("""{"command":"dir C:\\"}"""), "t1"));
        var card = Assert.IsType<PermissionFeedItem>(Assert.Single(f.Items));
        Assert.True(card.IsPending);
        Assert.Equal("Разрешить Bash?", card.Title);
        Assert.Equal("dir C:\\", card.Summary);

        card.AllowCommand.Execute(null);
        Assert.Equal(("r1", true), Assert.Single(_answers));

        f.Add(new PermissionAnswered("r1", true));
        Assert.False(card.IsPending);
        Assert.Equal("разрешено", card.ResultText);
    }

    [Fact]
    public void StalePermission_Expired()
    {
        var f = New();
        f.Add(new PermissionAsked("old", "Bash", J("{}"), null));
        f.Add(new PermissionAsked("live", "Bash", J("{}"), null));
        f.ExpirePermissionsExcept(new[] { "live" });

        var cards = f.Items.OfType<PermissionFeedItem>().ToList();
        Assert.False(cards[0].IsPending);
        Assert.Contains("истёк", cards[0].ResultText);
        Assert.True(cards[1].IsPending);
    }

    [Fact]
    public void Crash_CardWithRestart()
    {
        var f = New();
        f.Add(new ProcessCrashed(2, new[] { "a", "b" }));
        var card = Assert.IsType<CrashFeedItem>(Assert.Single(f.Items));
        Assert.Equal("claude завершился (код 2)", card.Title);
        Assert.Equal("a\nb", card.Details);
        card.RestartCommand.Execute(null);
        Assert.Equal(1, _restarts);

        f.Add(new ProcessCrashed(null, new[] { "claude не найден" }));
        Assert.Equal("claude не запустился", Assert.IsType<CrashFeedItem>(f.Items[^1]).Title);
    }

    [Fact]
    public void Unknown_RawCard_ParseErrorSkipped()
    {
        var f = New();
        f.Add(new UnknownEvent("new_thing", "{}"));
        f.Add(new ParseError("{", "битая"));
        Assert.Equal("raw · new_thing", Assert.IsType<RawFeedItem>(Assert.Single(f.Items)).Title);
    }

    [Fact]
    public void HugeOutput_Clipped()
    {
        var f = New();
        f.Add(new ToolUse("m", "t1", "Bash", J("""{"command":"type big.log"}"""), null));
        f.Add(new ToolResult("t1", new string('x', 50_000), false, false, null));
        var card = Assert.Single(f.Items.OfType<ToolFeedItem>());
        Assert.StartsWith(new string('x', FeedBuilder.MaxOutputChars), card.Output);
        Assert.EndsWith("[вывод обрезан: показано 20000 из 50000 символов]", card.Output);
    }

    [Fact]
    public void SubagentEvents_CountedOnParentCard()
    {
        var f = New();
        f.Add(new ToolUse("m", "p1", "Task", J("""{"description":"разбор дампа"}"""), null));
        f.Add(new ToolUse("m2", "c1", "Bash", J("{}"), "p1"));
        f.Add(new ToolUse("m2", "c2", "Read", J("{}"), "p1"));
        f.Add(new AssistantText("m3", "промежуточное", "p1"));
        f.Add(new ToolResult("c1", "ok", false, false, "p1"));

        var card = Assert.IsType<ToolFeedItem>(Assert.Single(f.Items));
        Assert.Equal(2, card.SubSteps);
        Assert.EndsWith("· субагент: 2", card.Header);
    }

    [Theory]
    [InlineData("Bash", """{"command":"dir C:\\"}""", "dir C:\\")]
    [InlineData("Read", """{"file_path":"C:\\a.txt"}""", "C:\\a.txt")]
    [InlineData("mcp__x__y", """{"n":1,"q":"abc"}""", "abc")]
    [InlineData("PowerShell", """{"command":"Get-Date\nGet-Item x"}""", "Get-Date …")]
    [InlineData("Weird", "{}", "")]
    public void ToolSummary_PicksMeaningfulField(string tool, string input, string expected)
        => Assert.Equal(expected, ToolSummary.For(tool, J(input)));

    [Fact]
    public void ToolSummary_LongClipped()
    {
        var s = ToolSummary.For("Bash", J($$"""{"command":"{{new string('a', 300)}}"}"""));
        Assert.Equal(ToolSummary.Max, s.Length);
        Assert.EndsWith("…", s);
    }
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~FeedBuilder`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/FeedItems.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SzDiag.Desk.ViewModels;

public abstract class FeedItemViewModel : ObservableObject
{
    public DateTimeOffset? At { get; init; }
}

public sealed class UserFeedItem(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

public sealed class AssistantFeedItem(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

/// <summary>Серая системная строка: события машины, прерывание, архив.</summary>
public sealed class NoteFeedItem(string text) : FeedItemViewModel
{
    public string Text { get; } = text;
}

public sealed class RawFeedItem(string type, string raw) : FeedItemViewModel
{
    public string Title { get; } = $"raw · {type}";
    public string Raw { get; } = raw;
}

public sealed class CrashFeedItem(string title, string details, Action restart) : FeedItemViewModel
{
    public string Title { get; } = title;
    public string Details { get; } = details;
    public IRelayCommand RestartCommand { get; } = new RelayCommand(restart);
}

public enum ToolStatus { Running, Ok, Error, Denied }

/// <summary>Свёрнутая моно-карточка вызова инструмента `✓ команда · 12с`, раскрывается в вывод.</summary>
public sealed partial class ToolFeedItem : FeedItemViewModel
{
    public ToolFeedItem(string id, string name, string summary)
    {
        Id = id;
        Name = name;
        Summary = summary;
    }

    public string Id { get; }
    public string Name { get; }
    public string Summary { get; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private ToolStatus _status = ToolStatus.Running;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private TimeSpan? _duration;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Header))] private int _subSteps;
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isExpanded;

    public string Header
    {
        get
        {
            var glyph = Status switch
            {
                ToolStatus.Ok => "✓",
                ToolStatus.Error => "✗",
                ToolStatus.Denied => "⊘",
                _ => "…",
            };
            var head = $"{glyph} {Name} · {Summary}";
            if (Duration is { } d) head += $" · {FormatDuration(d)}";
            if (SubSteps > 0) head += $" · субагент: {SubSteps}";
            return head;
        }
    }

    internal static string FormatDuration(TimeSpan d)
        => d.TotalSeconds < 60 ? $"{Math.Max(0, (int)d.TotalSeconds)}с" : $"{(int)d.TotalMinutes}м {d.Seconds:00}с";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>Оранжевая карточка разрешения. Ответ уходит в брокер, а «разрешено/отклонено»
/// ставится по событию PermissionAnswered — карточка показывает то, что реально произошло.</summary>
public sealed partial class PermissionFeedItem : FeedItemViewModel
{
    private readonly Action<string, bool> _answer;
    private bool _expired;

    public PermissionFeedItem(string requestId, string toolName, string summary, Action<string, bool> answer)
    {
        RequestId = requestId;
        ToolName = toolName;
        Summary = summary;
        _answer = answer;
    }

    public string RequestId { get; }
    public string ToolName { get; }
    public string Summary { get; }
    public string Title => $"Разрешить {ToolName}?";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPending), nameof(ResultText))] private bool? _allowed;

    public bool IsPending => Allowed is null;

    public string ResultText => _expired
        ? "запрос истёк — Desk перезапускался"
        : Allowed switch { true => "разрешено", false => "отклонено", null => "" };

    /// <summary>Запрос из прошлого запуска Desk: брокер о нём уже не знает.</summary>
    internal void Expire()
    {
        _expired = true;
        Allowed = false;
    }

    [RelayCommand]
    private void Allow() => _answer(RequestId, true);

    [RelayCommand]
    private void Deny() => _answer(RequestId, false);
}
```
`src/SzDiag.Desk/ViewModels/ToolSummary.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Desk.ViewModels;

/// <summary>Одна строка «что делает инструмент» для свёрнутой карточки.</summary>
public static class ToolSummary
{
    public const int Max = 100;

    public static string For(string tool, JsonElement input)
    {
        var raw = tool switch
        {
            "Bash" or "PowerShell" => Prop(input, "command"),
            "Read" or "Write" or "Edit" or "MultiEdit" => Prop(input, "file_path"),
            "NotebookEdit" => Prop(input, "notebook_path"),
            "Grep" or "Glob" => Prop(input, "pattern"),
            "WebFetch" => Prop(input, "url"),
            "WebSearch" => Prop(input, "query"),
            "Task" or "Agent" => Prop(input, "description"),
            _ => null,
        } ?? FirstString(input) ?? "";

        var lines = raw.Trim().Split('\n');
        var one = lines[0].TrimEnd('\r') + (lines.Length > 1 ? " …" : "");
        return one.Length <= Max ? one : one[..(Max - 1)] + "…";
    }

    private static string? Prop(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstString(JsonElement o)
        => o.ValueKind != JsonValueKind.Object
            ? null
            : o.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()).FirstOrDefault();
}
```
`src/SzDiag.Desk/ViewModels/FeedBuilder.cs`:
```csharp
using System.Collections.ObjectModel;
using SzDiag.Claude;

namespace SzDiag.Desk.ViewModels;

/// <summary>События сессии → карточки ленты. Вызывать в UI-потоке.</summary>
public sealed class FeedBuilder(Action<string, bool> answer, Action restart)
{
    /// <summary>Сколько вывода держать в карточке: 200k символов exec-а в одном TextBlock
    /// подвешивают раскладку, а полный вывод остаётся в журнале сессии.</summary>
    public const int MaxOutputChars = 20_000;

    private readonly Dictionary<string, ToolFeedItem> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PermissionFeedItem> _permissions = new(StringComparer.Ordinal);

    public ObservableCollection<FeedItemViewModel> Items { get; } = new();

    public void Add(ClaudeEvent e)
    {
        switch (e)
        {
            case DeskUserMessage m:
                Items.Add(new UserFeedItem(m.Text) { At = m.At });
                break;
            case AssistantText { ParentToolUseId: null } t:
                Items.Add(new AssistantFeedItem(t.Text) { At = t.At });
                break;
            case ToolUse { ParentToolUseId: { } parent }:
                // Шаги субагента в ленту не льём — считаем на карточке вызвавшего инструмента.
                if (_tools.TryGetValue(parent, out var owner)) owner.SubSteps++;
                break;
            case ToolUse u:
                var card = new ToolFeedItem(u.Id, u.Name, ToolSummary.For(u.Name, u.Input)) { At = u.At };
                _tools[u.Id] = card;
                Items.Add(card);
                break;
            case ToolResult { ParentToolUseId: null } r when _tools.TryGetValue(r.ToolUseId, out var c):
                c.Status = r.Denied ? ToolStatus.Denied : r.IsError ? ToolStatus.Error : ToolStatus.Ok;
                c.Output = Clip(r.Text);
                if (c.At is { } start && r.At is { } end && end >= start) c.Duration = end - start;
                break;
            case TurnResult { Interrupted: true } ti:
                Items.Add(new NoteFeedItem("ход прерван") { At = ti.At });
                break;
            case TurnResult { IsError: true } te:
                Items.Add(new NoteFeedItem($"ход завершился ошибкой: {te.Text}") { At = te.At });
                break;
            case DeskNote n:
                Items.Add(new NoteFeedItem(n.Text) { At = n.At });
                break;
            case PermissionAsked p:
                var item = new PermissionFeedItem(p.RequestId, p.ToolName, ToolSummary.For(p.ToolName, p.Input), answer) { At = p.At };
                _permissions[p.RequestId] = item;
                Items.Add(item);
                break;
            case PermissionAnswered a when _permissions.TryGetValue(a.RequestId, out var q):
                q.Allowed = a.Allowed;
                break;
            case ProcessCrashed x:
                Items.Add(new CrashFeedItem(
                    x.ExitCode is { } code ? $"claude завершился (код {code})" : "claude не запустился",
                    string.Join("\n", x.StderrTail), restart) { At = x.At });
                break;
            case UnknownEvent u:
                Items.Add(new RawFeedItem(u.Type, u.Raw) { At = u.At });
                break;
        }
    }

    public void ExpirePermissionsExcept(IEnumerable<string> liveIds)
    {
        var live = liveIds.ToHashSet(StringComparer.Ordinal);
        foreach (var (id, item) in _permissions)
            if (item.IsPending && !live.Contains(id)) item.Expire();
    }

    internal static string Clip(string text) => text.Length <= MaxOutputChars
        ? text
        : text[..MaxOutputChars] + $"\n[вывод обрезан: показано {MaxOutputChars} из {text.Length} символов]";
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~FeedBuilder`
Expected: 16 passed.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels tests/SzDiag.Desk.Tests/Fixture.cs tests/SzDiag.Desk.Tests/FeedBuilderTests.cs
git commit -m "feat(desk): лента чата — карточки инструментов, разрешений и падений"
```

---

### Task 8: `ChatViewModel`

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/ChatViewModel.cs`
- Create: `tests/SzDiag.Desk.Tests/FakeClaudeProcess.cs`, `ChatHarness.cs`, `ChatViewModelTests.cs`

**Interfaces:**
- Consumes: `ClaudeSession`, `PermissionBroker` (задача 5), `ITerminalLauncher`, `ChatServices` (задача 6), `FeedBuilder` (задача 7).
- Produces: `ChatViewModel(ClaudeSession session, PermissionBroker broker, ITerminalLauncher terminal, Action<Action> ui)` — `string Key`, `FeedBuilder Feed`, `ObservableCollection<FeedItemViewModel> Items`, `string Draft`, `SessionState State`, `int Queued`, `bool HasQueue`, `bool CanStop`, `string StateText`, `SendCommand`, `StopCommand`, `RestartCommand`, `OpenInTerminalCommand`.

- [ ] **Step 1: Обвязка тестов**

`tests/SzDiag.Desk.Tests/FakeClaudeProcess.cs` — копия фейка из `SzDiag.Claude.Tests` (тестовые проекты друг на друга не ссылаются), в `namespace SzDiag.Desk.Tests`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Desk.Tests;

public sealed class FakeClaudeProcess : IClaudeProcess
{
    public List<string> Written { get; } = new();
    public List<string> Stderr { get; } = new();
    public ClaudeLaunch? Launch { get; private set; }
    public bool Stopped { get; private set; }
    public bool IsRunning { get; private set; }
    public IReadOnlyList<string> StderrTail => Stderr;

    public event Action<string>? OutputLine;
    public event Action<int?>? Exited;

    public void Start(ClaudeLaunch launch)
    {
        Launch = launch;
        IsRunning = true;
    }

    public Task WriteLineAsync(string line)
    {
        Written.Add(line);
        return Task.CompletedTask;
    }

    public Task StopAsync(TimeSpan grace)
    {
        Stopped = true;
        if (IsRunning) Exit(0);
        return Task.CompletedTask;
    }

    public void Emit(string line) => OutputLine?.Invoke(line);

    public void Exit(int? code)
    {
        IsRunning = false;
        Exited?.Invoke(code);
    }
}
```
`tests/SzDiag.Desk.Tests/ChatHarness.cs`:
```csharp
using SzDiag.Claude;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public sealed class FakeTerminal : ITerminalLauncher
{
    public List<(string Key, string SessionId)> Opened { get; } = new();

    public bool Open(string key, string sessionId)
    {
        Opened.Add((key, sessionId));
        return true;
    }
}

/// <summary>Ядро сессий на фейковых процессах; UI-маршалинг — синхронный.</summary>
internal sealed class ChatHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "szdesk-" + Guid.NewGuid().ToString("N"));
    public List<FakeClaudeProcess> Processes { get; } = new();
    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public TokenLedger Tokens { get; }
    public TranscriptStore Transcripts { get; }
    public FakeTerminal Terminal { get; } = new();
    public ChatServices Services { get; }

    public ChatHarness()
    {
        Directory.CreateDirectory(Dir);
        Tokens = new TokenLedger(Path.Combine(Dir, "desk-tokens.json"), TimeProvider.System);
        Transcripts = new TranscriptStore(Path.Combine(Dir, "sessions"));
        var sessions = new SessionManager(new SessionDeps(
            SessionIndex.Load(Path.Combine(Dir, "desk-sessions.json")), Transcripts, Tokens, Broker,
            (key, resume) => new ClaudeLaunch("claude.exe", Dir, null, resume, "вводная " + key, "mcp.json"),
            () =>
            {
                var p = new FakeClaudeProcess();
                Processes.Add(p);
                return p;
            },
            TimeProvider.System, SessionTimeouts.Default));
        Services = new ChatServices(sessions, Broker, Tokens, Terminal, a => a());
    }

    public FakeClaudeProcess Last => Processes[^1];

    public ChatViewModel Chat(string key) => new(Services.Sessions.Create(key), Broker, Terminal, a => a());

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
```

- [ ] **Step 2: Failing tests**

`tests/SzDiag.Desk.Tests/ChatViewModelTests.cs`:
```csharp
using System.Text.Json;
using SzDiag.Claude;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class ChatViewModelTests : IDisposable
{
    private const string SpikeSession = "8c8e3bf5-ea25-4879-a3dc-566095eba936";
    private readonly ChatHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static async Task Send(ChatViewModel vm, string text)
    {
        vm.Draft = text;
        await vm.SendCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Send_ClearsDraft_ShowsBubble_Working()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "  проверь SMART  ");

        Assert.Equal("", vm.Draft);
        Assert.Equal("проверь SMART", Assert.IsType<UserFeedItem>(Assert.Single(vm.Items)).Text);
        Assert.Equal(SessionState.Working, vm.State);
        Assert.True(vm.CanStop);
    }

    [Fact]
    public async Task EmptyDraft_NotSent()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "   ");
        Assert.Empty(_h.Processes);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Stop_ReturnsQueuedToDraft()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "первое");
        await Send(vm, "второе");
        Assert.Equal(1, vm.Queued);
        Assert.True(vm.HasQueue);

        vm.Draft = "третье";
        var stop = vm.StopCommand.ExecuteAsync(null);
        _h.Last.Emit(Fixture.Line("interrupt-turn.jsonl", e => e is TurnResult { Interrupted: true }));
        await stop;

        Assert.Equal("второе\n\nтретье", vm.Draft);
        Assert.Equal(0, vm.Queued);
        Assert.Equal(SessionState.Idle, vm.State);
    }

    [Fact]
    public async Task PermissionCard_AllowResolvesBroker()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "создай файл");
        var ask = _h.Broker.AskAsync("161432", "Write", J("""{"file_path":"C:\\a.txt"}"""), "t1", default);

        var card = Assert.Single(vm.Items.OfType<PermissionFeedItem>());
        Assert.True(card.IsPending);
        Assert.Equal(SessionState.WaitingPermission, vm.State);
        card.AllowCommand.Execute(null);

        Assert.True((await ask).Allow);
        Assert.Equal("разрешено", card.ResultText);
        Assert.Equal(SessionState.Working, vm.State);
    }

    [Fact]
    public void Replay_StalePermission_Expired()
    {
        // Запрос из прошлого запуска Desk: брокер о нём не знает — карточка не должна ждать вечно.
        _h.Transcripts.Append("161432", DeskLines.Serialize(new PermissionAsked("old", "Bash", J("{}"), null))!);
        var vm = _h.Chat("161432");
        var card = Assert.Single(vm.Items.OfType<PermissionFeedItem>());
        Assert.False(card.IsPending);
        Assert.Contains("истёк", card.ResultText);
    }

    [Fact]
    public async Task OpenInTerminal_StopsSessionAndLaunches()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "привет");
        _h.Last.Emit(Fixture.Line("simple-turn.jsonl", e => e is SystemInit));

        await vm.OpenInTerminalCommand.ExecuteAsync(null);

        Assert.Equal(("161432", SpikeSession), Assert.Single(_h.Terminal.Opened));
        Assert.True(_h.Last.Stopped);
        Assert.Equal(SessionState.Stopped, vm.State);
        Assert.Contains(vm.Items, i => i is NoteFeedItem n && n.Text.Contains("два писателя"));
    }

    [Fact]
    public async Task OpenInTerminal_NoSessionYet_Note()
    {
        var vm = _h.Chat("161432");
        await vm.OpenInTerminalCommand.ExecuteAsync(null);
        Assert.Empty(_h.Terminal.Opened);
        Assert.Contains(vm.Items, i => i is NoteFeedItem n && n.Text.Contains("ещё не началась"));
    }

    [Fact]
    public async Task Crash_ShowsCard_StateCrashed()
    {
        var vm = _h.Chat("161432");
        await Send(vm, "x");
        _h.Last.Stderr.Add("boom");
        _h.Last.Exit(3);

        var card = Assert.Single(vm.Items.OfType<CrashFeedItem>());
        Assert.Contains("код 3", card.Title);
        Assert.Contains("boom", card.Details);
        Assert.Equal(SessionState.Crashed, vm.State);
        Assert.Contains("упала", vm.StateText);
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~ChatViewModel`
Expected: ошибка компиляции — `ChatViewModel` не найден.

- [ ] **Step 4: Реализация**

`src/SzDiag.Desk/ViewModels/ChatViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Claude;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels;

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly ClaudeSession _session;
    private readonly ITerminalLauncher _terminal;
    private readonly Action<Action> _ui;

    public ChatViewModel(ClaudeSession session, PermissionBroker broker, ITerminalLauncher terminal, Action<Action> ui)
    {
        _session = session;
        _terminal = terminal;
        _ui = ui;
        Feed = new FeedBuilder((id, allow) => broker.Resolve(id, allow), session.Restart);
        foreach (var e in session.Attach(ev => _ui(() => Feed.Add(ev)))) Feed.Add(e);
        Feed.ExpirePermissionsExcept(broker.Pending(session.Key).Select(p => p.RequestId));
        session.Changed += () => _ui(Refresh);
        Refresh();
    }

    public string Key => _session.Key;
    public FeedBuilder Feed { get; }
    public ObservableCollection<FeedItemViewModel> Items => Feed.Items;

    [ObservableProperty] private string _draft = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStop), nameof(StateText))] private SessionState _state;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasQueue))] private int _queued;

    public bool CanStop => State is SessionState.Working or SessionState.WaitingPermission;
    public bool HasQueue => Queued > 0;

    public string StateText => State switch
    {
        SessionState.Stopped when _session.SessionId is null => "новая сессия — напиши первое сообщение",
        SessionState.Stopped => "остановлена — сообщение продолжит её (--resume)",
        SessionState.Idle => "готова",
        SessionState.Working => "работает…",
        SessionState.WaitingPermission => "ждёт разрешения",
        SessionState.Crashed => "упала — см. карточку в ленте",
        _ => "архив — сообщение продолжит сессию",
    };

    private void Refresh()
    {
        State = _session.State;
        Queued = _session.Queued.Count;
        OnPropertyChanged(nameof(StateText));
    }

    [RelayCommand]
    private async Task Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        Draft = "";
        await _session.SendAsync(text);
    }

    [RelayCommand]
    private async Task Stop()
    {
        var back = await _session.InterruptAsync();
        if (back.Count == 0) return;
        // Неотправленное возвращается в поле ввода, как Esc в терминальном Claude Code:
        // «■» — это «стоп», а не «выброси всё, что я успел написать».
        Draft = string.Join("\n\n", back.Append(Draft).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    [RelayCommand]
    private void Restart() => _session.Restart();

    [RelayCommand]
    private async Task OpenInTerminal()
    {
        if (_session.SessionId is not { } id)
        {
            _session.Note("в терминале открывать нечего: сессия ещё не началась");
            return;
        }
        await _session.DetachAsync();
        if (!_terminal.Open(_session.Key, id))
            _session.Note("терминал не открылся: claude.exe не найден или нет ни wt.exe, ни cmd.exe");
    }
}
```

- [ ] **Step 5: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное (8 новых).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Desk/ViewModels/ChatViewModel.cs tests/SzDiag.Desk.Tests/FakeClaudeProcess.cs tests/SzDiag.Desk.Tests/ChatHarness.cs tests/SzDiag.Desk.Tests/ChatViewModelTests.cs
git commit -m "feat(desk): модель чата — отправка, очередь, стоп, терминал"
```

---

### Task 9: `MainViewModel` — сессия выбранной СЗ, архив, события машины, токены

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/ArchivedItemViewModel.cs`
- Create: `tests/SzDiag.Desk.Tests/MainViewModelChatTests.cs`
- Modify: `src/SzDiag.Desk/ViewModels/MainViewModel.cs` (полная замена), `SzItemViewModel.cs`, `StatusBarViewModel.cs`

**Interfaces:**
- Consumes: `ChatServices` (задача 6), `ChatViewModel` (задача 8), `SessionManager.Create/Get/Peek/Records`, `ClaudeSession.Note/ArchiveAsync/State/Changed`, `TokenLedger.Today/CostToday/Changed`, `PermissionBroker.Requested/PendingCount`.
- Produces: `MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time, ChatServices? chat = null)` — сохраняет всё из части 1 и добавляет `ObservableCollection<ArchivedItemViewModel> Archived`, `ArchivedItemViewModel? SelectedArchived`, `ChatViewModel? ActiveChat`, `bool HasArchived`, `bool CanStartSession`, `bool ShowPlaceholder`, `string Title`, `StartSessionCommand`, `event Action? AttentionNeeded`, `bool HasPendingPermissions`; `SzItemViewModel.SessionState` (`SessionState?`) и `HasSession`; `StatusBarViewModel.TokensText` и `static string FormatTokens(TokenUsage u, decimal cost)`; `ArchivedItemViewModel(SessionRecord r)` — `Key`, `Subtitle`.

- [ ] **Step 1: Failing tests**

`tests/SzDiag.Desk.Tests/MainViewModelChatTests.cs`:
```csharp
using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class MainViewModelChatTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ChatHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SessionInfo S(string sz, SessionStatus st = SessionStatus.Online, int reboots = 0)
        => new(sz, "10.0.0.5", "PC-" + sz, st, Now.AddHours(-1), Now, RebootCount: reboots);

    private static HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = Now };

    private MainViewModel New()
        => new(new HubPoller(new FakeHubApi(), new Clock()), new DeskUiState(), new Clock(), _h.Services);

    [Fact]
    public void SelectSzWithoutSession_CanStart_StartOpensChat()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();

        Assert.Null(vm.ActiveChat);
        Assert.True(vm.CanStartSession);
        Assert.False(vm.ShowPlaceholder);

        vm.StartSessionCommand.Execute(null);
        Assert.Equal("161432", vm.ActiveChat!.Key);
        Assert.False(vm.CanStartSession);
        Assert.Equal("161432", vm.Title);
    }

    [Fact]
    public void SelectSzWithSession_ChatOpensItself()
    {
        _h.Services.Sessions.Create("161432");
        var vm = New();
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        Assert.Equal("161432", vm.ActiveChat!.Key);
    }

    [Fact]
    public void ClosedSz_SessionArchived_AndListed()
    {
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161501");

        vm.Apply(Snap(S("161432")));

        Assert.Equal(SessionState.Archived, _h.Services.Sessions.Peek("161501")!.State);
        Assert.Equal("161501", Assert.Single(vm.Archived).Key);
        Assert.True(vm.HasArchived);
    }

    [Fact]
    public void Apply_BeforeFirstSessionsPoll_ArchivesNothing()
    {
        // Первым может прийти опрос передач: пустой список СЗ тогда значит «ещё не знаю».
        _h.Services.Sessions.Create("161432");
        var vm = New();
        vm.Apply(HubSnapshot.Empty);

        Assert.NotEqual(SessionState.Archived, _h.Services.Sessions.Peek("161432")!.State);
        Assert.Empty(vm.Archived);
    }

    [Fact]
    public void SelectArchived_OpensItsChat_ClearsLiveSelection()
    {
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161501");
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();

        vm.SelectedArchived = vm.Archived.Single();

        Assert.Null(vm.Selected);
        Assert.Equal("161501", vm.ActiveChat!.Key);
    }

    [Fact]
    public void MachineChanges_NotedInSession()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        var session = _h.Services.Sessions.Create("161432");

        vm.Apply(Snap(S("161432", SessionStatus.Offline)));
        Assert.Contains(session.History, e => e is DeskNote n && n.Text.StartsWith("связь с машиной пропала"));

        vm.Apply(Snap(S("161432", reboots: 1)));
        Assert.Contains(session.History, e => e is DeskNote n && n.Text.Contains("boot сменился") && n.Text.Contains("⚡1"));
    }

    [Fact]
    public void BackOnlineSameBoot_NotedAsLag()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        var session = _h.Services.Sessions.Create("161432");
        vm.Apply(Snap(S("161432", SessionStatus.Offline)));
        vm.Apply(Snap(S("161432")));
        Assert.Contains(session.History, e => e is DeskNote n && n.Text.Contains("лаг"));
    }

    [Fact]
    public void Tokens_InStatusBar()
    {
        var vm = New();
        Assert.Equal("токены сегодня: 0 · $0.00", vm.Status.TokensText);
        _h.Tokens.Add(new TokenUsage(1000, 500, 0, 0), 0.12m);
        Assert.Equal("токены сегодня: 1.5K · $0.12", vm.Status.TokensText);
    }

    [Fact]
    public async Task SessionState_ShownOnCard()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        vm.StartSessionCommand.Execute(null);
        vm.ActiveChat!.Draft = "привет";
        await vm.ActiveChat.SendCommand.ExecuteAsync(null);

        Assert.Equal(SessionState.Working, vm.Items.Single().SessionState);
        Assert.True(vm.Items.Single().HasSession);
    }

    [Fact]
    public void PermissionRequest_RaisesAttention()
    {
        var vm = New();
        _h.Services.Sessions.Create("161432");
        var raised = 0;
        vm.AttentionNeeded += () => raised++;
        _ = _h.Broker.AskAsync("161432", "Bash", System.Text.Json.JsonDocument.Parse("{}").RootElement, null, default);
        Assert.Equal(1, raised);
        Assert.True(vm.HasPendingPermissions);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12_345, "12.3K")]
    [InlineData(2_500_000, "2.5M")]
    public void FormatTokens(long total, string expected)
        => Assert.Equal($"токены сегодня: {expected} · $1.50",
            StatusBarViewModel.FormatTokens(new TokenUsage(total, 0, 0, 0), 1.5m));
}
```

- [ ] **Step 2: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~MainViewModelChat`
Expected: ошибка компиляции.

- [ ] **Step 3: Реализация**

`src/SzDiag.Desk/ViewModels/ArchivedItemViewModel.cs`:
```csharp
using SzDiag.Claude;

namespace SzDiag.Desk.ViewModels;

/// <summary>Сессия закрытой СЗ в секции «АРХИВ»: СЗ из списка hub пропала, разговор остался.</summary>
public sealed class ArchivedItemViewModel(SessionRecord r)
{
    public string Key { get; } = r.Key;
    public string Subtitle { get; } = $"сессия с {r.CreatedAt.ToLocalTime():dd.MM HH:mm}";
}
```
`src/SzDiag.Desk/ViewModels/SzItemViewModel.cs` — добавить `using SzDiag.Claude;` и после `HasReboots`:
```csharp
    /// <summary>Состояние сессии Claude этой СЗ (точка на карточке); null — сессии нет.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSession))] private SessionState? _sessionState;

    public bool HasSession => SessionState is not null;
```
`src/SzDiag.Desk/ViewModels/StatusBarViewModel.cs` — добавить `using System.Globalization;`, `using SzDiag.Claude;`, свойство рядом с `_staleText`:
```csharp
    [ObservableProperty] private string? _tokensText;
```
и метод в конец класса:
```csharp
    public static string FormatTokens(TokenUsage u, decimal cost)
    {
        var t = u.Total;
        var n = t >= 1_000_000 ? (t / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M"
            : t >= 1_000 ? (t / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K"
            : t.ToString(CultureInfo.InvariantCulture);
        return $"токены сегодня: {n} · ${cost.ToString("0.00", CultureInfo.InvariantCulture)}";
    }
```
`src/SzDiag.Desk/ViewModels/MainViewModel.cs` — полная замена:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SzDiag.Claude;
using SzDiag.Desk.Services;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeskUiState _ui;
    private readonly TimeProvider _time;
    private readonly ChatServices? _chat;
    private readonly Dictionary<string, ChatViewModel> _chats = new(StringComparer.Ordinal);

    public MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time, ChatServices? chat = null)
    {
        Poller = poller;
        _ui = ui;
        _time = time;
        _chat = chat;
        _isInspectorOpen = ui.InspectorOpen;
        if (chat is null) return;
        chat.Tokens.Changed += () => chat.Ui(UpdateTokens);
        chat.Broker.Requested += _ => chat.Ui(() => AttentionNeeded?.Invoke());
        UpdateTokens();
    }

    public HubPoller Poller { get; }
    public ObservableCollection<SzItemViewModel> Items { get; } = new();
    public ObservableCollection<ArchivedItemViewModel> Archived { get; } = new();
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();
    public StatusBarViewModel Status { get; } = new();

    /// <summary>Пришёл запрос разрешения — окно мигает в панели задач.</summary>
    public event Action? AttentionNeeded;

    public bool HasPendingPermissions => _chat?.Broker.PendingCount > 0;

    [ObservableProperty] private SzItemViewModel? _selected;
    [ObservableProperty] private ArchivedItemViewModel? _selectedArchived;
    [ObservableProperty] private ChatViewModel? _activeChat;
    [ObservableProperty] private bool _hasTransfers;
    [ObservableProperty] private bool _hasArchived;
    [ObservableProperty] private bool _isInspectorOpen;

    public bool CanStartSession => _chat is not null && Selected is not null && ActiveChat is null;
    public bool ShowPlaceholder => Selected is null && ActiveChat is null;
    public string Title => ActiveChat?.Key ?? Selected?.Sz ?? "SzDiag";

    partial void OnIsInspectorOpenChanged(bool value) => _ui.InspectorOpen = value;

    partial void OnSelectedChanged(SzItemViewModel? value)
    {
        if (value is not null) SelectedArchived = null;
        ActiveChat = value is not null ? ChatFor(value.Sz)
            : SelectedArchived is not null ? ChatFor(SelectedArchived.Key)
            : null;
        NotifyCenter();
    }

    partial void OnSelectedArchivedChanged(ArchivedItemViewModel? value)
    {
        if (value is null)
        {
            if (Selected is null) ActiveChat = null;
            return;
        }
        Selected = null;
        ActiveChat = ChatFor(value.Key);
    }

    partial void OnActiveChatChanged(ChatViewModel? value) => NotifyCenter();

    private void NotifyCenter()
    {
        OnPropertyChanged(nameof(CanStartSession));
        OnPropertyChanged(nameof(ShowPlaceholder));
        OnPropertyChanged(nameof(Title));
    }

    /// <summary>«Начать сессию» — только руками: автозапуска нет (токены, спека).</summary>
    [RelayCommand]
    private void StartSession()
    {
        if (_chat is null || Selected is null) return;
        _chat.Sessions.Create(Selected.Sz);
        ActiveChat = ChatFor(Selected.Sz);
        RefreshSessionBadges();
    }

    private ChatViewModel? ChatFor(string key)
    {
        if (_chat is null) return null;
        if (_chats.TryGetValue(key, out var vm)) return vm;
        var session = _chat.Sessions.Get(key);
        if (session is null) return null;
        vm = new ChatViewModel(session, _chat.Broker, _chat.Terminal, _chat.Ui);
        session.Changed += () => _chat.Ui(RefreshSessionBadges);
        _chats[key] = vm;
        return vm;
    }

    /// <summary>Вызывать в UI-потоке (окно маршалит событие <see cref="HubPoller.Changed"/>).</summary>
    public void Apply(HubSnapshot s)
    {
        var now = _time.GetUtcNow();
        var selectedSz = Selected?.Sz;
        var before = Items.ToDictionary(i => i.Sz, i => (i.Liveness, i.RebootCount), StringComparer.Ordinal);
        CollectionSync.Sync(Items, s.Sessions.OrderBy(x => x.Sz, StringComparer.Ordinal),
            x => x.Sz, vm => vm.Sz, x => new SzItemViewModel(x, now), (vm, x) => vm.Update(x, now));
        if (selectedSz is not null && Items.All(i => i.Sz != selectedSz)) Selected = null;

        CollectionSync.Sync(Transfers, s.Transfers, t => t.Id, vm => vm.Id,
            t => new TransferItemViewModel(t), (vm, t) => vm.Update(t));
        HasTransfers = Transfers.Count > 0;

        Status.Apply(s, now);

        // Без удачного опроса списка СЗ пустой список значит «ещё не знаю», а не «все СЗ закрыты»:
        // иначе первый же опрос передач отправил бы все сессии в архив.
        if (_chat is null || s.SessionsOkAt is null || s.IsStale) return;
        NoteMachineChanges(before);
        ArchiveClosed(s);
        RefreshArchived();
        RefreshSessionBadges();
    }

    /// <summary>События машины — серыми строками в ленту её сессии. Уверенного «вырубон» по
    /// молчанию heartbeat не пишем: подтверждает отказ только смена boot (CLAUDE.md, п.42).</summary>
    private void NoteMachineChanges(Dictionary<string, (SzLivenessState Liveness, int RebootCount)> before)
    {
        foreach (var item in Items)
        {
            if (!before.TryGetValue(item.Sz, out var was)) continue;
            if (_chat!.Sessions.Get(item.Sz) is not { } session) continue;
            var online = item.Liveness == SzLivenessState.Online;
            var wasOnline = was.Liveness == SzLivenessState.Online;
            var rebooted = item.RebootCount > was.RebootCount;
            if (wasOnline && !online)
                session.Note("связь с машиной пропала: heartbeat молчит (под нагрузкой это бывает лагом — вырубон подтвердит только смена boot)");
            else if (!wasOnline && online)
                session.Note(rebooted
                    ? $"машина вернулась, boot сменился — был ребут или вырубон (⚡{item.RebootCount})"
                    : "машина снова на связи, boot прежний — это был лаг");
            else if (rebooted)
                session.Note($"boot сменился — был ребут или вырубон (⚡{item.RebootCount})");
        }
    }

    private void ArchiveClosed(HubSnapshot s)
    {
        var live = s.Sessions.Select(x => x.Sz).ToHashSet(StringComparer.Ordinal);
        foreach (var r in _chat!.Sessions.Records)
            if (!r.Archived && !live.Contains(r.Key) && _chat.Sessions.Get(r.Key) is { } session)
                _ = session.ArchiveAsync();
    }

    private void RefreshArchived()
    {
        var live = Items.Select(i => i.Sz).ToHashSet(StringComparer.Ordinal);
        CollectionSync.Sync(Archived,
            _chat!.Sessions.Records.Where(r => !live.Contains(r.Key)).OrderByDescending(r => r.CreatedAt),
            r => r.Key, vm => vm.Key, r => new ArchivedItemViewModel(r), (_, _) => { });
        if (SelectedArchived is not null && !Archived.Contains(SelectedArchived)) SelectedArchived = null;
        HasArchived = Archived.Count > 0;
    }

    private void RefreshSessionBadges()
    {
        if (_chat is null) return;
        foreach (var item in Items) item.SessionState = _chat.Sessions.Peek(item.Sz)?.State;
    }

    private void UpdateTokens()
        => Status.TokensText = StatusBarViewModel.FormatTokens(_chat!.Tokens.Today, _chat.Tokens.CostToday);
}
```

- [ ] **Step 4: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное — и 14 новых, и тесты части 1 (`MainViewModelTests` зовут конструктор с тремя аргументами).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Desk/ViewModels tests/SzDiag.Desk.Tests/MainViewModelChatTests.cs
git commit -m "feat(desk): сессия выбранной СЗ, архив, события машины и токены в модели окна"
```

---

### Task 10: Представления чата и сборка окна

**Files:**
- Create: `src/SzDiag.Desk/Views/ChatView.axaml(.cs)`, `src/SzDiag.Desk/Services/WindowAttention.cs`
- Create: `tests/SzDiag.Desk.Tests/ChatWindowSmokeTests.cs`
- Modify: `src/SzDiag.Desk/SzDiag.Desk.csproj` (Markdown), `Views/MainWindow.axaml(.cs)`, `Views/SzListView.axaml`, `Views/StatusBarView.axaml`, `Views/Converters.cs`, `App.axaml.cs`

**Interfaces:**
- Consumes: `MainViewModel` (задача 9), `ChatViewModel` и карточки (задачи 7–8), `DeskClaudeHost` (задача 6).
- Produces: `ChatView` (x:Name поля ввода `ChatInput`, ленты `FeedScroll`), в `MainWindow` — `ChatPane` (ChatView), `StartSessionButton`, `TerminalButton`; `static void WindowAttention.Flash(Window w)`; конвертеры `Converters.SessionStateToBrush`, `Converters.SessionStateToText`.

- [ ] **Step 1: Пакет markdown**

В `src/SzDiag.Desk/SzDiag.Desk.csproj` рядом с Avalonia-пакетами:
```xml
    <PackageReference Include="Markdown.Avalonia.Tight" Version="11.0.3" />
```
Run: `"C:\Program Files\dotnet\dotnet.exe" build src/SzDiag.Desk`
Expected: без ошибок.

- [ ] **Step 2: Failing smoke tests**

`tests/SzDiag.Desk.Tests/ChatWindowSmokeTests.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.Views;

namespace SzDiag.Desk.Tests;

public class ChatWindowSmokeTests
{
    private static (MainWindow W, MainViewModel Vm, ChatHarness H) Open()
    {
        var h = new ChatHarness();
        var vm = new MainViewModel(new HubPoller(new FakeHubApi(), TimeProvider.System), new DeskUiState(),
            TimeProvider.System, h.Services);
        var w = new MainWindow(vm);
        w.Show();
        vm.Apply(HubSnapshot.Empty with
        {
            SessionsOkAt = DateTimeOffset.UtcNow,
            Sessions = new[] { new SessionInfo("161432", "10.0.0.5", "PC", SessionStatus.Online,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) },
        });
        return (w, vm, h);
    }

    [AvaloniaFact]
    public void SelectSz_StartButton_ThenChatPane()
    {
        var (w, vm, h) = Open();
        using var _ = h;
        vm.Selected = vm.Items.Single();

        Assert.True(w.FindControl<Button>("StartSessionButton")!.IsEffectivelyVisible);
        Assert.False(w.FindControl<ChatView>("ChatPane")!.IsEffectivelyVisible);

        vm.StartSessionCommand.Execute(null);
        Assert.False(w.FindControl<Button>("StartSessionButton")!.IsEffectivelyVisible);
        Assert.True(w.FindControl<ChatView>("ChatPane")!.IsEffectivelyVisible);
        Assert.True(w.FindControl<Button>("TerminalButton")!.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task SendAndAnswer_RenderInFeed()
    {
        var (w, vm, h) = Open();
        using var _ = h;
        vm.Selected = vm.Items.Single();
        vm.StartSessionCommand.Execute(null);

        var input = w.FindControl<ChatView>("ChatPane")!.FindControl<TextBox>("ChatInput")!;
        input.Text = "привет";
        await vm.ActiveChat!.SendCommand.ExecuteAsync(null);
        h.Last.Emit(Fixture.Line("simple-turn.jsonl", e => e is AssistantText));
        h.Last.Emit(Fixture.Line("tool-turn.jsonl", e => e is ToolUse));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("", input.Text);
        Assert.Collection(vm.ActiveChat.Items,
            i => Assert.IsType<UserFeedItem>(i),
            i => Assert.IsType<AssistantFeedItem>(i),
            i => Assert.IsType<ToolFeedItem>(i));
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~ChatWindowSmoke`
Expected: ошибка компиляции — `ChatView` не найден.

- [ ] **Step 4: Конвертеры и мигание окна**

В `src/SzDiag.Desk/Views/Converters.cs` добавить `using SzDiag.Claude;` и в класс:
```csharp
    /// <summary>Точка сессии на карточке СЗ: работает — акцент, ждёт разрешения — оранжевый,
    /// упала — красный, остальное — третий план.</summary>
    public static readonly IValueConverter SessionStateToBrush = new FuncValueConverter<SessionState?, IBrush>(s => s switch
    {
        SessionState.Working => Res("Accent"),
        SessionState.WaitingPermission => Res("Warn"),
        SessionState.Crashed => Res("Bad"),
        _ => Res("Text.Tertiary"),
    });

    public static readonly IValueConverter SessionStateToText = new FuncValueConverter<SessionState?, string>(s => s switch
    {
        SessionState.Working => "Claude работает",
        SessionState.WaitingPermission => "Claude ждёт разрешения",
        SessionState.Crashed => "сессия упала",
        SessionState.Idle => "сессия готова",
        _ => "сессия остановлена",
    });
```
`src/SzDiag.Desk/Services/WindowAttention.cs`:
```csharp
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace SzDiag.Desk.Services;

/// <summary>Мигание окна в панели задач, пока оно не в фокусе. Вместо всплывающего уведомления
/// Windows: у Avalonia 11 нативных тостов нет, а мигание работает без зависимостей.</summary>
public static class WindowAttention
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private const uint FlashAll = 3;          // FLASHW_ALL: заголовок и кнопка в панели задач
    private const uint FlashUntilFocus = 12;  // FLASHW_TIMERNOFG: до получения фокуса

    public static void Flash(Window w)
    {
        if (!OperatingSystem.IsWindows() || w.IsActive) return;
        var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = hwnd, Flags = FlashAll | FlashUntilFocus,
        };
        FlashWindowEx(ref info);
    }
}
```

- [ ] **Step 5: ChatView**

`src/SzDiag.Desk/Views/ChatView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             xmlns:md="https://github.com/whistyun/Markdown.Avalonia.Tight"
             x:Class="SzDiag.Desk.Views.ChatView"
             x:DataType="vm:ChatViewModel">
  <DockPanel>
    <Border DockPanel.Dock="Bottom" BorderBrush="{StaticResource Line}" BorderThickness="0,1,0,0" Padding="14,10">
      <StackPanel Spacing="6">
        <TextBlock Classes="secondary" IsVisible="{Binding HasQueue}"
                   Text="{Binding Queued, StringFormat='в очереди: {0} — уйдут после текущего хода'}" />
        <Grid ColumnDefinitions="*,Auto,Auto">
          <TextBox x:Name="ChatInput" Text="{Binding Draft}" AcceptsReturn="True" TextWrapping="Wrap"
                   MaxHeight="160" MinHeight="36"
                   Watermark="Сообщение для Claude — Enter отправить, Shift+Enter перенос строки" />
          <Button Grid.Column="1" Content="Отправить" Command="{Binding SendCommand}"
                  Margin="8,0,0,0" VerticalAlignment="Bottom" />
          <Button Grid.Column="2" Content="■" ToolTip.Tip="Остановить ход (неотправленное вернётся в поле ввода)"
                  Command="{Binding StopCommand}" IsVisible="{Binding CanStop}"
                  Margin="6,0,0,0" VerticalAlignment="Bottom" />
        </Grid>
      </StackPanel>
    </Border>
    <ScrollViewer x:Name="FeedScroll">
      <ItemsControl ItemsSource="{Binding Items}" Margin="18,14">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate>
            <StackPanel Spacing="8" />
          </ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
        <ItemsControl.DataTemplates>
          <DataTemplate DataType="vm:UserFeedItem">
            <Border HorizontalAlignment="Right" MaxWidth="560" Background="{StaticResource Accent}"
                    CornerRadius="12" Padding="11,7">
              <SelectableTextBlock Text="{Binding Text}" TextWrapping="Wrap" Foreground="White" />
            </Border>
          </DataTemplate>
          <DataTemplate DataType="vm:AssistantFeedItem">
            <md:MarkdownScrollViewer Markdown="{Binding Text}" />
          </DataTemplate>
          <DataTemplate DataType="vm:ToolFeedItem">
            <Border Background="{StaticResource Bg.Panel}" CornerRadius="8" Padding="10,6">
              <StackPanel Spacing="6">
                <Button Command="{Binding ToggleCommand}" Background="Transparent" Padding="0"
                        HorizontalAlignment="Stretch" HorizontalContentAlignment="Left">
                  <TextBlock Text="{Binding Header}" FontFamily="{StaticResource MonoFont}" FontSize="11.5"
                             TextTrimming="CharacterEllipsis" Foreground="{StaticResource Text.Secondary}" />
                </Button>
                <SelectableTextBlock IsVisible="{Binding IsExpanded}" Text="{Binding Output}" TextWrapping="Wrap"
                                     FontFamily="{StaticResource MonoFont}" FontSize="11" />
              </StackPanel>
            </Border>
          </DataTemplate>
          <DataTemplate DataType="vm:PermissionFeedItem">
            <Border BorderBrush="{StaticResource Warn}" BorderThickness="1" Background="#1fff9f0a"
                    CornerRadius="10" Padding="11,8">
              <StackPanel Spacing="6">
                <TextBlock Text="{Binding Title}" FontWeight="SemiBold" />
                <TextBlock Text="{Binding Summary}" FontFamily="{StaticResource MonoFont}" FontSize="11.5"
                           TextWrapping="Wrap" />
                <StackPanel Orientation="Horizontal" Spacing="8" IsVisible="{Binding IsPending}">
                  <Button Content="Разрешить" Command="{Binding AllowCommand}" />
                  <Button Content="Отклонить" Command="{Binding DenyCommand}" />
                </StackPanel>
                <TextBlock Classes="secondary" Text="{Binding ResultText}" IsVisible="{Binding !IsPending}" />
              </StackPanel>
            </Border>
          </DataTemplate>
          <DataTemplate DataType="vm:NoteFeedItem">
            <TextBlock Text="{Binding Text}" Classes="secondary" HorizontalAlignment="Center"
                       TextAlignment="Center" TextWrapping="Wrap" />
          </DataTemplate>
          <DataTemplate DataType="vm:CrashFeedItem">
            <Border BorderBrush="{StaticResource Bad}" BorderThickness="1" CornerRadius="10" Padding="11,8">
              <StackPanel Spacing="6">
                <TextBlock Text="{Binding Title}" Foreground="{StaticResource Bad}" FontWeight="SemiBold" />
                <SelectableTextBlock Text="{Binding Details}" FontFamily="{StaticResource MonoFont}" FontSize="11"
                                     TextWrapping="Wrap" />
                <Button Content="Перезапустить (--resume)" Command="{Binding RestartCommand}" HorizontalAlignment="Left" />
              </StackPanel>
            </Border>
          </DataTemplate>
          <DataTemplate DataType="vm:RawFeedItem">
            <Expander Header="{Binding Title}">
              <SelectableTextBlock Text="{Binding Raw}" FontFamily="{StaticResource MonoFont}" FontSize="11"
                                   TextWrapping="Wrap" />
            </Expander>
          </DataTemplate>
        </ItemsControl.DataTemplates>
      </ItemsControl>
    </ScrollViewer>
  </DockPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/ChatView.axaml.cs`:
```csharp
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Views;

public partial class ChatView : UserControl
{
    private INotifyCollectionChanged? _items;

    public ChatView()
    {
        InitializeComponent();
        // Туннелем: TextBox с AcceptsReturn сам съел бы Enter как перенос строки.
        ChatInput.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Hook();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    private void Hook()
    {
        if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
        _items = (DataContext as ChatViewModel)?.Items;
        if (_items is not null) _items.CollectionChanged += OnItemsChanged;
        Dispatcher.UIThread.Post(() => FeedScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Прокручиваем вниз, только если читатель и так был внизу: новые события не должны
        // выдёргивать его из середины длинного вывода.
        var nearBottom = FeedScroll.Offset.Y >= FeedScroll.Extent.Height - FeedScroll.Viewport.Height - 80;
        if (nearBottom) Dispatcher.UIThread.Post(() => FeedScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
```

- [ ] **Step 6: Главное окно, список, статусбар**

`src/SzDiag.Desk/Views/MainWindow.axaml` — в шапке центральной панели заменить `<Grid ColumnDefinitions="*,Auto">…</Grid>` (заголовок + `InspectorToggle`) на:
```xml
          <Grid ColumnDefinitions="*,Auto,Auto">
            <StackPanel Orientation="Horizontal" Spacing="10" VerticalAlignment="Center">
              <TextBlock Text="{Binding Title}" FontWeight="SemiBold" />
              <TextBlock Text="{Binding ActiveChat.StateText, FallbackValue=''}" Classes="secondary"
                         IsVisible="{Binding ActiveChat, Converter={x:Static ObjectConverters.IsNotNull}}" />
            </StackPanel>
            <Button x:Name="TerminalButton" Grid.Column="1" Content="открыть в терминале" Margin="0,0,8,0"
                    ToolTip.Tip="claude --resume в Windows Terminal; процесс в Desk при этом останавливается"
                    Command="{Binding ActiveChat.OpenInTerminalCommand, FallbackValue={x:Null}}"
                    IsVisible="{Binding ActiveChat, Converter={x:Static ObjectConverters.IsNotNull}}" />
            <ToggleButton x:Name="InspectorToggle" Grid.Column="2" Classes="icon"
                          IsChecked="{Binding IsInspectorOpen}" ToolTip.Tip="Инспектор СЗ">
              <Path Data="M1.5,2.5 H14.5 V13.5 H1.5 Z M10,2.5 V13.5" Stroke="{Binding $parent[ToggleButton].Foreground}"
                    StrokeThickness="1.4" Width="16" Height="16" Stretch="None" />
            </ToggleButton>
          </Grid>
```
и заменить заглушку (комментарий `<!-- Чат сессии Claude — часть 2 плана. -->` и `TextBlock` «Сессии Claude появятся в следующей версии») на:
```xml
        <Panel>
          <TextBlock Text="Выбери СЗ слева" Classes="secondary" IsVisible="{Binding ShowPlaceholder}"
                     HorizontalAlignment="Center" VerticalAlignment="Center" />
          <Button x:Name="StartSessionButton" Content="Начать сессию Claude" IsVisible="{Binding CanStartSession}"
                  Command="{Binding StartSessionCommand}" HorizontalAlignment="Center" VerticalAlignment="Center" />
          <!-- Видимость — на обёртке: у ChatView свой DataContext (модель чата), а решает модель окна. -->
          <Border IsVisible="{Binding ActiveChat, Converter={x:Static ObjectConverters.IsNotNull}}">
            <v:ChatView x:Name="ChatPane" DataContext="{Binding ActiveChat}" />
          </Border>
        </Panel>
```

`src/SzDiag.Desk/Views/MainWindow.axaml.cs` — полная замена:
```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Напоминание о висящем разрешении: ждём его сколько угодно (как терминал), но
    /// раз в 10 минут окно снова мигает, пока запрос не решён.</summary>
    private readonly DispatcherTimer _attention = new() { Interval = TimeSpan.FromMinutes(10) };

    /// <summary>Для дизайнера XAML.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        // Событие опроса приходит с пула потоков — в коллекции окна пишем только из UI-потока.
        vm.Poller.Changed += snap => Dispatcher.UIThread.Post(() => vm.Apply(snap));
        vm.AttentionNeeded += () => WindowAttention.Flash(this);
        _attention.Tick += (_, _) =>
        {
            if (vm.HasPendingPermissions) WindowAttention.Flash(this);
        };
        Opened += (_, _) =>
        {
            _ = vm.Poller.RunAsync(_stop.Token);
            _attention.Start();
        };
        Closed += (_, _) =>
        {
            _stop.Cancel();
            _attention.Stop();
        };
    }
}
```
`src/SzDiag.Desk/Views/SzListView.axaml` — полная замена:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             xmlns:v="using:SzDiag.Desk.Views"
             x:Class="SzDiag.Desk.Views.SzListView"
             x:DataType="vm:MainViewModel">
  <UserControl.Styles>
    <Style Selector="ListBoxItem">
      <Setter Property="CornerRadius" Value="8" />
      <Setter Property="Padding" Value="10,8" />
      <Setter Property="Margin" Value="0,1" />
    </Style>
    <Style Selector="ListBoxItem:selected /template/ ContentPresenter">
      <Setter Property="Background" Value="{StaticResource Bg.Selected}" />
    </Style>
  </UserControl.Styles>
  <DockPanel>
    <TextBlock DockPanel.Dock="Top" Classes="caps" Margin="14,12,14,6"
               Text="{Binding Items.Count, StringFormat='ЗАЯВКИ · {0}'}" />
    <!-- Сессии закрытых СЗ: разговор остался, продолжение — через --resume. -->
    <StackPanel DockPanel.Dock="Bottom" IsVisible="{Binding HasArchived}" Margin="0,0,0,8">
      <TextBlock Classes="caps" Margin="14,10,14,6" Text="АРХИВ" />
      <ListBox ItemsSource="{Binding Archived}" SelectedItem="{Binding SelectedArchived}"
               Background="Transparent" Margin="6,0" MaxHeight="220">
        <ListBox.ItemTemplate>
          <DataTemplate x:DataType="vm:ArchivedItemViewModel">
            <StackPanel>
              <TextBlock Text="{Binding Key}" FontWeight="SemiBold" Foreground="{StaticResource Text.Secondary}" />
              <TextBlock Text="{Binding Subtitle}" Classes="secondary" />
            </StackPanel>
          </DataTemplate>
        </ListBox.ItemTemplate>
      </ListBox>
    </StackPanel>
    <ListBox x:Name="List" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}"
             Background="Transparent" Margin="6,0">
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:SzItemViewModel">
          <Grid ColumnDefinitions="Auto,*,Auto,Auto">
            <Ellipse Width="7" Height="7" VerticalAlignment="Center" Margin="0,0,9,0"
                     Fill="{Binding Liveness, Converter={x:Static v:Converters.LivenessToBrush}}"
                     ToolTip.Tip="{Binding Liveness, Converter={x:Static v:Converters.LivenessToText}}" />
            <StackPanel Grid.Column="1">
              <TextBlock Text="{Binding Sz}" FontWeight="SemiBold" />
              <TextBlock Text="{Binding Subtitle}" Classes="secondary" TextTrimming="CharacterEllipsis" />
            </StackPanel>
            <Border Grid.Column="2" IsVisible="{Binding HasReboots}" CornerRadius="5" Padding="6,1"
                    Background="#22ff453a" VerticalAlignment="Center">
              <TextBlock Text="{Binding RebootCount, StringFormat='⚡{0}'}" FontSize="10" Foreground="#ff6b61" />
            </Border>
            <Ellipse Grid.Column="3" Width="6" Height="6" Margin="7,0,0,0" VerticalAlignment="Center"
                     IsVisible="{Binding HasSession}"
                     Fill="{Binding SessionState, Converter={x:Static v:Converters.SessionStateToBrush}}"
                     ToolTip.Tip="{Binding SessionState, Converter={x:Static v:Converters.SessionStateToText}}" />
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```
`src/SzDiag.Desk/Views/StatusBarView.axaml` — полная замена:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             x:Class="SzDiag.Desk.Views.StatusBarView"
             x:DataType="vm:StatusBarViewModel">
  <Grid ColumnDefinitions="*,Auto" Margin="14,0" VerticalAlignment="Center">
    <StackPanel Orientation="Horizontal" Spacing="18">
      <StackPanel Orientation="Horizontal" Spacing="6">
        <Ellipse Width="7" Height="7" Fill="{StaticResource Ok}" IsVisible="{Binding HubOk}" />
        <Ellipse Width="7" Height="7" Fill="{StaticResource Bad}" IsVisible="{Binding !HubOk}" />
        <TextBlock Text="{Binding HubText}" Classes="secondary" />
      </StackPanel>
      <TextBlock Text="{Binding StaleText}" Classes="secondary" Foreground="{StaticResource Warn}"
                 IsVisible="{Binding StaleText, Converter={x:Static ObjectConverters.IsNotNull}}" />
    </StackPanel>
    <TextBlock Grid.Column="1" Text="{Binding TokensText}" Classes="secondary"
               IsVisible="{Binding TokensText, Converter={x:Static ObjectConverters.IsNotNull}}" />
  </Grid>
</UserControl>
```

- [ ] **Step 7: Сборка в App**

`src/SzDiag.Desk/App.axaml.cs` — полная замена:
```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.Views;
using SzDiag.HubClient;

namespace SzDiag.Desk;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DeskLog.Init();
            var opts = DeskOptions.Load();
            var http = new HttpClient { BaseAddress = new Uri(opts.HubBaseUrl) };
            var api = new HubApiClient(http, opts.ManagementToken);
            var uiPath = Path.Combine(AppContext.BaseDirectory, "desk-ui.json");
            var ui = DeskUiState.Load(uiPath);

            // Старт и остановка ядра — на пуле потоков: блокирующее ожидание async-кода прямо на
            // UI-потоке Avalonia повесило бы окно дедлоком на его же SynchronizationContext.
            var claude = Task.Run(() => DeskClaudeHost.StartAsync(opts, AppContext.BaseDirectory)).GetAwaiter().GetResult();
            var chat = claude.Services(a => Dispatcher.UIThread.Post(a));

            var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), ui, TimeProvider.System, chat);
            desktop.MainWindow = new MainWindow(vm);
            desktop.Exit += (_, _) =>
            {
                ui.Save(uiPath);
                Task.Run(() => claude.DisposeAsync().AsTask()).GetAwaiter().GetResult();
                DeskLog.Write("выход: сессии остановлены");
            };
            DeskLog.Write($"старт, hub {opts.HubBaseUrl}");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 8: Run — PASS**

Run: `"C:\Program Files\dotnet\dotnet.exe" test tests/SzDiag.Desk.Tests`
Expected: всё зелёное, включая 2 новых и дымовые тесты части 1.

- [ ] **Step 9: Живая проверка чата с настоящим claude**

Без живой СЗ: в `src/SzDiag.Desk/bin/Debug/net8.0/` положить `desk-sessions.json` с записью `[{"Key":"000000","SessionId":null,"CreatedAt":"2026-09-25T12:00:00+00:00","Archived":true}]`, в `appsettings.json` рядом — `"ClaudeConfigDir": "<значение $env:CLAUDE_CONFIG_DIR>"`. Запустить `SzDiag.Desk.exe` (hub можно не поднимать — СЗ `000000` окажется в «АРХИВ» после первого удачного опроса; без hub — поднять временный hub из сборки на 127.0.0.1, как в части 1, с выключенным `Hub__KbBackup__Enabled`).

Проверить глазами и скриншотом:
1. «АРХИВ» → `000000` → лента пустая, статус «архив — сообщение продолжит сессию».
2. Сообщение «Ответь одним словом: привет» → синий пузырь → «работает…» → ответ Claude отрендерен markdown, статусбар показывает токены.
3. «Выполни `szcli --version`» → оранжевая карточка «Разрешить PowerShell?» (или Bash), окно мигает в панели задач, если не в фокусе → «Разрешить» → карточка `✓ PowerShell · … · Nс`, клик раскрывает вывод.
4. Длинный ответ (попросить «перечисли 60 строк») + «■» посреди хода → «ход прерван», процесс жив (следующее сообщение отвечается без задержки старта).
5. Прокрутка колёсиком над текстом ответа листает всю ленту. **Если `MarkdownScrollViewer` перехватывает колесо или растягивается на всю высоту** — заменить шаблон `AssistantFeedItem` на `<SelectableTextBlock Text="{Binding Text}" TextWrapping="Wrap" />`, убрать пакет `Markdown.Avalonia.Tight` и записать пункт в `docs/dev-backlog.md` (что именно не работает, версия пакета) — markdown тогда уходит в часть 3.
6. «открыть в терминале» → окно Windows Terminal с `claude --resume <id>`, в ленте Desk строка про двух писателей.
7. Закрыть Desk → в `logs\desk-<дата>.log` «выход: сессии остановлены», процессов `claude.exe` от Desk не осталось (`Get-Process claude`).
После проверки удалить `desk-sessions.json`, `sessions\`, `run\`, `desk-tokens.json` из `bin`.

- [ ] **Step 10: Commit**

```bash
git add src/SzDiag.Desk tests/SzDiag.Desk.Tests/ChatWindowSmokeTests.cs
git commit -m "feat(desk): чат сессии Claude в окне — лента, разрешения, архив, токены"
```

---

### Task 11: Документация, спека, чек-лист

**Files:**
- Modify: `CLAUDE.md`, `docs/dev-knowledge-base.md`, `docs/superpowers/specs/2026-09-25-desk-gui-design.md`
- Create: `docs/live-checklist-2026-09-25-desk-part2.md`

- [ ] **Step 1: Спека — решения плана**

В `docs/superpowers/specs/2026-09-25-desk-gui-design.md`:
- раздел «Жизненный цикл сессии», пункт «Запуск Desk»: «процесс стартует с `--resume` при открытии чата» → «процесс стартует с `--resume` при первом сообщении (открытие чата только показывает ленту из журнала `sessions\<ключ>.jsonl`)»;
- там же добавить пункт: «**Очередь** — у Desk: следующее сообщение уходит после `result` текущего хода; «■» возвращает неотправленное в поле ввода.»;
- пункт «СЗ закрыта»: дописать «— сессия переезжает в секцию «АРХИВ» под списком заявок»;
- таблица «Ошибки», строка «разрешение без ответа»: «повторное уведомление Windows через 10 мин» → «окно мигает в панели задач сразу и каждые 10 минут, пока запрос висит»;
- раздел `SzDiag.Claude`, пункт `ClaudeProcess`: добавить «Профиль — `ClaudeConfigDir` конфига Desk (`CLAUDE_CONFIG_DIR` процесса): на боксе его задаёт обёртка `claude2.cmd`, а Desk запускают из Проводника.»;
- статус спеки: «этапы 1–4 реализованы (планы частей 1 и 2), этапы 5–6 — часть 3».

- [ ] **Step 2: CLAUDE.md и база знаний разработчика**

`CLAUDE.md`, раздел «Архитектура»: «Одиннадцать проектов» → «Двенадцать проектов»; пункт `SzDiag.Desk` — заменить хвост «Сессии Claude на каждую СЗ — следующим планом (итоги спайка …)» на «В центре — чат сессии Claude выбранной СЗ (1 СЗ = 1 сессия): лента, карточки инструментов и разрешений, очередь, «■», «открыть в терминале», архив закрытых СЗ, токены за день.»; новый пункт перед `SzDiag.Kb`:
```markdown
- **SzDiag.Claude** — ядро сессий Claude без UI и без знания о СЗ (ключ — строка): процесс
  `claude -p` в stream-json (`ClaudeProcess`, флаги — `ClaudeLaunch`), парсер по фикстурам спайка
  (`StreamJsonParser`), `ClaudeSession` (состояния, очередь, прерывание control-запросом с
  остановкой процесса через 10 с, журнал `sessions\<ключ>.jsonl`), `SessionManager`,
  `PermissionBroker` + MCP-сервер `DeskMcpServer` (`permission_prompt`, 127.0.0.1, токен на
  запуск, `timeout` сутки). Ни на один проект решения не ссылается — пригодно для Telegram и службы.
```
`docs/dev-knowledge-base.md`: в таблицу проектов («12 в `src/`») строку `SzDiag.Claude`; новый раздел «Сессии Claude в Desk» — флаги запуска, файлы рядом с exe (`desk-sessions.json`, `desk-tokens.json`, `sessions\*.jsonl`, `run\*.mcp.json` и `resume-*.cmd`), конфиг `ClaudePath`/`ClaudeWorkDir`/`ClaudeConfigDir`, состояния сессии и переходы, правило архива (только после удачного опроса списка СЗ), маршрутизация разрешений по пути `/mcp/<ключ>`.

- [ ] **Step 3: Живой чек-лист**

`docs/live-checklist-2026-09-25-desk-part2.md`:
```markdown
# Живой чек-лист: SzDiag Desk, часть 2 — сессии Claude (2026-09-25)

Без живой СЗ уже проверено (архивная сессия `000000`): ответ и markdown, карточка разрешения,
«■», «открыть в терминале», остановка процессов при выходе. Ниже — на онлайн-СЗ.

- [ ] Desk из `dist\host\desk` двойным кликом: claude поднимается с профилем `ClaudeConfigDir` (в ленте нет «не залогинен»).
- [ ] «Начать сессию» на онлайн-СЗ → первое сообщение → Claude знает номер СЗ из вводной и зовёт `szcli … <СЗ>`.
- [ ] Разрешение на `szcli exec` → карточка → «Разрешить»; окно не в фокусе — мигает в панели задач.
- [ ] Два сообщения подряд → второе «в очереди», уходит само после ответа на первое.
- [ ] Вырубон под OCCT → в ленте «связь с машиной пропала», после возврата — «boot сменился (⚡N)», на карточке `⚡N`.
- [ ] Лаг heartbeat без ребута → «машина снова на связи, boot прежний — это был лаг».
- [ ] `szcli close <СЗ>` → сессия в «АРХИВ», новое сообщение из архива продолжает разговор (`--resume`).
- [ ] Перезапуск Desk → лента СЗ восстановилась из журнала, висевшая карточка разрешения — «запрос истёк».
- [ ] Две СЗ одновременно → каждая сессия видит только свою машину, токены в статусбаре суммируются.
- [ ] «открыть в терминале» на живой сессии → терминал с тем же разговором; Desk не пишет в него, пока терминал открыт.
```

- [ ] **Step 4: Полный прогон**

Run: `"C:\Program Files\dotnet\dotnet.exe" build` и `"C:\Program Files\dotnet\dotnet.exe" test`
Expected: сборка без ошибок и без новых предупреждений в `SzDiag.Claude`/`SzDiag.Desk`; все тесты зелёные, кроме известного `ScriptLintTests.AllRepoClientRecipes_NoFalseConcatWarning` (падает и до ветки — `update-agent-package.ps1`).

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md docs/dev-knowledge-base.md docs/superpowers/specs/2026-09-25-desk-gui-design.md docs/live-checklist-2026-09-25-desk-part2.md
git commit -m "docs(desk): сессии Claude — спека, база знаний, живой чек-лист"
```

---

## Дальше

- **Часть 3** — вкладки инспектора (вырубоны, сенсоры, задачи, журнал, железо), действия (`diag run`, `test run --config`, `freeze`/`unfreeze`, заметка, `sz fetch`, `close`), `GET /api/status` (версия пакета агента, бэкап kb, туннель), бейджи `🧊`/`≈`, `ask_peer`/`peers` в `DeskMcpServer` (глубина 1, 20 вопросов в час, таймаут 5 минут, фиолетовая метка пары).
- **Telegram** — лента и карточки разрешений из `SzDiag.Claude` в чат; отдельная спека.
