# SzDiag Desk, часть 1 — окно, список СЗ, статусбар, передачи — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Нативное окно SzDiag Desk на Avalonia, которое уже заменяет `szcli watch`: список СЗ с признаками живости, статусбар hub, прогресс `push`/`pull`, инспектор с вкладкой «Обзор» — плюс спайк stream-json, по итогам которого пишется часть 2 (сессии Claude и чат).

**Architecture:** `HubApiClient` выносится из CLI в библиотеку `SzDiag.HubClient`, общую для CLI и Desk. Hub получает `TransferTracker` и `GET /api/transfers`: Push/Pull отмечают байты, агент передаёт `requestId` в запросах раздачи. Desk — MVVM (CommunityToolkit.Mvvm): `HubPoller` опрашивает `/api` с разной частотой и откатом при ошибках, ViewModel'и синхронизируют коллекции по ключу, представления — Avalonia XAML со своими токенами тёмной темы и своим заголовком окна.

**Tech Stack:** .NET 8, Avalonia 11 (Desktop, Fluent-тема как база), CommunityToolkit.Mvvm, Avalonia.Headless.XUnit, xunit 2.5.3, ASP.NET Core minimal API (hub).

**Spec:** [docs/superpowers/specs/2026-09-25-desk-gui-design.md](../specs/2026-09-25-desk-gui-design.md)

**Что покрывает этот план:** этапы 1–3 спеки. Этапы 4–6 (`SzDiag.Claude`, чат, остальные вкладки инспектора и действия, `ask_peer`) — **отдельные планы**, которые пишутся после спайка (задача 1): их код целиком зависит от реальной формы событий stream-json, и писать его по догадкам — значит переписывать.

**Сознательно отложено из статусбара спеки** (нет API — появится вместе с `GET /api/status` в плане части 3): версия пакета агента, последний бэкап kb, состояние туннеля. Токены — с сессиями (часть 2). Бейджи `🧊` и `≈` и строка железа на карточке — с инспектором (часть 3); в части 1 на карточке — `Hostname`/`Activity`.

## Global Constraints

- Целевой фреймворк — **net8.0** во всех новых проектах.
- Файлы сборки (`*.csproj`, `*.ps1`) — **UTF-8 с BOM** (PowerShell 5.1 иначе ломает кириллицу, коммит 3e60857).
- Комментарии и пользовательский вывод — **на русском**, в стиле существующего кода (комментарий объясняет «почему», со ссылкой на СЗ/бэклог, если есть).
- Пути к конфигу/логам резолвятся от `AppContext.BaseDirectory`, не от CWD.
- Имена методов/заголовков протокола — только из `SzDiag.Contracts` (`HubRoutes`, `ToolRoutes`, новый `TransferRoutes`), не строками на концах.
- Признаки живости клиента — только `LastHeartbeat` и `BootTime`/`RebootCount`; уверенного «вырубон» по одному молчанию heartbeat **не показывать** (порог 10 минут → «нет связи», не «вырубон»).
- Тема: фон окна `#17181b`, боковая `#1c1d21`, панель `#212227`, линия `#2a2b31`, текст `#e7e7ea`, второй план `#8b8c94`, третий `#5d5e66`, акцент `#0a84ff`, ок `#30d158`, внимание `#ff9f0a`, ошибка `#ff453a`, фиолетовый `#bf5af2`. Шрифт `Segoe UI Variable Text, Segoe UI`.
- Кнопки окна: тонкие глифы 1.1 px, 34×28, скругление 6, при наведении плашка `#ffffff12`; крестик при наведении `#e81123`, глиф белый. Горячих клавиш для инспектора нет — только кнопка в шапке.
- Раскладка: левая колонка 210 px · центр растягивается · инспектор 290 px; заголовок окна 36 px; статусбар 26 px.

## Review Focus

- **Hub недоступен при старте Desk** (hub ещё не поднят) — окно должно открыться с пустым списком и красным статусбаром «hub не отвечает», а не упасть исключением из `HttpClient`. → тест в задаче 8 (`Poll_WhenHubDown_KeepsPreviousAndMarksStale`, `Poll_FirstCallFails_EmptyButStale`).
- **Повторный `push` уже доставленного инструмента** — агент пропускает все файлы, байт не приходит; прогресс не должен вечно висеть на 0 %, передача обязана стать `Done` с пометкой «пропущено N». → тест в задаче 5 (`Push_Repeat_TransferDoneWithSkippedNote`).
- **Таймаут/исключение посреди `pull`** — передача не должна остаться `Running` навсегда. → тест в задаче 4 (`Finish_WithError_MarksFailed`) и задаче 5 (`Pull_UnknownSz_NoTransferLeftRunning`).
- **СЗ пропала из `/api/sessions`** (закрыта) — карточка должна исчезнуть, выбор — сброситься, а не указывать на удалённый элемент. → тест в задаче 9 (`Apply_RemovesVanishedAndClearsSelection`).
- **Старый hub без `/api/transfers`** (404) — Desk работает, панель передач просто пуста, статусбар не краснеет. → тест в задаче 6 (`GetTransfers_OldHub_ReturnsEmpty`).

---

## Карта файлов

**Новое:**
- `src/SzDiag.HubClient/` — `SzDiag.HubClient.csproj`, `HubApiClient.cs`, `IHubApiClient.cs` (перенос), `SzLiveness.cs`.
- `tests/SzDiag.HubClient.Tests/` — `HubApiClientTests.cs` (перенос), `SzLivenessTests.cs`, `HubApiClientTransfersTests.cs`.
- `src/SzDiag.Contracts/TransferInfo.cs`, `src/SzDiag.Contracts/HealthzResponse.cs` (перенос из hub).
- `src/SzDiag.Hub/TransferTracker.cs`, `src/SzDiag.Hub/CountingReadStream.cs`.
- `tests/SzDiag.Hub.Tests/TransferTrackerTests.cs`, `tests/SzDiag.Hub.Tests/ManualTime.cs`.
- `src/SzDiag.Desk/` — `SzDiag.Desk.csproj`, `Program.cs`, `App.axaml(.cs)`, `appsettings.json`, `app.manifest`,
  `Theme/Tokens.axaml`, `Theme/Controls.axaml`,
  `Services/DeskOptions.cs`, `Services/DeskLog.cs`, `Services/HubPoller.cs`, `Services/HubSnapshot.cs`, `Services/DeskUiState.cs`,
  `ViewModels/MainViewModel.cs`, `ViewModels/SzItemViewModel.cs`, `ViewModels/StatusBarViewModel.cs`, `ViewModels/TransferItemViewModel.cs`, `ViewModels/CollectionSync.cs`,
  `Views/MainWindow.axaml(.cs)`, `Views/TitleBar.axaml(.cs)`, `Views/SzListView.axaml(.cs)`, `Views/InspectorView.axaml(.cs)`, `Views/StatusBarView.axaml(.cs)`, `Views/TransfersView.axaml(.cs)`, `Views/Converters.cs`.
- `tests/SzDiag.Desk.Tests/` — `SzDiag.Desk.Tests.csproj`, `FakeHubApi.cs`, `HubPollerTests.cs`, `MainViewModelTests.cs`, `StatusBarViewModelTests.cs`, `TransferItemViewModelTests.cs`, `TestApp.cs`, `MainWindowSmokeTests.cs`.
- `docs/superpowers/specs/2026-09-25-desk-spike-notes.md`, `tests/SzDiag.Claude.Tests/Fixtures/*.jsonl` (фикстуры спайка).

**Изменения:**
- `src/SzDiag.Cli/*.cs` (16 файлов с `IHubApiClient`) — `using SzDiag.HubClient;` через `GlobalUsings.cs`.
- `src/SzDiag.Cli/SessionTableRenderer.cs` — статус через `SzLiveness`.
- `src/SzDiag.Hub/HealthApi.cs`, `ToolsApi.cs`, `PushCoordinator.cs`, `PullCoordinator.cs`, `ManagementApi.cs`, `Program.cs`.
- `src/SzDiag.Contracts/PushCommand.cs` — `ToolRoutes.Manifest/File` с `requestId`.
- `src/SzDiag.Agent/PushCommandHandler.cs` — передаёт `requestId`.
- `tests/SzDiag.Hub.Tests/PushEndToEndTests.cs`, `PullEndToEndTests.cs` — тесты передач.
- `SzDiag.sln`, `tools/build-dist.ps1`, `CLAUDE.md`, `docs/dev-knowledge-base.md`.

---

### Task 1: Спайк stream-json (одноразовый код)

**Цель:** ответить на вопросы, от которых зависит часть 2, и снять реальные фикстуры. Код спайка **не сохраняется** в `src/` — только заметки и фикстуры.

**Files:**
- Create: `docs/superpowers/specs/2026-09-25-desk-spike-notes.md`
- Create: `tests/SzDiag.Claude.Tests/Fixtures/simple-turn.jsonl`, `tool-turn.jsonl`, `permission-turn.jsonl`, `interrupt-turn.jsonl`, `resume-init.jsonl`
- Scratch (не коммитить): `<scratchpad>/spike/`

**Interfaces:**
- Produces: заметки с ответами на вопросы 1–6 ниже; фикстуры — сырой stdout `claude`, по одному JSON на строку.

- [ ] **Step 1: Простой ход, фикстура `simple-turn.jsonl`**

Из корня репозитория (PowerShell):
```powershell
'{"type":"user","message":{"role":"user","content":"Ответь одним словом: привет"}}' |
  claude -p --input-format stream-json --output-format stream-json --verbose |
  Set-Content -Encoding utf8 tests\SzDiag.Claude.Tests\Fixtures\simple-turn.jsonl
```
Expected: строки с `"type":"system"` (`subtype":"init"`, есть `session_id`), `"type":"assistant"`, `"type":"result"` (есть `usage`, `total_cost_usd`, `duration_ms`). Записать в заметки точные имена полей.

- [ ] **Step 2: Ход с инструментом, фикстура `tool-turn.jsonl`**

```powershell
'{"type":"user","message":{"role":"user","content":"Выполни szcli --version и скажи результат"}}' |
  claude -p --input-format stream-json --output-format stream-json --verbose --allowedTools "Bash(szcli --version)" |
  Set-Content -Encoding utf8 tests\SzDiag.Claude.Tests\Fixtures\tool-turn.jsonl
```
Expected: в `assistant`-сообщении блок `tool_use` (`id`, `name`, `input`), затем `user`-сообщение с `tool_result` (`tool_use_id`, `content`, `is_error`). Записать структуру.

- [ ] **Step 3: `--permission-prompt-tool` через MCP по HTTP, фикстура `permission-turn.jsonl`**

В scratchpad поднять минимальный MCP-сервер (C#, `dotnet new web` + пакет `ModelContextProtocol.AspNetCore`, одна тулза `permission_prompt(tool_name, input)`, которая пишет вызов в консоль и возвращает `{"behavior":"allow","updatedInput":<input>}` текстом). Конфиг:
```json
{"mcpServers":{"desk":{"type":"http","url":"http://127.0.0.1:5199/mcp/161432","headers":{"X-Desk-Token":"spike"}}}}
```
Запуск:
```powershell
'{"type":"user","message":{"role":"user","content":"Создай файл spike.txt с текстом ok"}}' |
  claude -p --input-format stream-json --output-format stream-json --verbose `
    --mcp-config spike-mcp.json --permission-prompt-tool mcp__desk__permission_prompt |
  Set-Content -Encoding utf8 tests\SzDiag.Claude.Tests\Fixtures\permission-turn.jsonl
```
Expected: сервер получил вызов `permission_prompt` с `tool_name` = `Write`; файл создан. Повторить с ответом `{"behavior":"deny","message":"отклонено"}` — файл не создан, в ленте видно отказ. В заметки: точная схема аргументов и ответа, доходит ли путь `/mcp/161432` (ключ сессии), как ведёт себя `claude`, если сервер отвечает 30+ секунд (ждёт ли бесконечно — спека требует «ждём как терминал»).

- [ ] **Step 4: Прерывание хода, фикстура `interrupt-turn.jsonl`**

Мини-программа в scratchpad (C# console): запускает `claude -p --input-format stream-json --output-format stream-json --verbose`, пишет user-сообщение «Посчитай вслух от 1 до 200, каждое число отдельной строкой», через 3 с пишет в stdin
```json
{"type":"control_request","request_id":"int-1","request":{"subtype":"interrupt"}}
```
и сохраняет stdout. Expected (проверить): приходит `control_response` и `result` с признаком прерывания; процесс **остаётся жив** и принимает следующее сообщение. Если control-запрос не поддерживается — записать это, и запасной путь: kill процесса + `--resume` (проверить, что разговор продолжается).

- [ ] **Step 5: `--resume`, фикстура `resume-init.jsonl`**

Взять `session_id` из шага 1:
```powershell
'{"type":"user","message":{"role":"user","content":"Что я просил тебя сказать в прошлый раз?"}}' |
  claude -p --resume <session_id> --input-format stream-json --output-format stream-json --verbose |
  Set-Content -Encoding utf8 tests\SzDiag.Claude.Tests\Fixtures\resume-init.jsonl
```
Expected: ответ про «привет»; в `init` тот же или новый `session_id` — записать, какой (от этого зависит, что хранить в `desk-sessions.json`).

- [ ] **Step 6: Заметки**

`docs/superpowers/specs/2026-09-25-desk-spike-notes.md` — ответы:
1. Схема событий (`system/init`, `assistant` + блоки `text`/`tool_use`, `user` + `tool_result`, `result`) с именами полей.
2. Схема `permission_prompt`: аргументы, формат ответа allow/deny, поведение при долгом ответе.
3. Работает ли путь `/mcp/<ключ>` для различения сессий.
4. Прерывание: control-запрос работает / не работает, запасной путь.
5. `--resume`: `session_id` сохраняется или меняется.
6. Видит ли headless-сессию `claude-tg-bridge` (появляется ли файл в `~/.claude/sessions/*.json`) — для будущей интеграции с Telegram.

Если ответы расходятся со спекой (раздел «Жизненный цикл»), поправить спеку в том же коммите.

- [ ] **Step 7: Commit**

```bash
git add docs/superpowers/specs/2026-09-25-desk-spike-notes.md tests/SzDiag.Claude.Tests/Fixtures
git commit -m "docs(desk): итоги спайка stream-json и фикстуры событий"
```

---

### Task 2: Вынести `HubApiClient` в `SzDiag.HubClient`

**Files:**
- Create: `src/SzDiag.HubClient/SzDiag.HubClient.csproj`
- Move: `src/SzDiag.Cli/HubApiClient.cs` → `src/SzDiag.HubClient/HubApiClient.cs`
- Move: `src/SzDiag.Cli/IHubApiClient.cs` → `src/SzDiag.HubClient/IHubApiClient.cs`
- Create: `src/SzDiag.Cli/GlobalUsings.cs`
- Create: `tests/SzDiag.HubClient.Tests/SzDiag.HubClient.Tests.csproj`
- Move: `tests/SzDiag.Cli.Tests/HubApiClientTests.cs` → `tests/SzDiag.HubClient.Tests/HubApiClientTests.cs`
- Modify: `src/SzDiag.Cli/SzDiag.Cli.csproj`, `SzDiag.sln`

**Interfaces:**
- Produces: namespace `SzDiag.HubClient` с `IHubApiClient`, `HubApiClient(HttpClient http, string managementToken)`, `TriggerResult`, `NoteResult`, `RestartAgentOutcome` — сигнатуры без изменений.

- [ ] **Step 1: Проект библиотеки** (UTF-8 с BOM)

`src/SzDiag.HubClient/SzDiag.HubClient.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\SzDiag.Contracts\SzDiag.Contracts.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: Перенос файлов**

```bash
git mv src/SzDiag.Cli/HubApiClient.cs src/SzDiag.HubClient/HubApiClient.cs
git mv src/SzDiag.Cli/IHubApiClient.cs src/SzDiag.HubClient/IHubApiClient.cs
git mv tests/SzDiag.Cli.Tests/HubApiClientTests.cs tests/SzDiag.HubClient.Tests/HubApiClientTests.cs
```
В обоих перенесённых файлах `src/` заменить `namespace SzDiag.Cli;` на `namespace SzDiag.HubClient;`. В тестовом: `using SzDiag.Cli;` → `using SzDiag.HubClient;`, `namespace SzDiag.Cli.Tests;` → `namespace SzDiag.HubClient.Tests;`.

- [ ] **Step 3: CLI ссылается на библиотеку**

В `src/SzDiag.Cli/SzDiag.Cli.csproj` в первый `<ItemGroup>` добавить:
```xml
    <ProjectReference Include="..\SzDiag.HubClient\SzDiag.HubClient.csproj" />
```
`src/SzDiag.Cli/GlobalUsings.cs`:
```csharp
// Клиент hub живёт в общей библиотеке: им пользуются и CLI, и Desk (спека 2026-09-25) —
// копия в каждом приложении разошлась бы при первой правке протокола.
global using SzDiag.HubClient;
```

- [ ] **Step 4: Тестовый проект** (UTF-8 с BOM)

`tests/SzDiag.HubClient.Tests/SzDiag.HubClient.Tests.csproj`:
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
    <ProjectReference Include="..\..\src\SzDiag.HubClient\SzDiag.HubClient.csproj" />
    <ProjectReference Include="..\..\src\SzDiag.Contracts\SzDiag.Contracts.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Солюшен**

```bash
dotnet sln SzDiag.sln add src/SzDiag.HubClient/SzDiag.HubClient.csproj --solution-folder src
dotnet sln SzDiag.sln add tests/SzDiag.HubClient.Tests/SzDiag.HubClient.Tests.csproj --solution-folder tests
```

- [ ] **Step 6: Сборка и тесты**

Run: `dotnet build && dotnet test tests/SzDiag.HubClient.Tests && dotnet test tests/SzDiag.Cli.Tests`
Expected: сборка без ошибок; все тесты зелёные (число тестов `HubApiClientTests` то же, что было в Cli.Tests). Если какой-то файл CLI не видит тип — значит в нём был `using SzDiag.Cli;` ради клиента; глобальный using это покрывает, править не нужно.

- [ ] **Step 7: Commit**

```bash
git add -A src/SzDiag.HubClient tests/SzDiag.HubClient.Tests src/SzDiag.Cli tests/SzDiag.Cli.Tests SzDiag.sln
git commit -m "refactor(cli): клиент hub вынесен в библиотеку SzDiag.HubClient"
```

---

### Task 3: `SzLiveness` — единая классификация живости СЗ

**Files:**
- Create: `src/SzDiag.HubClient/SzLiveness.cs`
- Create: `tests/SzDiag.HubClient.Tests/SzLivenessTests.cs`
- Modify: `src/SzDiag.Cli/SessionTableRenderer.cs:48-85`

**Interfaces:**
- Produces: `enum SzLivenessState { Online, LagSuspected, NoContact, RevertFailed }`; `static SzLivenessState SzLiveness.Classify(SessionInfo s, DateTimeOffset now)`; `static readonly TimeSpan SzLiveness.LikelyFailureThreshold` (10 мин).

- [ ] **Step 1: Failing tests**

`tests/SzDiag.HubClient.Tests/SzLivenessTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.HubClient.Tests;

public class SzLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static SessionInfo Session(SessionStatus status, TimeSpan silentFor, string? revertNote = null)
        => new("161432", "10.0.0.5", "PC-1", status, Now.AddHours(-2), Now - silentFor, RevertNote: revertNote);

    [Fact]
    public void Online_IsOnline()
        => Assert.Equal(SzLivenessState.Online, SzLiveness.Classify(Session(SessionStatus.Online, TimeSpan.Zero), Now));

    [Fact]
    public void OfflineShortSilence_IsLagSuspected()
        => Assert.Equal(SzLivenessState.LagSuspected,
            SzLiveness.Classify(Session(SessionStatus.Offline, TimeSpan.FromMinutes(9)), Now));

    [Fact]
    public void OfflineAtThreshold_IsNoContact()
        => Assert.Equal(SzLivenessState.NoContact,
            SzLiveness.Classify(Session(SessionStatus.Offline, SzLiveness.LikelyFailureThreshold), Now));

    [Fact]
    public void RevertNote_WinsOverOnline()
        => Assert.Equal(SzLivenessState.RevertFailed,
            SzLiveness.Classify(Session(SessionStatus.Online, TimeSpan.Zero, "sshd не снят"), Now));
}
```

- [ ] **Step 2: Run — FAIL**

Run: `dotnet test tests/SzDiag.HubClient.Tests --filter FullyQualifiedName~SzLiveness`
Expected: ошибка компиляции «SzLiveness не найден».

- [ ] **Step 3: Реализация**

`src/SzDiag.HubClient/SzLiveness.cs`:
```csharp
using SzDiag.Contracts;

namespace SzDiag.HubClient;

public enum SzLivenessState { Online, LagSuspected, NoContact, RevertFailed }

/// <summary>Одна классификация живости СЗ на CLI (`list`/`watch`) и Desk — иначе окно и
/// терминал начали бы расходиться в том, что считать «лагом», а что «нет связи».
///
/// Уверенного «вырубон» здесь нет сознательно: единственное надёжное подтверждение реального
/// отказа — смена boot-time при реконнекте (<see cref="SessionInfo.RebootCount"/>), а молчание
/// heartbeat само по себе им не является — под многочасовым OCCT 10 минут молчания штатны
/// (бэклог п.42, CLAUDE.md).</summary>
public static class SzLiveness
{
    /// <summary>Порог, после которого молчание уже не спишешь на лаг heartbeat под нагрузкой.</summary>
    public static readonly TimeSpan LikelyFailureThreshold = TimeSpan.FromMinutes(10);

    public static SzLivenessState Classify(SessionInfo s, DateTimeOffset now)
    {
        // Неудачный откат важнее всего остального: доступ мог остаться на клиенте навсегда
        // (бэклог п.59, СЗ 160705).
        if (!string.IsNullOrEmpty(s.RevertNote)) return SzLivenessState.RevertFailed;
        if (s.Status == SessionStatus.Online) return SzLivenessState.Online;
        return now - s.LastHeartbeat >= LikelyFailureThreshold
            ? SzLivenessState.NoContact
            : SzLivenessState.LagSuspected;
    }
}
```

- [ ] **Step 4: CLI на общей классификации**

В `src/SzDiag.Cli/SessionTableRenderer.cs` заменить объявление порога:
```csharp
    public static readonly TimeSpan LikelyFailureThreshold = SzLiveness.LikelyFailureThreshold;
```
(комментарий над ним оставить), а тело `StatusCell` после строки `var sessionZero = …`:
```csharp
        var silentFor = now - s.LastHeartbeat;
        return SzLiveness.Classify(s, now) switch
        {
            SzLivenessState.RevertFailed => "[red]⚠ откат[/]",
            SzLivenessState.Online => $"[green]online[/]{sessionZero}",
            SzLivenessState.NoContact => $"[yellow]offline (нет связи {FormatElapsed(silentFor)})[/]{sessionZero}",
            _ => $"[grey]offline (лаг?)[/]{sessionZero}",
        };
```
(существующие комментарии про глифы и п.59 оставить над `return`).

- [ ] **Step 5: Run — PASS**

Run: `dotnet test tests/SzDiag.HubClient.Tests && dotnet test tests/SzDiag.Cli.Tests`
Expected: всё зелёное — вывод `list`/`watch` не изменился (тесты рендерера это проверяют).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.HubClient/SzLiveness.cs tests/SzDiag.HubClient.Tests/SzLivenessTests.cs src/SzDiag.Cli/SessionTableRenderer.cs
git commit -m "refactor(cli): классификация живости СЗ общая для CLI и Desk"
```

---

### Task 4: `TransferInfo` и `TransferTracker`

**Files:**
- Create: `src/SzDiag.Contracts/TransferInfo.cs`
- Create: `src/SzDiag.Hub/TransferTracker.cs`
- Create: `tests/SzDiag.Hub.Tests/ManualTime.cs`
- Create: `tests/SzDiag.Hub.Tests/TransferTrackerTests.cs`

**Interfaces:**
- Produces (Contracts): `enum TransferDirection { Push, Pull }`, `enum TransferState { Running, Done, Failed }`,
  `record TransferInfo(string Id, string Sz, TransferDirection Direction, string What, long? TotalBytes, long DoneBytes, double BytesPerSecond, DateTimeOffset StartedAt, TransferState State, string? Note = null, DateTimeOffset? FinishedAt = null)`,
  `static class TransferRoutes { const string List = "/api/transfers"; }`.
- Produces (Hub): `TransferTracker(TimeProvider time)`; `Start(string id, string sz, TransferDirection dir, string what, long? total = null)`, `SetTotal(string id, long total)`, `Add(string id, long bytes)`, `Finish(string id, string? error, string? note = null)`, `IReadOnlyList<TransferInfo> Snapshot()`, `static readonly TimeSpan KeepFinished` (10 мин). Все методы с неизвестным `id` — no-op.

- [ ] **Step 1: DTO**

`src/SzDiag.Contracts/TransferInfo.cs`:
```csharp
namespace SzDiag.Contracts;

public enum TransferDirection { Push, Pull }

public enum TransferState { Running, Done, Failed }

/// <summary>Передача файлов hub ↔ клиент для прогресс-баров (Desk, спека 2026-09-25).
/// Раньше `push`/`pull` были долгим синхронным запросом без единого признака жизни —
/// 300 МБ OCCT под плохой сетью выглядели как зависание.</summary>
/// <param name="What">Инструмент (push) или путь/маска на клиенте (pull).</param>
/// <param name="TotalBytes">null — объём заранее неизвестен (pull узнаёт его только в конце).</param>
/// <param name="Note">Для Done — итог («скачано 2, пропущено 5»), для Failed — причина.</param>
public sealed record TransferInfo(
    string Id,
    string Sz,
    TransferDirection Direction,
    string What,
    long? TotalBytes,
    long DoneBytes,
    double BytesPerSecond,
    DateTimeOffset StartedAt,
    TransferState State,
    string? Note = null,
    DateTimeOffset? FinishedAt = null);

public static class TransferRoutes
{
    public const string List = "/api/transfers";
}
```

- [ ] **Step 2: Ручные часы для тестов**

`tests/SzDiag.Hub.Tests/ManualTime.cs`:
```csharp
namespace SzDiag.Hub.Tests;

/// <summary>Часы, которые двигает тест: скорость и срок хранения передач считаются от времени.</summary>
public sealed class ManualTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan by) => Now += by;
}
```

- [ ] **Step 3: Failing tests**

`tests/SzDiag.Hub.Tests/TransferTrackerTests.cs`:
```csharp
using SzDiag.Contracts;

namespace SzDiag.Hub.Tests;

public class TransferTrackerTests
{
    private readonly ManualTime _time = new();
    private TransferTracker New() => new(_time);

    [Fact]
    public void Start_Add_ReportsBytesAndSpeed()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Push, "occt", 1000);
        _time.Advance(TimeSpan.FromSeconds(2));
        t.Add("r1", 400);

        var item = Assert.Single(t.Snapshot());
        Assert.Equal(TransferState.Running, item.State);
        Assert.Equal(1000, item.TotalBytes);
        Assert.Equal(400, item.DoneBytes);
        Assert.Equal(200, item.BytesPerSecond, precision: 1);
    }

    [Fact]
    public void SetTotal_FillsUnknownTotal()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Push, "occt");
        t.SetTotal("r1", 5000);
        Assert.Equal(5000, t.Snapshot().Single().TotalBytes);
    }

    [Fact]
    public void Finish_Success_MarksDoneWithNote()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Push, "occt", 1000);
        t.Finish("r1", error: null, note: "скачано 0, пропущено 5");

        var item = t.Snapshot().Single();
        Assert.Equal(TransferState.Done, item.State);
        Assert.Equal("скачано 0, пропущено 5", item.Note);
        Assert.NotNull(item.FinishedAt);
    }

    [Fact]
    public void Finish_WithError_MarksFailed()
    {
        var t = New();
        t.Start("r1", "161432", TransferDirection.Pull, "C:\\dumps\\*.dmp");
        t.Finish("r1", error: "таймаут");

        var item = t.Snapshot().Single();
        Assert.Equal(TransferState.Failed, item.State);
        Assert.Equal("таймаут", item.Note);
    }

    [Fact]
    public void Finished_DroppedAfterKeepWindow_RunningKept()
    {
        var t = New();
        t.Start("done", "161432", TransferDirection.Push, "occt");
        t.Finish("done", null);
        t.Start("run", "161432", TransferDirection.Push, "tm5");

        _time.Advance(TransferTracker.KeepFinished + TimeSpan.FromSeconds(1));

        Assert.Equal("run", Assert.Single(t.Snapshot()).Id);
    }

    [Fact]
    public void UnknownId_IsIgnored()
    {
        var t = New();
        t.Add("нет", 10);
        t.SetTotal("нет", 10);
        t.Finish("нет", null);
        Assert.Empty(t.Snapshot());
    }

    [Fact]
    public void Snapshot_NewestFirst()
    {
        var t = New();
        t.Start("old", "1", TransferDirection.Push, "a");
        _time.Advance(TimeSpan.FromSeconds(1));
        t.Start("new", "1", TransferDirection.Push, "b");
        Assert.Equal(new[] { "new", "old" }, t.Snapshot().Select(x => x.Id));
    }
}
```

- [ ] **Step 4: Run — FAIL**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~TransferTracker`
Expected: ошибка компиляции «TransferTracker не найден».

- [ ] **Step 5: Реализация**

`src/SzDiag.Hub/TransferTracker.cs`:
```csharp
using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Живой учёт передач push/pull для `GET /api/transfers` (прогресс-бары Desk, спека
/// 2026-09-25). In-memory: после рестарта hub незавершённые передачи всё равно мертвы.
/// Прогресс виден и для передач, запущенных Claude через `szcli`, — учёт на стороне hub,
/// а не в том, кто запустил.</summary>
public sealed class TransferTracker
{
    /// <summary>Сколько держать завершённую передачу в списке — чтобы итог успели увидеть.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public required string Id;
        public required string Sz;
        public required TransferDirection Direction;
        public required string What;
        public required DateTimeOffset StartedAt;
        public long? Total;
        public long Done;
        public TransferState State = TransferState.Running;
        public string? Note;
        public DateTimeOffset? FinishedAt;
    }

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _items = new();

    public TransferTracker(TimeProvider time) => _time = time;

    public void Start(string id, string sz, TransferDirection dir, string what, long? total = null)
        => _items[id] = new Entry
        {
            Id = id, Sz = sz, Direction = dir, What = what, Total = total, StartedAt = _time.GetUtcNow(),
        };

    public void SetTotal(string id, long total)
    {
        if (_items.TryGetValue(id, out var e)) lock (e) e.Total = total;
    }

    public void Add(string id, long bytes)
    {
        if (_items.TryGetValue(id, out var e)) lock (e) e.Done += bytes;
    }

    /// <param name="error">Не null — передача провалена, текст уходит в <see cref="TransferInfo.Note"/>.</param>
    public void Finish(string id, string? error, string? note = null)
    {
        if (!_items.TryGetValue(id, out var e)) return;
        lock (e)
        {
            e.State = error is null ? TransferState.Done : TransferState.Failed;
            e.Note = error ?? note;
            e.FinishedAt = _time.GetUtcNow();
        }
    }

    public IReadOnlyList<TransferInfo> Snapshot()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, e) in _items)
            if (e.FinishedAt is { } f && now - f > KeepFinished) _items.TryRemove(id, out _);

        return _items.Values
            .Select(e => { lock (e) return ToInfo(e, now); })
            .OrderByDescending(i => i.StartedAt)
            .ToList();
    }

    private static TransferInfo ToInfo(Entry e, DateTimeOffset now)
    {
        // Средняя скорость с начала передачи: для полосы прогресса этого хватает, а скользящее
        // окно дало бы дёрганые цифры при чанках по 1 МБ.
        var end = e.FinishedAt ?? now;
        var seconds = (end - e.StartedAt).TotalSeconds;
        var speed = seconds > 0 ? e.Done / seconds : 0;
        return new TransferInfo(e.Id, e.Sz, e.Direction, e.What, e.Total, e.Done, speed,
            e.StartedAt, e.State, e.Note, e.FinishedAt);
    }
}
```

- [ ] **Step 6: Run — PASS**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~TransferTracker`
Expected: 7 passed.

- [ ] **Step 7: Commit**

```bash
git add src/SzDiag.Contracts/TransferInfo.cs src/SzDiag.Hub/TransferTracker.cs tests/SzDiag.Hub.Tests/ManualTime.cs tests/SzDiag.Hub.Tests/TransferTrackerTests.cs
git commit -m "feat(hub): учёт передач push/pull для прогресс-баров"
```

---

### Task 5: Push/Pull отмечают байты, `GET /api/transfers`

**Files:**
- Create: `src/SzDiag.Hub/CountingReadStream.cs`
- Modify: `src/SzDiag.Contracts/PushCommand.cs:50-61` (`ToolRoutes`)
- Modify: `src/SzDiag.Agent/PushCommandHandler.cs:40-41,92` (передать `requestId`)
- Modify: `src/SzDiag.Hub/ToolsApi.cs`, `PushCoordinator.cs`, `PullCoordinator.cs`, `ManagementApi.cs`, `Program.cs:83-86`
- Test: `tests/SzDiag.Hub.Tests/PushEndToEndTests.cs`, `tests/SzDiag.Hub.Tests/PullEndToEndTests.cs`

**Interfaces:**
- Consumes: `TransferTracker` (задача 4).
- Produces: `ToolRoutes.Manifest(string tool, string? requestId = null)`, `ToolRoutes.File(string tool, string relativePath, string? requestId = null)` — параметр запроса `req`; `GET /api/transfers` → `List<TransferInfo>` (под management-токеном).

- [ ] **Step 1: Failing tests (push)**

Дописать в `PushEndToEndTests`:
```csharp
    private async Task<List<TransferInfo>> TransfersAsync()
        => (await Cli().GetFromJsonAsync<List<TransferInfo>>(TransferRoutes.List))!;

    [Fact]
    public async Task Push_ReportsTransferWithBytes()
    {
        await using var agent = await ConnectAgentAsync("160710", _clientDir);

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160710/push", new PushCommandRequest("occt"));
        resp.EnsureSuccessStatusCode();

        var t = Assert.Single(await TransfersAsync(), x => x.Sz == "160710");
        var expected = new FileInfo(Path.Combine(_toolsRoot, "occt", "OCCTCmd.exe")).Length
                     + new FileInfo(Path.Combine(_toolsRoot, "occt", "schedules", "long.json")).Length;
        Assert.Equal(TransferDirection.Push, t.Direction);
        Assert.Equal("occt", t.What);
        Assert.Equal(TransferState.Done, t.State);
        Assert.Equal(expected, t.TotalBytes);
        Assert.Equal(expected, t.DoneBytes);
    }

    [Fact]
    public async Task Push_Repeat_TransferDoneWithSkippedNote()
    {
        // Повтор уже доставленного: байт не приходит вовсе — передача не должна висеть на 0 %.
        await using var agent = await ConnectAgentAsync("160711", _clientDir);
        (await Cli().PostAsJsonAsync("/api/sessions/160711/push", new PushCommandRequest("occt"))).EnsureSuccessStatusCode();
        (await Cli().PostAsJsonAsync("/api/sessions/160711/push", new PushCommandRequest("occt"))).EnsureSuccessStatusCode();

        var latest = (await TransfersAsync()).Where(x => x.Sz == "160711").OrderByDescending(x => x.StartedAt).First();
        Assert.Equal(TransferState.Done, latest.State);
        Assert.Equal(0, latest.DoneBytes);
        Assert.Contains("пропущено 2", latest.Note);
    }

    [Fact]
    public async Task Transfers_RequiresManagementToken()
    {
        var resp = await _factory.CreateClient().GetAsync(TransferRoutes.List);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
```

- [ ] **Step 2: Failing tests (pull)**

Дописать в `PullEndToEndTests`:
```csharp
    [Fact]
    public async Task Pull_ReportsTransferWithReceivedBytes()
    {
        File.WriteAllBytes(Path.Combine(_clientDir, "big.dmp"), Bytes(10_000));
        await using var agent = await ConnectAgentAsync("160712");

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160712/pull",
            new PullCommandRequest(Path.Combine(_clientDir, "big.dmp")));
        resp.EnsureSuccessStatusCode();

        var list = await Cli().GetFromJsonAsync<List<TransferInfo>>(TransferRoutes.List);
        var t = Assert.Single(list!, x => x.Sz == "160712");
        Assert.Equal(TransferDirection.Pull, t.Direction);
        Assert.Equal(TransferState.Done, t.State);
        Assert.Equal(10_000, t.DoneBytes);
    }

    [Fact]
    public async Task Pull_UnknownSz_NoTransferLeftRunning()
    {
        var resp = await Cli().PostAsJsonAsync("/api/sessions/999999/pull", new PullCommandRequest("C:\\x"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);

        var list = await Cli().GetFromJsonAsync<List<TransferInfo>>(TransferRoutes.List);
        Assert.DoesNotContain(list!, x => x.State == TransferState.Running);
    }
```

- [ ] **Step 3: Run — FAIL**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter "FullyQualifiedName~PushEndToEnd|FullyQualifiedName~PullEndToEnd"`
Expected: новые тесты падают (404 на `/api/transfers`), старые зелёные.

- [ ] **Step 4: Маршруты с `requestId`**

В `src/SzDiag.Contracts/PushCommand.cs` заменить методы `ToolRoutes`:
```csharp
    /// <summary>Манифест инструмента: <c>/tools/{tool}/manifest</c>. <paramref name="requestId"/>
    /// привязывает запрос к передаче (прогресс в `/api/transfers`); старый агент его не шлёт —
    /// раздача работает как раньше, просто без прогресса.</summary>
    public static string Manifest(string tool, string? requestId = null)
        => $"{Prefix}/{tool}/manifest{Req(requestId, first: true)}";

    /// <summary>Файл инструмента: <c>/tools/{tool}/file?path=...</c>.</summary>
    public static string File(string tool, string relativePath, string? requestId = null)
        => $"{Prefix}/{tool}/file?path={Uri.EscapeDataString(relativePath)}{Req(requestId, first: false)}";

    private static string Req(string? requestId, bool first)
        => requestId is null ? "" : $"{(first ? '?' : '&')}req={Uri.EscapeDataString(requestId)}";
```

- [ ] **Step 5: Агент передаёт `requestId`**

В `src/SzDiag.Agent/PushCommandHandler.cs`: `ToolRoutes.Manifest(request.Tool)` → `ToolRoutes.Manifest(request.Tool, request.RequestId)`; у `DownloadAsync` добавить параметр `string requestId` первым после `tool` и в вызове `ToolRoutes.File(tool, file.Path, requestId)`; в месте вызова `DownloadAsync(...)` передать `request.RequestId`.

- [ ] **Step 6: Считающий поток**

`src/SzDiag.Hub/CountingReadStream.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>Поток на чтение, который сообщает о каждом отданном куске — так hub видит
/// прогресс push, хотя байты качает сам агент обычным HTTP.</summary>
public sealed class CountingReadStream(Stream inner, Action<long> onRead) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        if (n > 0) onRead(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        if (n > 0) onRead(n);
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 7: Раздача отмечает байты**

В `src/SzDiag.Hub/ToolsApi.cs` заменить два эндпоинта:
```csharp
        group.MapGet("/{tool}/manifest", (string tool, string? req, ToolCatalog catalog, TransferTracker transfers) =>
        {
            var manifest = catalog.Manifest(tool);
            if (manifest is null) return Results.NotFound();
            if (req is not null) transfers.SetTotal(req, manifest.TotalBytes);
            return Results.Ok(manifest);
        });

        group.MapGet("/{tool}/file", (string tool, string path, string? req, ToolCatalog catalog,
            TransferTracker transfers) =>
        {
            var full = catalog.ResolveFile(tool, path);
            // 404 и на «нет файла», и на попытку выйти за папку инструмента: подсказывать,
            // что путь существует, но запрещён, незачем.
            if (full is null) return Results.NotFound();
            Stream body = File.OpenRead(full);
            if (req is not null) body = new CountingReadStream(body, n => transfers.Add(req, n));
            return Results.File(body, "application/octet-stream", Path.GetFileName(full));
        });
```

- [ ] **Step 8: PushCoordinator ведёт передачу**

В `src/SzDiag.Hub/PushCoordinator.cs`: поле `private readonly TransferTracker? _transfers;`, конструктор
```csharp
    public PushCoordinator(SessionRegistry registry, IAgentCommandSender sender,
        int timeoutSeconds = PushLimits.TimeoutSeconds, TransferTracker? transfers = null)
```
(присвоить `_transfers = transfers;`). Тело `PushAsync` после `var requestId = …`:
```csharp
        _transfers?.Start(requestId, sz, TransferDirection.Push, tool);
        // Исход по умолчанию — «прервано»: исключение из SendPushAsync не должно оставить
        // передачу Running навсегда.
        string? transferError = "прервано";
        string? transferNote = null;
        var tcs = new TaskCompletionSource<PushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        try
        {
            await _sender.SendPushAsync(connId, new PushRequest(sz, requestId, tool), ct);

            var wait = TimeSpan.FromSeconds(_timeoutSeconds);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(wait, ct));
            if (done != tcs.Task)
            {
                transferError = $"агент не завершил за {wait.TotalSeconds:N0} с";
                throw new TimeoutException(
                    $"агент СЗ {sz} не завершил доставку '{tool}' за {wait.TotalSeconds:N0} с");
            }
            var result = await tcs.Task;
            transferError = result.Error;
            transferNote = $"скачано {result.Downloaded}, пропущено {result.Skipped}";
            return result;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            _transfers?.Finish(requestId, transferError, transferNote);
        }
```

- [ ] **Step 9: PullCoordinator ведёт передачу**

В `src/SzDiag.Hub/PullCoordinator.cs`: поле `private readonly TransferTracker? _transfers;`, конструктор
```csharp
    public PullCoordinator(SessionRegistry registry, IAgentCommandSender sender, string root,
        int timeoutSeconds = PullLimits.TimeoutSeconds, TransferTracker? transfers = null)
```
В `PullAsync` после `_pending[requestId] = session;`:
```csharp
        _transfers?.Start(requestId, sz, TransferDirection.Pull, path);
        string? transferError = "прервано";
```
Перед `throw new TimeoutException(hint);` — `transferError = hint;`. После `var result = await session.Done.Task;` — `transferError = result.Error;`. В `finally` первой строкой — `_transfers?.Finish(requestId, transferError);`. В `AcceptChunk` после `stream.Write(...)` — `_transfers?.Add(chunk.RequestId, chunk.Data.Length);`.

- [ ] **Step 10: DI и эндпоинт**

`src/SzDiag.Hub/Program.cs` — перед регистрацией `PullCoordinator`:
```csharp
builder.Services.AddSingleton(new TransferTracker(TimeProvider.System));
```
фабрику `PullCoordinator` — с трекером:
```csharp
    return new PullCoordinator(sp.GetRequiredService<SessionRegistry>(),
        sp.GetRequiredService<IAgentCommandSender>(), opts.PullRoot,
        transfers: sp.GetRequiredService<TransferTracker>());
```
`PushCoordinator` — фабрикой (DI не подставит трекер в параметр со значением по умолчанию надёжно):
```csharp
builder.Services.AddSingleton(sp => new PushCoordinator(sp.GetRequiredService<SessionRegistry>(),
    sp.GetRequiredService<IAgentCommandSender>(), transfers: sp.GetRequiredService<TransferTracker>()));
```
`src/SzDiag.Hub/ManagementApi.cs` — рядом с `/tools`:
```csharp
        // Прогресс push/pull для Desk (спека 2026-09-25): учёт на hub, поэтому видны и передачи,
        // запущенные через szcli.
        group.MapGet("/transfers", (TransferTracker transfers) => Results.Ok(transfers.Snapshot()));
```
(путь группы — `/api`, итог совпадает с `TransferRoutes.List`).

- [ ] **Step 11: Run — PASS**

Run: `dotnet test tests/SzDiag.Hub.Tests && dotnet test tests/SzDiag.Agent.Tests`
Expected: всё зелёное, включая 5 новых тестов.

- [ ] **Step 12: Commit**

```bash
git add src/SzDiag.Contracts/PushCommand.cs src/SzDiag.Agent/PushCommandHandler.cs src/SzDiag.Hub tests/SzDiag.Hub.Tests
git commit -m "feat(hub): /api/transfers — прогресс push и pull"
```

---

### Task 6: Клиент: передачи и здоровье hub

**Files:**
- Create: `src/SzDiag.Contracts/HealthzResponse.cs` (перенос record из `src/SzDiag.Hub/HealthApi.cs:9-17`)
- Modify: `src/SzDiag.Hub/HealthApi.cs` (удалить record, добавить `using SzDiag.Contracts;`), `tests/SzDiag.Hub.Tests/HealthApiTests.cs` (добавить `using SzDiag.Contracts;`, если нет)
- Modify: `src/SzDiag.HubClient/IHubApiClient.cs`, `src/SzDiag.HubClient/HubApiClient.cs`
- Test: `tests/SzDiag.HubClient.Tests/HubApiClientTransfersTests.cs`

**Interfaces:**
- Produces: `Task<IReadOnlyList<TransferInfo>> IHubApiClient.GetTransfersAsync(CancellationToken ct = default)` (старый hub, 404 → пустой список); `Task<HealthzResponse?> IHubApiClient.GetHealthAsync(CancellationToken ct = default)` (null — hub не ответил); `record HealthzResponse` теперь в `SzDiag.Contracts`.

- [ ] **Step 1: Перенос `HealthzResponse`**

Вырезать record (вместе с его `<summary>`) из `HealthApi.cs` в `src/SzDiag.Contracts/HealthzResponse.cs` с `namespace SzDiag.Contracts;`. В `HealthApi.cs` и `HealthApiTests.cs` — `using SzDiag.Contracts;`.

Run: `dotnet build`
Expected: без ошибок.

- [ ] **Step 2: Failing tests**

`tests/SzDiag.HubClient.Tests/HubApiClientTransfersTests.cs`:
```csharp
using System.Net;
using System.Text;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.HubClient.Tests;

public class HubApiClientTransfersTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static HubApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new Stub(respond)) { BaseAddress = new Uri("http://hub") }, "mgmt");

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetTransfers_ParsesList()
    {
        var c = Client(r =>
        {
            Assert.Equal("/api/transfers", r.RequestUri!.AbsolutePath);
            // enum'ы числами: hub не регистрирует JsonStringEnumConverter (проверено при написании плана).
            return Json("""[{"id":"r1","sz":"161432","direction":0,"what":"occt","totalBytes":100,"doneBytes":40,"bytesPerSecond":20,"startedAt":"2026-09-25T12:00:00Z","state":0}]""");
        });
        var t = Assert.Single(await c.GetTransfersAsync());
        Assert.Equal(40, t.DoneBytes);
        Assert.Equal(TransferState.Running, t.State);
    }

    [Fact]
    public async Task GetTransfers_OldHub_ReturnsEmpty()
    {
        var c = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await c.GetTransfersAsync());
    }

    [Fact]
    public async Task GetHealth_HubDown_ReturnsNull()
    {
        var c = Client(_ => throw new HttpRequestException("connection refused"));
        Assert.Null(await c.GetHealthAsync());
    }

    [Fact]
    public async Task GetHealth_Parses()
    {
        var c = Client(_ => Json("""{"threadCount":30,"availableWorkerThreads":32000,"maxWorkerThreads":32767,"availableCompletionPortThreads":1000,"maxCompletionPortThreads":1000,"pendingWorkItemCount":0,"at":"2026-09-25T12:00:00Z"}"""));
        Assert.Equal(30, (await c.GetHealthAsync())!.ThreadCount);
    }
}
```


- [ ] **Step 3: Run — FAIL**

Run: `dotnet test tests/SzDiag.HubClient.Tests --filter FullyQualifiedName~Transfers`
Expected: ошибка компиляции — методов нет.

- [ ] **Step 4: Реализация**

В `IHubApiClient`:
```csharp
    /// <summary>Передачи push/pull для прогресс-баров. Старый hub без эндпоинта (404) — пустой
    /// список: Desk должен работать и с ним, просто без прогресса.</summary>
    Task<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken ct = default);

    /// <summary>`/healthz` — null, если hub не ответил вовсе (статусбар Desk краснеет).</summary>
    Task<HealthzResponse?> GetHealthAsync(CancellationToken ct = default);
```
В `HubApiClient`:
```csharp
    public async Task<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken ct = default)
    {
        using var cts = Short(ct);
        var resp = await _http.GetAsync(TransferRoutes.List, cts.Token);
        if (resp.StatusCode == HttpStatusCode.NotFound) return Array.Empty<TransferInfo>();
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<TransferInfo>>(cts.Token) ?? new();
    }

    public async Task<HealthzResponse?> GetHealthAsync(CancellationToken ct = default)
    {
        using var cts = Short(ct);
        try
        {
            return await _http.GetFromJsonAsync<HealthzResponse>("/healthz", cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
```

- [ ] **Step 5: Run — PASS**

Run: `dotnet test tests/SzDiag.HubClient.Tests && dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~HealthApi`
Expected: всё зелёное.

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Contracts/HealthzResponse.cs src/SzDiag.Hub/HealthApi.cs tests/SzDiag.Hub.Tests/HealthApiTests.cs src/SzDiag.HubClient tests/SzDiag.HubClient.Tests
git commit -m "feat(hubclient): передачи и здоровье hub в клиенте"
```

---

### Task 7: Каркас Desk — окно, тема, заголовок с кнопками

**Files:**
- Create: `src/SzDiag.Desk/SzDiag.Desk.csproj`, `Program.cs`, `App.axaml`, `App.axaml.cs`, `app.manifest`, `appsettings.json`
- Create: `src/SzDiag.Desk/Theme/Tokens.axaml`, `Theme/Controls.axaml`
- Create: `src/SzDiag.Desk/Views/MainWindow.axaml(.cs)`, `Views/TitleBar.axaml(.cs)`
- Create: `tests/SzDiag.Desk.Tests/SzDiag.Desk.Tests.csproj`, `TestApp.cs`, `MainWindowSmokeTests.cs`
- Modify: `SzDiag.sln`

**Interfaces:**
- Produces: `App` (Avalonia `Application`), `MainWindow` с именованными частями `TitleBar`, `LeftPane`, `CenterPane`, `InspectorPane`, `StatusPane` (`x:Name`), ресурсы цветов `Bg.Window`, `Bg.Side`, `Bg.Panel`, `Line`, `Text.Primary`, `Text.Secondary`, `Text.Tertiary`, `Accent`, `Ok`, `Warn`, `Bad`, `Violet` (как `SolidColorBrush`).

- [ ] **Step 1: Проект** (UTF-8 с BOM)

```bash
dotnet new console -o src/SzDiag.Desk --framework net8.0
rm src/SzDiag.Desk/Program.cs
dotnet add src/SzDiag.Desk package Avalonia
dotnet add src/SzDiag.Desk package Avalonia.Desktop
dotnet add src/SzDiag.Desk package Avalonia.Themes.Fluent
dotnet add src/SzDiag.Desk package CommunityToolkit.Mvvm
dotnet add src/SzDiag.Desk package Microsoft.Extensions.Configuration.Json --version 8.0.1
dotnet add src/SzDiag.Desk package Microsoft.Extensions.Configuration.Binder --version 8.0.2
```
`dotnet add` без версии берёт последнюю стабильную; Avalonia-пакеты должны получить **одну и ту же** версию 11.x — проверить в csproj и выровнять. Итоговый csproj привести к виду:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\SzDiag.Contracts\SzDiag.Contracts.csproj" />
    <ProjectReference Include="..\SzDiag.HubClient\SzDiag.HubClient.csproj" />
    <ProjectReference Include="..\SzDiag.Kb\SzDiag.Kb.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- версии — как поставил dotnet add; все Avalonia.* одинаковые -->
    <PackageReference Include="Avalonia" Version="11.*" />
    <PackageReference Include="Avalonia.Desktop" Version="11.*" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.*" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.1" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <!-- Skia/HarfBuzz — нативные dll; без этого single-file публикация кладёт их рядом россыпью -->
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>

</Project>
```
(`11.*`/`8.*` заменить на точные версии, которые поставил `dotnet add`.)

`src/SzDiag.Desk/app.manifest` — стандартный манифест Avalonia-шаблона с `supportedOS` Win10 и `dpiAwareness` `PerMonitorV2` (скопировать из `dotnet new avalonia.app`, если шаблоны установлены: `dotnet new install Avalonia.Templates`; иначе — минимальный манифест с `<dpiAware>true/pm</dpiAware>` и `<dpiAwareness>PerMonitorV2</dpiAwareness>`). **Без** `requireAdministrator` — сессия на боксе идёт без elevation (CLAUDE.md).

`src/SzDiag.Desk/appsettings.json`:
```json
{
  "HubBaseUrl": "http://localhost:5000",
  "ManagementToken": "dev-token",
  "KbRoot": "kb"
}
```

- [ ] **Step 2: Точка входа и App**

`src/SzDiag.Desk/Program.cs`:
```csharp
using Avalonia;

namespace SzDiag.Desk;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
```
`src/SzDiag.Desk/App.axaml`:
```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="SzDiag.Desk.App"
             RequestedThemeVariant="Dark">
  <Application.Styles>
    <FluentTheme />
    <StyleInclude Source="avares://SzDiag.Desk/Theme/Controls.axaml" />
  </Application.Styles>
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceInclude Source="avares://SzDiag.Desk/Theme/Tokens.axaml" />
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```
`src/SzDiag.Desk/App.axaml.cs`:
```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SzDiag.Desk.Views;

namespace SzDiag.Desk;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 3: Токены темы**

`src/SzDiag.Desk/Theme/Tokens.axaml`:
```xml
<ResourceDictionary xmlns="https://github.com/avaloniaui"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <!-- Палитра из утверждённого макета B v2 (спека 2026-09-25). Одно место для цветов:
       экраны берут только эти ключи, иначе тёмная тема расползётся оттенками. -->
  <SolidColorBrush x:Key="Bg.Window" Color="#17181b" />
  <SolidColorBrush x:Key="Bg.TitleBar" Color="#1a1b1f" />
  <SolidColorBrush x:Key="Bg.Side" Color="#1c1d21" />
  <SolidColorBrush x:Key="Bg.Panel" Color="#212227" />
  <SolidColorBrush x:Key="Bg.Status" Color="#141518" />
  <SolidColorBrush x:Key="Bg.Selected" Color="#260a84ff" />
  <SolidColorBrush x:Key="Bg.Hover" Color="#12ffffff" />
  <SolidColorBrush x:Key="Line" Color="#2a2b31" />
  <SolidColorBrush x:Key="Text.Primary" Color="#e7e7ea" />
  <SolidColorBrush x:Key="Text.Secondary" Color="#8b8c94" />
  <SolidColorBrush x:Key="Text.Tertiary" Color="#5d5e66" />
  <SolidColorBrush x:Key="Accent" Color="#0a84ff" />
  <SolidColorBrush x:Key="Ok" Color="#30d158" />
  <SolidColorBrush x:Key="Warn" Color="#ff9f0a" />
  <SolidColorBrush x:Key="Bad" Color="#ff453a" />
  <SolidColorBrush x:Key="Violet" Color="#bf5af2" />
  <SolidColorBrush x:Key="CloseHover" Color="#e81123" />
  <FontFamily x:Key="UiFont">Segoe UI Variable Text, Segoe UI</FontFamily>
  <FontFamily x:Key="MonoFont">Cascadia Mono, Consolas</FontFamily>
</ResourceDictionary>
```

- [ ] **Step 4: Стили контролов**

`src/SzDiag.Desk/Theme/Controls.axaml`:
```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style Selector="Window">
    <Setter Property="FontFamily" Value="{StaticResource UiFont}" />
    <Setter Property="FontSize" Value="12.5" />
    <Setter Property="Foreground" Value="{StaticResource Text.Primary}" />
    <Setter Property="Background" Value="{StaticResource Bg.Window}" />
  </Style>

  <!-- Кнопки окна: вариант 2 макета — скруглённая плашка при наведении. -->
  <Style Selector="Button.caption">
    <Setter Property="Width" Value="34" />
    <Setter Property="Height" Value="28" />
    <Setter Property="CornerRadius" Value="6" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="#9a9ba3" />
    <Setter Property="HorizontalContentAlignment" Value="Center" />
    <Setter Property="VerticalContentAlignment" Value="Center" />
  </Style>
  <Style Selector="Button.caption:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{StaticResource Bg.Hover}" />
  </Style>
  <Style Selector="Button.caption.close:pointerover /template/ ContentPresenter">
    <Setter Property="Background" Value="{StaticResource CloseHover}" />
  </Style>
  <Style Selector="Button.caption.close:pointerover Path">
    <Setter Property="Stroke" Value="White" />
  </Style>
  <Style Selector="Button.caption Path">
    <Setter Property="Stroke" Value="{Binding $parent[Button].Foreground}" />
    <Setter Property="StrokeThickness" Value="1.1" />
    <Setter Property="Width" Value="10" />
    <Setter Property="Height" Value="10" />
    <Setter Property="Stretch" Value="None" />
  </Style>

  <!-- Иконка-кнопка (инспектор): «on» — подсветка акцентом. -->
  <Style Selector="ToggleButton.icon">
    <Setter Property="Width" Value="30" />
    <Setter Property="Height" Value="28" />
    <Setter Property="CornerRadius" Value="7" />
    <Setter Property="Padding" Value="0" />
    <Setter Property="Background" Value="Transparent" />
    <Setter Property="Foreground" Value="{StaticResource Text.Secondary}" />
  </Style>
  <Style Selector="ToggleButton.icon:checked /template/ ContentPresenter">
    <Setter Property="Background" Value="#220a84ff" />
    <Setter Property="BorderBrush" Value="#550a84ff" />
    <Setter Property="BorderThickness" Value="1" />
  </Style>
  <Style Selector="ToggleButton.icon:checked">
    <Setter Property="Foreground" Value="#4aa3ff" />
  </Style>

  <Style Selector="TextBlock.caps">
    <Setter Property="FontSize" Value="10.5" />
    <Setter Property="FontWeight" Value="SemiBold" />
    <Setter Property="Foreground" Value="{StaticResource Text.Tertiary}" />
    <Setter Property="LetterSpacing" Value="0.8" />
  </Style>
  <Style Selector="TextBlock.secondary">
    <Setter Property="Foreground" Value="{StaticResource Text.Secondary}" />
    <Setter Property="FontSize" Value="11" />
  </Style>
</Styles>
```
(Если `LetterSpacing` не поддерживается версией Avalonia — удалить сеттер: сборка XAML упадёт с явной ошибкой.)

- [ ] **Step 5: Заголовок окна**

`src/SzDiag.Desk/Views/TitleBar.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="SzDiag.Desk.Views.TitleBar"
             Height="36" Background="{StaticResource Bg.TitleBar}">
  <Grid ColumnDefinitions="*,Auto">
    <Border x:Name="DragArea" Background="Transparent">
      <TextBlock Text="SzDiag" Margin="14,0" VerticalAlignment="Center"
                 FontWeight="SemiBold" FontSize="12" Foreground="{StaticResource Text.Secondary}" />
    </Border>
    <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="2" Margin="0,0,4,0"
                VerticalAlignment="Center">
      <Button x:Name="MinButton" Classes="caption" ToolTip.Tip="Свернуть">
        <Path Data="M0,5 L10,5" />
      </Button>
      <Button x:Name="MaxButton" Classes="caption" ToolTip.Tip="Развернуть">
        <Path Data="M0.5,0.5 H9.5 V9.5 H0.5 Z" />
      </Button>
      <Button x:Name="CloseButton" Classes="caption close" ToolTip.Tip="Закрыть">
        <Path Data="M0,0 L10,10 M10,0 L0,10" />
      </Button>
    </StackPanel>
  </Grid>
</UserControl>
```
`src/SzDiag.Desk/Views/TitleBar.axaml.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SzDiag.Desk.Views;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        MinButton.Click += (_, _) => Owner()!.WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Owner()!.Close();
        DragArea.PointerPressed += OnDragPressed;
        DragArea.DoubleTapped += (_, _) => ToggleMaximize();
    }

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    private void ToggleMaximize()
    {
        var w = Owner();
        if (w is null) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            Owner()?.BeginMoveDrag(e);
    }
}
```

- [ ] **Step 6: Главное окно-каркас**

`src/SzDiag.Desk/Views/MainWindow.axaml`:
```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:v="using:SzDiag.Desk.Views"
        x:Class="SzDiag.Desk.Views.MainWindow"
        Title="SzDiag" Width="1280" Height="780" MinWidth="900" MinHeight="520"
        ExtendClientAreaToDecorationsHint="True"
        ExtendClientAreaChromeHints="NoChrome"
        ExtendClientAreaTitleBarHeightHint="36">
  <DockPanel>
    <Border DockPanel.Dock="Top" BorderBrush="{StaticResource Line}" BorderThickness="0,0,0,1">
      <v:TitleBar x:Name="TitleBar" />
    </Border>
    <Border x:Name="StatusPane" DockPanel.Dock="Bottom" Height="26"
            Background="{StaticResource Bg.Status}" BorderBrush="{StaticResource Line}" BorderThickness="0,1,0,0" />
    <Grid ColumnDefinitions="210,*,Auto">
      <Border x:Name="LeftPane" Background="{StaticResource Bg.Side}"
              BorderBrush="{StaticResource Line}" BorderThickness="0,0,1,0" />
      <Border x:Name="CenterPane" Grid.Column="1" />
      <Border x:Name="InspectorPane" Grid.Column="2" Width="290" Background="{StaticResource Bg.TitleBar}"
              BorderBrush="{StaticResource Line}" BorderThickness="1,0,0,0" />
    </Grid>
  </DockPanel>
</Window>
```
`src/SzDiag.Desk/Views/MainWindow.axaml.cs`:
```csharp
using Avalonia.Controls;

namespace SzDiag.Desk.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 7: Тестовый проект с headless-Avalonia** (UTF-8 с BOM)

```bash
dotnet new xunit -o tests/SzDiag.Desk.Tests --framework net8.0
rm tests/SzDiag.Desk.Tests/UnitTest1.cs
dotnet add tests/SzDiag.Desk.Tests reference src/SzDiag.Desk/SzDiag.Desk.csproj src/SzDiag.HubClient/SzDiag.HubClient.csproj src/SzDiag.Contracts/SzDiag.Contracts.csproj
dotnet add tests/SzDiag.Desk.Tests package Avalonia.Headless.XUnit
```
Версию `Avalonia.Headless.XUnit` выровнять с `Avalonia` из шага 1. Пакеты xunit в csproj выровнять с остальными тестами (`xunit` 2.5.3, `xunit.runner.visualstudio` 2.5.3, `Microsoft.NET.Test.Sdk` 17.8.0, `coverlet.collector` 6.0.0), добавить `<Using Include="Xunit" />`.

`tests/SzDiag.Desk.Tests/TestApp.cs`:
```csharp
using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(SzDiag.Desk.Tests.TestApp))]

namespace SzDiag.Desk.Tests;

public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<SzDiag.Desk.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
```
`App.OnFrameworkInitializationCompleted` в headless не создаёт окно (там нет `IClassicDesktopStyleApplicationLifetime`) — это ожидаемо.

- [ ] **Step 8: Дымовой тест — failing**

`tests/SzDiag.Desk.Tests/MainWindowSmokeTests.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SzDiag.Desk.Views;

namespace SzDiag.Desk.Tests;

public class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void Window_Shows_WithAllPanes()
    {
        var w = new MainWindow();
        w.Show();
        foreach (var name in new[] { "TitleBar", "LeftPane", "CenterPane", "InspectorPane", "StatusPane" })
            Assert.NotNull(w.FindControl<Control>(name));
    }

    [AvaloniaFact]
    public void CloseButton_ClosesWindow()
    {
        var w = new MainWindow();
        w.Show();
        var closed = false;
        w.Closed += (_, _) => closed = true;
        w.FindControl<TitleBar>("TitleBar")!.FindControl<Button>("CloseButton")!
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(closed);
    }
}
```

- [ ] **Step 9: Солюшен, сборка, тесты**

```bash
dotnet sln SzDiag.sln add src/SzDiag.Desk/SzDiag.Desk.csproj --solution-folder src
dotnet sln SzDiag.sln add tests/SzDiag.Desk.Tests/SzDiag.Desk.Tests.csproj --solution-folder tests
dotnet test tests/SzDiag.Desk.Tests
```
Expected: 2 passed.

- [ ] **Step 10: Ручная проверка окна**

Run: `dotnet run --project src/SzDiag.Desk`
Проверить глазами: тёмное окно, свой заголовок, три кнопки справа со скруглённой подсветкой, крестик краснеет, перетаскивание за заголовок, двойной клик разворачивает, ресайз за края работает. **Snap layouts Win11** (наведение на «развернуть»): если не появляются — записать пункт в `docs/dev-backlog.md` (Avalonia-версия, что пробовали) и не блокировать план.

- [ ] **Step 11: Commit**

```bash
git add src/SzDiag.Desk tests/SzDiag.Desk.Tests SzDiag.sln
git commit -m "feat(desk): каркас окна Avalonia — тема и свой заголовок"
```

---

### Task 8: `HubPoller` — опрос hub с откатом

**Files:**
- Create: `src/SzDiag.Desk/Services/HubSnapshot.cs`, `src/SzDiag.Desk/Services/HubPoller.cs`
- Create: `tests/SzDiag.Desk.Tests/FakeHubApi.cs`, `tests/SzDiag.Desk.Tests/HubPollerTests.cs`

**Interfaces:**
- Consumes: `IHubApiClient.GetSessionsAsync`, `GetTransfersAsync`, `GetHealthAsync`, `GetHubVersionAsync`.
- Produces:
  - `enum PollKind { Sessions, Transfers, Health }`
  - `record HubSnapshot(IReadOnlyList<SessionInfo> Sessions, IReadOnlyList<TransferInfo> Transfers, HealthzResponse? Health, string? HubVersion, DateTimeOffset? SessionsOkAt, string? Error, int Failures)` + `static HubSnapshot Empty`; `bool IsStale => Error is not null`.
  - `HubPoller(IHubApiClient api, TimeProvider time)`; `HubSnapshot Current`; `event Action<HubSnapshot>? Changed`; `Task PollOnceAsync(PollKind kind, CancellationToken ct)`; `TimeSpan NextDelay(PollKind kind)`; `Task RunAsync(CancellationToken ct)`.

- [ ] **Step 1: Фейковый клиент**

`tests/SzDiag.Desk.Tests/FakeHubApi.cs` — реализация `IHubApiClient`, где `GetSessionsAsync`/`GetTransfersAsync`/`GetHealthAsync`/`GetHubVersionAsync` берут результат из свойств, а остальные методы бросают `NotSupportedException`:
```csharp
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Tests;

public sealed class FakeHubApi : IHubApiClient
{
    public Func<IReadOnlyList<SessionInfo>> Sessions { get; set; } = () => Array.Empty<SessionInfo>();
    public Func<IReadOnlyList<TransferInfo>> Transfers { get; set; } = () => Array.Empty<TransferInfo>();
    public Func<HealthzResponse?> Health { get; set; } = () => null;
    public Func<string?> Version { get; set; } = () => "1.14";

    public Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default) => Task.FromResult(Sessions());
    public Task<IReadOnlyList<TransferInfo>> GetTransfersAsync(CancellationToken ct = default) => Task.FromResult(Transfers());
    public Task<HealthzResponse?> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(Health());
    public Task<string?> GetHubVersionAsync(CancellationToken ct = default) => Task.FromResult(Version());

    // Остальное Desk в части 1 не зовёт.
    private static Task<T> No<T>() => throw new NotSupportedException();
    public Task<CloseOutcome> CloseAsync(string sz, CancellationToken ct = default) => No<CloseOutcome>();
    public Task<NoteResult> AddNoteAsync(string sz, string text, CancellationToken ct = default) => No<NoteResult>();
    public Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default) => No<TargetInfo?>();
    public Task<TriggerResult> TriggerTestAsync(string sz, string? filter, string? config, bool sameConfig,
        string? schedule = null, CancellationToken ct = default) => No<TriggerResult>();
    public Task<OcctSchedulePlan?> GetOcctScheduleAsync(string? profile = null, CancellationToken ct = default) => No<OcctSchedulePlan?>();
    public Task<OcctReportSummary?> GetTestResultAsync(string sz, CancellationToken ct = default) => No<OcctReportSummary?>();
    public Task<bool> TriggerDiagAsync(string sz, string? sections = null, CancellationToken ct = default) => No<bool>();
    public Task<ExecResult?> ExecAsync(string sz, string script, int? timeoutSeconds = null, CancellationToken ct = default,
        bool detached = false, bool isolated = false, bool asSystem = false) => No<ExecResult?>();
    public Task<ExecJobStatus?> ExecStatusAsync(string sz, string jobId, int tailLines, CancellationToken ct = default) => No<ExecJobStatus?>();
    public Task<ExecJobStatus?> ExecCancelAsync(string sz, string jobId, CancellationToken ct = default) => No<ExecJobStatus?>();
    public Task<ExecJobStatus?> ExecJobsAsync(string sz, CancellationToken ct = default) => No<ExecJobStatus?>();
    public Task<PullResponse?> PullAsync(string sz, string path, long? maxBytes = null, bool recurse = false,
        string? label = null, CancellationToken ct = default) => No<PullResponse?>();
    public Task<PushResult?> PushAsync(string sz, string tool, CancellationToken ct = default) => No<PushResult?>();
    public Task<ToolCatalogInfo?> GetToolsAsync(CancellationToken ct = default) => No<ToolCatalogInfo?>();
    public Task<RebootTimeline?> GetRebootsAsync(string sz, CancellationToken ct = default) => No<RebootTimeline?>();
    public Task<bool> AddMaintenanceAsync(MaintenanceWindow window, CancellationToken ct = default) => No<bool>();
    public Task<IReadOnlyList<MaintenanceWindow>> GetMaintenanceAsync(string sz, CancellationToken ct = default) => No<IReadOnlyList<MaintenanceWindow>>();
    public Task<RestartAgentOutcome> RestartAgentAsync(string sz, CancellationToken ct = default) => No<RestartAgentOutcome>();
}
```
Если интерфейс к моменту реализации получил новые члены — дописать их тем же `=> No<…>()`; критерий шага — проект тестов компилируется.

- [ ] **Step 2: Failing tests**

`tests/SzDiag.Desk.Tests/HubPollerTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

public class HubPollerTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SessionInfo S(string sz) => new(sz, "10.0.0.5", "PC", SessionStatus.Online,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    private static TransferInfo T(TransferState state) => new("r1", "161432", TransferDirection.Push, "occt",
        100, 10, 5, DateTimeOffset.UtcNow, state);

    [Fact]
    public async Task Poll_Sessions_UpdatesSnapshotAndRaisesChanged()
    {
        var api = new FakeHubApi { Sessions = () => new[] { S("161432") } };
        var clock = new Clock();
        var p = new HubPoller(api, clock);
        HubSnapshot? raised = null;
        p.Changed += s => raised = s;

        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.Equal("161432", Assert.Single(p.Current.Sessions).Sz);
        Assert.Equal(clock.Now, p.Current.SessionsOkAt);
        Assert.False(p.Current.IsStale);
        Assert.Same(p.Current, raised);
    }

    [Fact]
    public async Task Poll_WhenHubDown_KeepsPreviousAndMarksStale()
    {
        var up = true;
        var api = new FakeHubApi { Sessions = () => up ? new[] { S("161432") } : throw new HttpRequestException("refused") };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Sessions, default);

        up = false;
        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.Single(p.Current.Sessions);
        Assert.True(p.Current.IsStale);
        Assert.Equal(1, p.Current.Failures);
    }

    [Fact]
    public async Task Poll_FirstCallFails_EmptyButStale()
    {
        var api = new FakeHubApi { Sessions = () => throw new HttpRequestException("refused") };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.Empty(p.Current.Sessions);
        Assert.True(p.Current.IsStale);
        Assert.Null(p.Current.SessionsOkAt);
    }

    [Fact]
    public async Task Poll_RecoveryClearsError()
    {
        var up = false;
        var api = new FakeHubApi { Sessions = () => up ? new[] { S("1") } : throw new HttpRequestException("x") };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Sessions, default);
        up = true;
        await p.PollOnceAsync(PollKind.Sessions, default);

        Assert.False(p.Current.IsStale);
        Assert.Equal(0, p.Current.Failures);
    }

    [Fact]
    public void NextDelay_Base()
    {
        var p = new HubPoller(new FakeHubApi(), new Clock());
        Assert.Equal(TimeSpan.FromSeconds(2), p.NextDelay(PollKind.Sessions));
        Assert.Equal(TimeSpan.FromSeconds(5), p.NextDelay(PollKind.Transfers));
        Assert.Equal(TimeSpan.FromSeconds(10), p.NextDelay(PollKind.Health));
    }

    [Fact]
    public async Task NextDelay_Transfers_FastWhileRunning()
    {
        var api = new FakeHubApi { Transfers = () => new[] { T(TransferState.Running) } };
        var p = new HubPoller(api, new Clock());
        await p.PollOnceAsync(PollKind.Transfers, default);
        Assert.Equal(TimeSpan.FromSeconds(1), p.NextDelay(PollKind.Transfers));
    }

    [Fact]
    public async Task NextDelay_BacksOffOnFailures_CappedAt30s()
    {
        var api = new FakeHubApi { Sessions = () => throw new HttpRequestException("x") };
        var p = new HubPoller(api, new Clock());
        for (var i = 0; i < 10; i++) await p.PollOnceAsync(PollKind.Sessions, default);
        Assert.Equal(TimeSpan.FromSeconds(30), p.NextDelay(PollKind.Sessions));
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `dotnet test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~HubPoller`
Expected: ошибка компиляции.

- [ ] **Step 4: Реализация**

`src/SzDiag.Desk/Services/HubSnapshot.cs`:
```csharp
using SzDiag.Contracts;

namespace SzDiag.Desk.Services;

public enum PollKind { Sessions, Transfers, Health }

/// <summary>Последнее, что известно от hub. При ошибке опроса прежние данные НЕ стираются —
/// окно показывает их с пометкой «данные на ЧЧ:ММ» (спека: «список СЗ заморожен»).</summary>
public sealed record HubSnapshot(
    IReadOnlyList<SessionInfo> Sessions,
    IReadOnlyList<TransferInfo> Transfers,
    HealthzResponse? Health,
    string? HubVersion,
    DateTimeOffset? SessionsOkAt,
    string? Error,
    int Failures)
{
    public static HubSnapshot Empty { get; } =
        new(Array.Empty<SessionInfo>(), Array.Empty<TransferInfo>(), null, null, null, null, 0);

    public bool IsStale => Error is not null;
}
```
`src/SzDiag.Desk/Services/HubPoller.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Services;

/// <summary>Опрос `/api` вместо push-канала (спека 2026-09-25: hub почти не трогаем).
/// Три независимых цикла с разной частотой; при недоступном hub — откат до 30 с.</summary>
public sealed class HubPoller(IHubApiClient api, TimeProvider time)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();

    public HubSnapshot Current { get; private set; } = HubSnapshot.Empty;

    public event Action<HubSnapshot>? Changed;

    public async Task PollOnceAsync(PollKind kind, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case PollKind.Sessions:
                    var sessions = await api.GetSessionsAsync(ct);
                    Update(s => s with { Sessions = sessions, SessionsOkAt = time.GetUtcNow(), Error = null, Failures = 0 });
                    break;
                case PollKind.Transfers:
                    var transfers = await api.GetTransfersAsync(ct);
                    Update(s => s with { Transfers = transfers });
                    break;
                case PollKind.Health:
                    var health = await api.GetHealthAsync(ct);
                    var version = health is null ? Current.HubVersion : await api.GetHubVersionAsync(ct);
                    Update(s => s with { Health = health, HubVersion = version });
                    break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Ошибку считаем только по списку СЗ: он главный признак «hub жив». Передачи и
            // здоровье падают вместе с ним и сами по себе статус не переключают.
            if (kind == PollKind.Sessions)
                Update(s => s with { Error = $"hub не отвечает: {ex.Message}", Failures = s.Failures + 1 });
        }
    }

    public TimeSpan NextDelay(PollKind kind)
    {
        var snap = Current;
        if (snap.Failures > 0)
        {
            var backoff = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(snap.Failures, 5)));
            return backoff > MaxBackoff ? MaxBackoff : backoff;
        }
        return kind switch
        {
            PollKind.Sessions => TimeSpan.FromSeconds(2),
            PollKind.Transfers => snap.Transfers.Any(t => t.State == TransferState.Running)
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };
    }

    public Task RunAsync(CancellationToken ct)
        => Task.WhenAll(Loop(PollKind.Sessions, ct), Loop(PollKind.Transfers, ct), Loop(PollKind.Health, ct));

    private async Task Loop(PollKind kind, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(kind, ct);
            try { await Task.Delay(NextDelay(kind), time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Update(Func<HubSnapshot, HubSnapshot> change)
    {
        HubSnapshot next;
        lock (_gate) Current = next = change(Current);
        Changed?.Invoke(next);
    }
}
```
Тест `NextDelay_BacksOffOnFailures_CappedAt30s`: после 10 ошибок `2^min(10,5)=32` → срезается до 30. ✓

- [ ] **Step 5: Run — PASS**

Run: `dotnet test tests/SzDiag.Desk.Tests --filter FullyQualifiedName~HubPoller`
Expected: 7 passed.

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Desk/Services tests/SzDiag.Desk.Tests
git commit -m "feat(desk): опрос hub с откатом при недоступности"
```

---

### Task 9: ViewModel'и — список СЗ, статусбар, передачи

**Files:**
- Create: `src/SzDiag.Desk/ViewModels/CollectionSync.cs`, `SzItemViewModel.cs`, `StatusBarViewModel.cs`, `TransferItemViewModel.cs`, `MainViewModel.cs`
- Create: `src/SzDiag.Desk/Services/DeskUiState.cs`
- Test: `tests/SzDiag.Desk.Tests/MainViewModelTests.cs`, `StatusBarViewModelTests.cs`, `TransferItemViewModelTests.cs`

**Interfaces:**
- Consumes: `HubSnapshot`, `SzLiveness.Classify`, `SzLivenessState`.
- Produces:
  - `SzItemViewModel` : `ObservableObject` — `string Sz`, `string Subtitle`, `SzLivenessState Liveness`, `int RebootCount`, `bool HasReboots`, `SessionInfo Info`; `void Update(SessionInfo s, DateTimeOffset now)`.
  - `TransferItemViewModel` — `string Id`, `string Title` («push occt → 161432»), `double? Percent` (null — неопределённый), `string Detail` («412 / 690 МБ · 17 МБ/с» / «готово · скачано 2, пропущено 5» / «ошибка: …»), `TransferState State`; `void Update(TransferInfo t)`.
  - `StatusBarViewModel` — `bool HubOk`, `string HubText`, `string? StaleText`; `void Apply(HubSnapshot s, DateTimeOffset now)`.
  - `MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time)` — `ObservableCollection<SzItemViewModel> Items`, `SzItemViewModel? Selected`, `ObservableCollection<TransferItemViewModel> Transfers`, `bool HasTransfers`, `StatusBarViewModel Status`, `bool IsInspectorOpen` (сохраняется в `DeskUiState`), `void Apply(HubSnapshot s)`.
  - `DeskUiState` — `bool InspectorOpen`; `static DeskUiState Load(string path)`; `void Save(string path)` (ошибки чтения → значения по умолчанию, `InspectorOpen = true`).
  - `static void CollectionSync.Sync<TVm, TSrc>(ObservableCollection<TVm> target, IEnumerable<TSrc> source, Func<TSrc,string> srcKey, Func<TVm,string> vmKey, Func<TSrc,TVm> create, Action<TVm,TSrc> update)`.

- [ ] **Step 1: Failing tests — MainViewModel**

`tests/SzDiag.Desk.Tests/MainViewModelTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.HubClient;

namespace SzDiag.Desk.Tests;

public class MainViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static SessionInfo S(string sz, SessionStatus st = SessionStatus.Online, int reboots = 0, string activity = "")
        => new(sz, "10.0.0.5", "PC-" + sz, st, Now.AddHours(-1), Now, Activity: activity, RebootCount: reboots);

    private static HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = Now };

    private static MainViewModel New() => new(new HubPoller(new FakeHubApi(), new Clock()), new DeskUiState(), new Clock());

    [Fact]
    public void Apply_AddsItemsSortedBySz()
    {
        var vm = New();
        vm.Apply(Snap(S("161520"), S("161432")));
        Assert.Equal(new[] { "161432", "161520" }, vm.Items.Select(i => i.Sz));
    }

    [Fact]
    public void Apply_UpdatesInPlace_KeepsInstanceAndSelection()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        var item = vm.Items[0];
        vm.Selected = item;

        vm.Apply(Snap(S("161432", reboots: 3)));

        Assert.Same(item, vm.Items[0]);
        Assert.Same(item, vm.Selected);
        Assert.Equal(3, item.RebootCount);
        Assert.True(item.HasReboots);
    }

    [Fact]
    public void Apply_RemovesVanishedAndClearsSelection()
    {
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161520")));
        vm.Selected = vm.Items.Single(i => i.Sz == "161520");

        vm.Apply(Snap(S("161432")));

        Assert.Equal("161432", Assert.Single(vm.Items).Sz);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public void Item_Liveness_And_Subtitle()
    {
        var vm = New();
        vm.Apply(Snap(S("161432", activity: "OCCT Combined"), S("161501", SessionStatus.Offline)));
        var a = vm.Items.Single(i => i.Sz == "161432");
        var b = vm.Items.Single(i => i.Sz == "161501");
        Assert.Equal(SzLivenessState.Online, a.Liveness);
        Assert.Equal("OCCT Combined", a.Subtitle);
        Assert.Equal(SzLivenessState.LagSuspected, b.Liveness);
        Assert.Equal("PC-161501", b.Subtitle);
    }

    [Fact]
    public void ToggleInspector_PersistsInUiState()
    {
        var ui = new DeskUiState { InspectorOpen = true };
        var vm = new MainViewModel(new HubPoller(new FakeHubApi(), new Clock()), ui, new Clock());
        vm.IsInspectorOpen = false;
        Assert.False(ui.InspectorOpen);
    }

    [Fact]
    public void Apply_Transfers_HasTransfersFlag()
    {
        var vm = New();
        vm.Apply(HubSnapshot.Empty with { Transfers = new[] {
            new TransferInfo("r1", "161432", TransferDirection.Push, "occt", 100, 50, 10, Now, TransferState.Running) } });
        Assert.True(vm.HasTransfers);
        vm.Apply(HubSnapshot.Empty);
        Assert.False(vm.HasTransfers);
    }
}
```

- [ ] **Step 2: Failing tests — статусбар и передачи**

`tests/SzDiag.Desk.Tests/StatusBarViewModelTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class StatusBarViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static HealthzResponse H(long pending = 0) => new(30, 32000, 32767, 1000, 1000, pending, Now);

    [Fact]
    public void Healthy_ShowsVersion()
    {
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Health = H(), HubVersion = "1.14", SessionsOkAt = Now }, Now);
        Assert.True(vm.HubOk);
        Assert.Equal("hub 1.14 · ok", vm.HubText);
        Assert.Null(vm.StaleText);
    }

    [Fact]
    public void Stale_ShowsLastDataTime()
    {
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Error = "hub не отвечает: refused", Failures = 2,
            SessionsOkAt = new DateTimeOffset(2026, 9, 25, 11, 58, 0, TimeSpan.Zero) }, Now);
        Assert.False(vm.HubOk);
        Assert.Equal("hub не отвечает", vm.HubText);
        Assert.StartsWith("данные на ", vm.StaleText);
    }

    [Fact]
    public void Starvation_IsNotOk()
    {
        // Очередь thread pool растёт — hub жив, но захлёбывается (бэклог п.50).
        var vm = new StatusBarViewModel();
        vm.Apply(HubSnapshot.Empty with { Health = H(pending: 500), HubVersion = "1.14", SessionsOkAt = Now }, Now);
        Assert.False(vm.HubOk);
        Assert.Contains("очередь 500", vm.HubText);
    }
}
```
`tests/SzDiag.Desk.Tests/TransferItemViewModelTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class TransferItemViewModelTests
{
    private static TransferInfo T(TransferDirection d, long? total, long done, TransferState st, string? note = null)
        => new("r1", "161432", d, "occt", total, done, 17 * 1024 * 1024, DateTimeOffset.UtcNow, st, note);

    [Fact]
    public void Running_WithTotal_PercentAndDetail()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Push, 690L << 20, 412L << 20, TransferState.Running));
        Assert.Equal("push occt → 161432", vm.Title);
        Assert.Equal(59.7, vm.Percent!.Value, precision: 1);
        Assert.Equal("412 / 690 МБ · 17 МБ/с", vm.Detail);
    }

    [Fact]
    public void Running_UnknownTotal_Indeterminate()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Pull, null, 3L << 20, TransferState.Running));
        Assert.Null(vm.Percent);
        Assert.Equal("pull occt ← 161432", vm.Title);
        Assert.Equal("3 МБ · 17 МБ/с", vm.Detail);
    }

    [Fact]
    public void Done_ShowsNote()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Push, 100, 0, TransferState.Done, "скачано 0, пропущено 2"));
        Assert.Equal(100.0, vm.Percent);
        Assert.Equal("готово · скачано 0, пропущено 2", vm.Detail);
    }

    [Fact]
    public void Failed_ShowsError()
    {
        var vm = new TransferItemViewModel(T(TransferDirection.Pull, null, 0, TransferState.Failed, "таймаут"));
        Assert.Equal("ошибка: таймаут", vm.Detail);
    }
}
```

- [ ] **Step 3: Run — FAIL**

Run: `dotnet test tests/SzDiag.Desk.Tests`
Expected: ошибка компиляции.

- [ ] **Step 4: Реализация**

`src/SzDiag.Desk/ViewModels/CollectionSync.cs`:
```csharp
using System.Collections.ObjectModel;

namespace SzDiag.Desk.ViewModels;

/// <summary>Синхронизация коллекции по ключу: существующие элементы обновляются на месте,
/// а не пересоздаются, — иначе каждый опрос раз в 2 с сбрасывал бы выделение и мигал списком.</summary>
public static class CollectionSync
{
    public static void Sync<TVm, TSrc>(ObservableCollection<TVm> target, IEnumerable<TSrc> source,
        Func<TSrc, string> srcKey, Func<TVm, string> vmKey, Func<TSrc, TVm> create, Action<TVm, TSrc> update)
    {
        var ordered = source.ToList();
        var keys = ordered.Select(srcKey).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!keys.Contains(vmKey(target[i]))) target.RemoveAt(i);

        for (var i = 0; i < ordered.Count; i++)
        {
            var key = srcKey(ordered[i]);
            var existing = -1;
            for (var j = i; j < target.Count; j++)
                if (vmKey(target[j]) == key) { existing = j; break; }

            if (existing < 0) target.Insert(i, create(ordered[i]));
            else
            {
                if (existing != i) target.Move(existing, i);
                update(target[i], ordered[i]);
            }
        }
    }
}
```
`src/SzDiag.Desk/ViewModels/SzItemViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.ViewModels;

public sealed partial class SzItemViewModel : ObservableObject
{
    public SzItemViewModel(SessionInfo s, DateTimeOffset now) { Sz = s.Sz; Update(s, now); }

    public string Sz { get; }

    [ObservableProperty] private SessionInfo _info = null!;
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private SzLivenessState _liveness;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasReboots))] private int _rebootCount;

    public bool HasReboots => RebootCount > 0;

    public void Update(SessionInfo s, DateTimeOffset now)
    {
        Info = s;
        // Чем занята машина важнее имени хоста; строка железа (CPU · плата) появится вместе
        // с паспортом в инспекторе (часть 3 плана).
        Subtitle = string.IsNullOrEmpty(s.Activity) ? s.Hostname : s.Activity;
        Liveness = SzLiveness.Classify(s, now);
        RebootCount = s.RebootCount;
    }
}
```
`src/SzDiag.Desk/ViewModels/TransferItemViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Contracts;

namespace SzDiag.Desk.ViewModels;

public sealed partial class TransferItemViewModel : ObservableObject
{
    public TransferItemViewModel(TransferInfo t) { Id = t.Id; Update(t); }

    public string Id { get; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private double? _percent;
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private TransferState _state;

    public void Update(TransferInfo t)
    {
        State = t.State;
        Title = t.Direction == TransferDirection.Push ? $"push {t.What} → {t.Sz}" : $"pull {t.What} ← {t.Sz}";
        Percent = t.State == TransferState.Done ? 100
            : t.TotalBytes is > 0 ? Math.Min(100, t.DoneBytes * 100.0 / t.TotalBytes.Value)
            : null;
        Detail = t.State switch
        {
            TransferState.Done => $"готово · {t.Note}",
            TransferState.Failed => $"ошибка: {t.Note}",
            _ => (t.TotalBytes is > 0 ? $"{Mb(t.DoneBytes)} / {Mb(t.TotalBytes.Value)} МБ" : $"{Mb(t.DoneBytes)} МБ")
                 + $" · {Mb((long)t.BytesPerSecond)} МБ/с",
        };
    }

    private static string Mb(long bytes) => (bytes / (1024 * 1024)).ToString();
}
```
`src/SzDiag.Desk/ViewModels/StatusBarViewModel.cs`:
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels;

public sealed partial class StatusBarViewModel : ObservableObject
{
    /// <summary>Сколько задач в очереди thread pool уже считать захлёбом (бэклог п.50: при
    /// starvation очередь росла сотнями, в норме — единицы).</summary>
    public const long StarvationQueue = 100;

    [ObservableProperty] private bool _hubOk;
    [ObservableProperty] private string _hubText = "hub …";
    [ObservableProperty] private string? _staleText;

    public void Apply(HubSnapshot s, DateTimeOffset now)
    {
        if (s.IsStale)
        {
            HubOk = false;
            HubText = "hub не отвечает";
            StaleText = s.SessionsOkAt is { } at ? $"данные на {at.ToLocalTime():HH:mm}" : "данных ещё не было";
            return;
        }

        StaleText = null;
        var version = s.HubVersion ?? "?";
        if (s.Health is { PendingWorkItemCount: >= StarvationQueue } h)
        {
            HubOk = false;
            HubText = $"hub {version} · очередь {h.PendingWorkItemCount}";
            return;
        }
        HubOk = true;
        HubText = $"hub {version} · ok";
    }
}
```
`src/SzDiag.Desk/Services/DeskUiState.cs`:
```csharp
using System.Text.Json;

namespace SzDiag.Desk.Services;

/// <summary>Мелкие настройки окна между запусками (открыт ли инспектор). Битый/недоступный
/// файл — не повод не открыть окно: берём значения по умолчанию.</summary>
public sealed class DeskUiState
{
    public bool InspectorOpen { get; set; } = true;

    public static DeskUiState Load(string path)
    {
        try { return JsonSerializer.Deserialize<DeskUiState>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this)); }
        catch { /* рядом с exe писать нельзя — просто не запомним */ }
    }
}
```
`src/SzDiag.Desk/ViewModels/MainViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeskUiState _ui;
    private readonly TimeProvider _time;

    public MainViewModel(HubPoller poller, DeskUiState ui, TimeProvider time)
    {
        Poller = poller;
        _ui = ui;
        _time = time;
        _isInspectorOpen = ui.InspectorOpen;
    }

    public HubPoller Poller { get; }
    public ObservableCollection<SzItemViewModel> Items { get; } = new();
    public ObservableCollection<TransferItemViewModel> Transfers { get; } = new();
    public StatusBarViewModel Status { get; } = new();

    [ObservableProperty] private SzItemViewModel? _selected;
    [ObservableProperty] private bool _hasTransfers;
    [ObservableProperty] private bool _isInspectorOpen;

    partial void OnIsInspectorOpenChanged(bool value) => _ui.InspectorOpen = value;

    /// <summary>Вызывать в UI-потоке (окно маршалит событие <see cref="HubPoller.Changed"/>).</summary>
    public void Apply(HubSnapshot s)
    {
        var now = _time.GetUtcNow();
        var selectedSz = Selected?.Sz;
        CollectionSync.Sync(Items, s.Sessions.OrderBy(x => x.Sz, StringComparer.Ordinal),
            x => x.Sz, vm => vm.Sz, x => new SzItemViewModel(x, now), (vm, x) => vm.Update(x, now));
        if (selectedSz is not null && Items.All(i => i.Sz != selectedSz)) Selected = null;

        CollectionSync.Sync(Transfers, s.Transfers, t => t.Id, vm => vm.Id,
            t => new TransferItemViewModel(t), (vm, t) => vm.Update(t));
        HasTransfers = Transfers.Count > 0;

        Status.Apply(s, now);
    }
}
```

- [ ] **Step 5: Run — PASS**

Run: `dotnet test tests/SzDiag.Desk.Tests`
Expected: все тесты зелёные (6 + 3 + 4 новых).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Desk/ViewModels src/SzDiag.Desk/Services/DeskUiState.cs tests/SzDiag.Desk.Tests
git commit -m "feat(desk): модели представления списка СЗ, статусбара и передач"
```

---

### Task 10: Представления и сборка окна

**Files:**
- Create: `src/SzDiag.Desk/Views/Converters.cs`, `SzListView.axaml(.cs)`, `InspectorView.axaml(.cs)`, `StatusBarView.axaml(.cs)`, `TransfersView.axaml(.cs)`
- Create: `src/SzDiag.Desk/Services/DeskOptions.cs`, `src/SzDiag.Desk/Services/DeskLog.cs`
- Modify: `src/SzDiag.Desk/Views/MainWindow.axaml(.cs)`, `src/SzDiag.Desk/App.axaml.cs`
- Test: `tests/SzDiag.Desk.Tests/MainWindowSmokeTests.cs`

**Interfaces:**
- Consumes: `MainViewModel`, `HubPoller`, `DeskUiState`, `HubApiClient`.
- Produces: `DeskOptions { string HubBaseUrl; string ManagementToken; string KbRoot; }` + `static DeskOptions Load()` (от `AppContext.BaseDirectory`); `DeskLog.Init()` — `logs\desk-<дата>.log`, `DeskLog.Write(string)`, чистка старше 14 дней; `MainWindow(MainViewModel vm)`; конвертеры `LivenessToBrush`, `TransferStateToBrush`.

- [ ] **Step 1: Конфиг и лог**

`src/SzDiag.Desk/Services/DeskOptions.cs`:
```csharp
using Microsoft.Extensions.Configuration;

namespace SzDiag.Desk.Services;

public sealed class DeskOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
    public string KbRoot { get; set; } = "kb";

    /// <summary>Конфиг рядом с exe, не от рабочего каталога (конвенция репо).</summary>
    public static DeskOptions Load()
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        var o = new DeskOptions();
        cfg.Bind(o);
        return o;
    }
}
```
`src/SzDiag.Desk/Services/DeskLog.cs`:
```csharp
namespace SzDiag.Desk.Services;

/// <summary>Лог окна в `logs\desk-&lt;дата&gt;.log` рядом с exe — по образцу HubLog: у GUI нет
/// консоли, и без файла упавший опрос или сессию нечем разбирать.</summary>
public static class DeskLog
{
    private static readonly object Gate = new();
    private static string? _dir;
    public const int RetentionDays = 14;

    public static void Init(string? baseDir = null)
    {
        _dir = Path.Combine(baseDir ?? AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var f in Directory.EnumerateFiles(_dir, "desk-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-RetentionDays)) File.Delete(f);
        }
        catch { _dir = null; }
    }

    public static void Write(string line)
    {
        if (_dir is null) return;
        lock (Gate)
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"desk-{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { /* лог не должен ронять окно */ }
        }
    }
}
```
В `HubPoller.PollOnceAsync` в `catch` добавить первой строкой `DeskLog.Write($"опрос {kind}: {ex.Message}");`.

- [ ] **Step 2: Конвертеры**

`src/SzDiag.Desk/Views/Converters.cs`:
```csharp
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Views;

public static class Converters
{
    private static IBrush Res(string key)
        => Application.Current!.TryGetResource(key, null, out var v) && v is IBrush b ? b : Brushes.Gray;

    /// <summary>Точка статуса СЗ. «Нет связи» — оранжевым, не красным: уверенно «вырубон» по
    /// одному молчанию heartbeat не говорим (бэклог п.42); красный — только сломанный откат.</summary>
    public static readonly IValueConverter LivenessToBrush = new FuncValueConverter<SzLivenessState, IBrush>(s => s switch
    {
        SzLivenessState.Online => Res("Ok"),
        SzLivenessState.LagSuspected => Res("Text.Tertiary"),
        SzLivenessState.NoContact => Res("Warn"),
        _ => Res("Bad"),
    });

    public static readonly IValueConverter LivenessToText = new FuncValueConverter<SzLivenessState, string>(s => s switch
    {
        SzLivenessState.Online => "онлайн",
        SzLivenessState.LagSuspected => "лаг heartbeat?",
        SzLivenessState.NoContact => "нет связи",
        _ => "⚠ откат не завершён",
    });

    public static readonly IValueConverter TransferStateToBrush = new FuncValueConverter<TransferState, IBrush>(s => s switch
    {
        TransferState.Done => Res("Ok"),
        TransferState.Failed => Res("Bad"),
        _ => Res("Accent"),
    });
}
```

- [ ] **Step 3: Список СЗ**

`src/SzDiag.Desk/Views/SzListView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             xmlns:v="using:SzDiag.Desk.Views"
             x:Class="SzDiag.Desk.Views.SzListView"
             x:DataType="vm:MainViewModel">
  <DockPanel>
    <TextBlock DockPanel.Dock="Top" Classes="caps" Margin="14,12,14,6"
               Text="{Binding Items.Count, StringFormat='ЗАЯВКИ · {0}'}" />
    <ListBox x:Name="List" ItemsSource="{Binding Items}" SelectedItem="{Binding Selected}"
             Background="Transparent" Margin="6,0">
      <ListBox.Styles>
        <Style Selector="ListBoxItem">
          <Setter Property="CornerRadius" Value="8" />
          <Setter Property="Padding" Value="10,8" />
          <Setter Property="Margin" Value="0,1" />
        </Style>
        <Style Selector="ListBoxItem:selected /template/ ContentPresenter">
          <Setter Property="Background" Value="{StaticResource Bg.Selected}" />
        </Style>
      </ListBox.Styles>
      <ListBox.ItemTemplate>
        <DataTemplate x:DataType="vm:SzItemViewModel">
          <Grid ColumnDefinitions="Auto,*,Auto">
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
          </Grid>
        </DataTemplate>
      </ListBox.ItemTemplate>
    </ListBox>
  </DockPanel>
</UserControl>
```
`SzListView.axaml.cs` — стандартный partial с `InitializeComponent()`.

- [ ] **Step 4: Инспектор (вкладка «Обзор»)**

`src/SzDiag.Desk/Views/InspectorView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             xmlns:v="using:SzDiag.Desk.Views"
             x:Class="SzDiag.Desk.Views.InspectorView"
             x:DataType="vm:MainViewModel">
  <Panel>
    <TextBlock IsVisible="{Binding Selected, Converter={x:Static ObjectConverters.IsNull}}"
               Text="Выбери СЗ слева" Classes="secondary" HorizontalAlignment="Center" VerticalAlignment="Center" />
    <StackPanel IsVisible="{Binding Selected, Converter={x:Static ObjectConverters.IsNotNull}}" Margin="12">
      <!-- Вкладки «Сенсоры / Задачи / Журнал / Железо» и действия — часть 3 плана. -->
      <TextBlock Classes="caps" Text="ОБЗОР" Margin="0,0,0,8" />
      <Border Background="{StaticResource Bg.Panel}" CornerRadius="10" Padding="11">
        <StackPanel Spacing="4">
          <TextBlock Text="{Binding Selected.Sz}" FontWeight="SemiBold" FontSize="14" />
          <TextBlock Text="{Binding Selected.Liveness, Converter={x:Static v:Converters.LivenessToText}}"
                     Foreground="{Binding Selected.Liveness, Converter={x:Static v:Converters.LivenessToBrush}}" />
          <TextBlock Classes="secondary" Text="{Binding Selected.Info.Hostname, StringFormat='хост: {0}'}" />
          <TextBlock Classes="secondary" Text="{Binding Selected.Info.LanIp, StringFormat='LAN: {0}'}" />
          <TextBlock Classes="secondary" Text="{Binding Selected.Info.AccessMode, StringFormat='доступ: {0}', TargetNullValue='доступ: direct'}" />
          <TextBlock Classes="secondary" Text="{Binding Selected.Info.BootTime, StringFormat='загрузка: {0:dd.MM HH:mm}'}" />
          <TextBlock Classes="secondary" Text="{Binding Selected.RebootCount, StringFormat='вырубонов за сессию: {0}'}" />
          <TextBlock Classes="secondary" TextWrapping="Wrap" Text="{Binding Selected.Info.Activity, StringFormat='занята: {0}'}" />
        </StackPanel>
      </Border>
    </StackPanel>
  </Panel>
</UserControl>
```
`InspectorView.axaml.cs` — стандартный partial.

- [ ] **Step 5: Передачи и статусбар**

`src/SzDiag.Desk/Views/TransfersView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             xmlns:v="using:SzDiag.Desk.Views"
             x:Class="SzDiag.Desk.Views.TransfersView"
             x:DataType="vm:MainViewModel">
  <ItemsControl ItemsSource="{Binding Transfers}" Margin="12,6">
    <ItemsControl.ItemTemplate>
      <DataTemplate x:DataType="vm:TransferItemViewModel">
        <Border Background="{StaticResource Bg.Panel}" CornerRadius="10" Padding="11,8" Margin="0,3">
          <StackPanel Spacing="4">
            <Grid ColumnDefinitions="*,Auto">
              <TextBlock Text="{Binding Title}" FontFamily="{StaticResource MonoFont}" FontSize="11.5" />
              <TextBlock Grid.Column="1" Text="{Binding Detail}" Classes="secondary" />
            </Grid>
            <ProgressBar Height="5" MinHeight="5" CornerRadius="3"
                         Minimum="0" Maximum="100"
                         Value="{Binding Percent, TargetNullValue=0}"
                         IsIndeterminate="{Binding Percent, Converter={x:Static ObjectConverters.IsNull}}"
                         Foreground="{Binding State, Converter={x:Static v:Converters.TransferStateToBrush}}"
                         Background="{StaticResource Line}" />
          </StackPanel>
        </Border>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</UserControl>
```
`src/SzDiag.Desk/Views/StatusBarView.axaml`:
```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:SzDiag.Desk.ViewModels"
             x:Class="SzDiag.Desk.Views.StatusBarView"
             x:DataType="vm:StatusBarViewModel">
  <StackPanel Orientation="Horizontal" Spacing="18" Margin="14,0" VerticalAlignment="Center">
    <StackPanel Orientation="Horizontal" Spacing="6">
      <Ellipse Width="7" Height="7" Fill="{StaticResource Ok}" IsVisible="{Binding HubOk}" />
      <Ellipse Width="7" Height="7" Fill="{StaticResource Bad}" IsVisible="{Binding !HubOk}" />
      <TextBlock Text="{Binding HubText}" Classes="secondary" />
    </StackPanel>
    <TextBlock Text="{Binding StaleText}" Classes="secondary" Foreground="{StaticResource Warn}"
               IsVisible="{Binding StaleText, Converter={x:Static ObjectConverters.IsNotNull}}" />
  </StackPanel>
</UserControl>
```
Code-behind у обоих — стандартный partial.

- [ ] **Step 6: Главное окно: наполнение и кнопка инспектора**

Заменить содержимое `<Grid ColumnDefinitions="210,*,Auto">` и `StatusPane` в `MainWindow.axaml` (в корень `Window` добавить `xmlns:vm="using:SzDiag.Desk.ViewModels"` и `x:DataType="vm:MainViewModel"`):
```xml
    <Border x:Name="StatusPane" DockPanel.Dock="Bottom" Height="26"
            Background="{StaticResource Bg.Status}" BorderBrush="{StaticResource Line}" BorderThickness="0,1,0,0">
      <v:StatusBarView DataContext="{Binding Status}" />
    </Border>
    <Grid ColumnDefinitions="210,*,Auto">
      <Border x:Name="LeftPane" Background="{StaticResource Bg.Side}"
              BorderBrush="{StaticResource Line}" BorderThickness="0,0,1,0">
        <v:SzListView />
      </Border>
      <DockPanel x:Name="CenterPane" Grid.Column="1">
        <Border DockPanel.Dock="Top" BorderBrush="{StaticResource Line}" BorderThickness="0,0,0,1"
                Padding="16,8,12,8">
          <Grid ColumnDefinitions="*,Auto">
            <TextBlock Text="{Binding Selected.Sz, FallbackValue='SzDiag'}" FontWeight="SemiBold"
                       VerticalAlignment="Center" />
            <ToggleButton x:Name="InspectorToggle" Grid.Column="1" Classes="icon"
                          IsChecked="{Binding IsInspectorOpen}" ToolTip.Tip="Инспектор СЗ">
              <Path Data="M1.5,2.5 H14.5 V13.5 H1.5 Z M10,2.5 V13.5" Stroke="{Binding $parent[ToggleButton].Foreground}"
                    StrokeThickness="1.4" Width="16" Height="16" Stretch="None" />
            </ToggleButton>
          </Grid>
        </Border>
        <v:TransfersView DockPanel.Dock="Bottom" IsVisible="{Binding HasTransfers}" />
        <!-- Чат сессии Claude — часть 2 плана. -->
        <TextBlock Text="Сессии Claude появятся в следующей версии" Classes="secondary"
                   HorizontalAlignment="Center" VerticalAlignment="Center" />
      </DockPanel>
      <Border x:Name="InspectorPane" Grid.Column="2" Width="290" Background="{StaticResource Bg.TitleBar}"
              BorderBrush="{StaticResource Line}" BorderThickness="1,0,0,0"
              IsVisible="{Binding IsInspectorOpen}">
        <v:InspectorView />
      </Border>
    </Grid>
```
`MainWindow.axaml.cs`:
```csharp
using Avalonia.Controls;
using Avalonia.Threading;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Для дизайнера XAML.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        // Событие опроса приходит с пула потоков — в коллекции окна пишем только из UI-потока.
        vm.Poller.Changed += snap => Dispatcher.UIThread.Post(() => vm.Apply(snap));
        Opened += (_, _) => _ = vm.Poller.RunAsync(_stop.Token);
        Closed += (_, _) => _stop.Cancel();
    }
}
```
`App.axaml.cs` — `OnFrameworkInitializationCompleted`:
```csharp
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DeskLog.Init();
            var opts = DeskOptions.Load();
            var http = new HttpClient { BaseAddress = new Uri(opts.HubBaseUrl) };
            var api = new HubApiClient(http, opts.ManagementToken);
            var uiPath = Path.Combine(AppContext.BaseDirectory, "desk-ui.json");
            var ui = DeskUiState.Load(uiPath);
            var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), ui, TimeProvider.System);
            desktop.MainWindow = new MainWindow(vm);
            desktop.Exit += (_, _) => ui.Save(uiPath);
            DeskLog.Write($"старт, hub {opts.HubBaseUrl}");
        }
```
(+ `using SzDiag.Desk.Services; using SzDiag.Desk.ViewModels; using SzDiag.HubClient;`).

- [ ] **Step 7: Дымовые тесты окна с моделью — failing → pass**

Дописать в `MainWindowSmokeTests`:
```csharp
    private static (MainWindow w, MainViewModel vm) WithVm()
    {
        var vm = new MainViewModel(new SzDiag.Desk.Services.HubPoller(new FakeHubApi(), TimeProvider.System),
            new SzDiag.Desk.Services.DeskUiState { InspectorOpen = true }, TimeProvider.System);
        var w = new MainWindow(vm);
        w.Show();
        return (w, vm);
    }

    [AvaloniaFact]
    public void InspectorToggle_HidesAndShowsInspector()
    {
        var (w, vm) = WithVm();
        var toggle = w.FindControl<ToggleButton>("InspectorToggle")!;
        var pane = w.FindControl<Control>("InspectorPane")!;
        Assert.True(pane.IsVisible);

        toggle.IsChecked = false;
        Assert.False(vm.IsInspectorOpen);
        Assert.False(pane.IsVisible);
    }

    [AvaloniaFact]
    public void Apply_ShowsSzInList()
    {
        var (w, vm) = WithVm();
        vm.Apply(SzDiag.Desk.Services.HubSnapshot.Empty with { Sessions = new[] {
            new SzDiag.Contracts.SessionInfo("161432", "10.0.0.5", "PC", SzDiag.Contracts.SessionStatus.Online,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) } });
        vm.Selected = vm.Items.Single();

        // Биндинги списка и инспектора на живом окне не падают, выбранная СЗ дошла до шапки.
        Assert.Single(vm.Items);
        Assert.True(w.IsVisible);
    }
```
(`using Avalonia.Controls.Primitives;` для `ToggleButton`.)

Run: `dotnet test tests/SzDiag.Desk.Tests`
Expected: всё зелёное.

- [ ] **Step 8: Живая проверка против hub**

Поднять hub (память: только через разовую `schtasks /it`, см. `start-hub-detached`), затем `dotnet run --project src/SzDiag.Desk` с `appsettings.json`, указывающим на этот hub. Проверить: список СЗ совпадает с `szcli list`; остановить hub → статусбар «hub не отвечает» + «данные на ЧЧ:ММ», список не пропал; поднять hub → зелёный за ≤ 30 с; `szcli push <СЗ> occt` на онлайн-СЗ → полоса прогресса над статусбаром, по завершении «готово · скачано …». Если онлайн-СЗ нет — пункт уходит в живой чек-лист (задача 11).

- [ ] **Step 9: Commit**

```bash
git add src/SzDiag.Desk tests/SzDiag.Desk.Tests
git commit -m "feat(desk): список СЗ, инспектор-обзор, передачи и статусбар в окне"
```

---

### Task 11: Раздача, документация, чек-лист

**Files:**
- Modify: `tools/build-dist.ps1:205-209` (список публикаций), `:387-402` (конфиг)
- Modify: `CLAUDE.md` (архитектура, команды), `docs/dev-knowledge-base.md` (`/api/transfers`, `requestId` в раздаче)
- Create: `docs/live-checklist-2026-09-25-desk.md`

- [ ] **Step 1: build-dist публикует Desk**

В список публикаций:
```powershell
    @{ Project = "src/SzDiag.Desk"; Out = "dist/host/desk" },
```
После записи конфига CLI:
```powershell
# Desk — тот же hub и тот же токен, что у CLI: окно и szcli обязаны видеть одно и то же.
$deskCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb"
}
"@
if ((Test-Path dist\host\desk) -and (Should-WriteConfig "dist/host/desk")) {
    Set-Content -Path dist\host\desk\appsettings.json -Value $deskCfg -Encoding utf8
}
```
Обновить комментарий «Три компонента независимы…» → «Компоненты независимы…», итоговую строку `Write-Host "Хост: …"` — добавить `desk\SzDiag.Desk.exe`. Файл сохранить в UTF-8 с BOM.

Run: `.\tools\build-dist.ps1`
Expected: `dist\host\desk\SzDiag.Desk.exe` запускается двойным кликом, окно открывается, `logs\desk-<дата>.log` появился рядом.

- [ ] **Step 2: Документация**

`CLAUDE.md`, раздел «Архитектура»: число проектов и два новых пункта —
- **SzDiag.HubClient** — клиент `/api` для CLI и Desk (`HubApiClient`, `SzLiveness` — единая классификация «онлайн / лаг? / нет связи / откат»).
- **SzDiag.Desk** — окно на Avalonia (спека `docs/superpowers/specs/2026-09-25-desk-gui-design.md`): список СЗ, инспектор, передачи, статусбар; опрос `/api`. Сессии Claude — следующим планом.

Раздел «Команды»: `dotnet run --project src/SzDiag.Desk` и `dist\host\desk\SzDiag.Desk.exe`.

`docs/dev-knowledge-base.md`: `GET /api/transfers` (DTO `TransferInfo`, 10 минут хранения завершённых), параметр `req` у `/tools/{tool}/manifest|file` (старый агент без него — без прогресса), `HealthzResponse` переехал в Contracts.

- [ ] **Step 3: Живой чек-лист**

`docs/live-checklist-2026-09-25-desk.md`:
```markdown
# Живой чек-лист: SzDiag Desk, часть 1 (2026-09-25)

- [ ] Desk из `dist\host\desk` открывается без elevation, лог пишется рядом с exe.
- [ ] Список СЗ совпадает с `szcli list`, статусы (онлайн / лаг? / нет связи) — тоже.
- [ ] Вырубон под тестом → `⚡N` на карточке растёт без перезапуска окна.
- [ ] `szcli push <СЗ> occt` → полоса прогресса в Desk, скорость правдоподобна, итог «готово · скачано …».
- [ ] Повторный push того же инструмента → «готово · скачано 0, пропущено N», не висит на 0 %.
- [ ] `szcli pull <СЗ> <дамп>` → неопределённая полоса с растущими МБ, итог «готово».
- [ ] Остановка hub → «hub не отвечает» + «данные на ЧЧ:ММ»; подъём → зелёный ≤ 30 с.
- [ ] Инспектор скрывается кнопкой, состояние переживает перезапуск Desk.
- [ ] Старый агент (без `req`) → push работает, прогресса нет, Desk не ломается.
- [ ] Snap layouts Win11 на кнопке «развернуть» (если нет — пункт в бэклоге).
```

- [ ] **Step 4: Полный прогон**

Run: `dotnet build && dotnet test`
Expected: сборка без предупреждений о новых проектах, все тесты зелёные (≈1380 + новые).

- [ ] **Step 5: Commit**

```bash
git add tools/build-dist.ps1 CLAUDE.md docs/dev-knowledge-base.md docs/live-checklist-2026-09-25-desk.md
git commit -m "chore(desk): публикация Desk в dist, документация и живой чек-лист"
```

---

## Дальше

После задачи 1 (спайк) и этого плана пишутся:
- **Часть 2** — `SzDiag.Claude` (процесс, парсер на фикстурах спайка, `SessionManager`, `DeskMcpServer` с `permission_prompt`) + чат в центре окна (лента, карточки инструментов, разрешения, очередь, стоп, `--resume`, «открыть в терминале», токены в статусбаре).
- **Часть 3** — вкладки инспектора (вырубоны, сенсоры, задачи, журнал, железо), действия, `GET /api/status` (версия пакета агента, бэкап kb, туннель), бейджи `🧊`/`≈`, `ask_peer`/`peers`.
