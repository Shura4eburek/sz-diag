# Dev knowledge base (карта функционала sz-diag)

> Плотный справочник по всему функционалу для быстрой навигации и правок без повторного
> обхода кодовой базы. Общий замысел — [vision.md](vision.md); архитектура/инварианты для
> ежедневной работы — [../CLAUDE.md](../CLAUDE.md). Здесь — протокол, точки расширения,
> таблицы параметров и рецепты. Обновлено 2026-07-21.

## Проекты (7 в `src/` + зеркальные тесты в `tests/`)

| Проект | Роль | Ключевые типы |
|---|---|---|
| `SzDiag.Contracts` | DTO + имена протокола (единый источник) + автообнаружение | `HubRoutes`, `DiscoveryProtocol`, `HubDiscovery`, DTO-записи |
| `SzDiag.Hub` | ASP.NET Core на хосте: SignalR `/agents` + `/api/*` + `/agent/*` | `AgentHub`, `ManagementApi`, `AgentPackageApi`, `SessionRegistry` |
| `SzDiag.Cli` (`szcli`) | тонкий клиент к `/api` | `HubApiClient`, команды в `Program.cs` |
| `SzDiag.Agent` | консоль на клиенте (админ, `app.manifest`) | `AgentSession`, `WindowsSystemAccessManager`, `PortableSshServer` |
| `SzDiag.Updater` | точка входа на клиенте: самообновление агента с hub | `HttpUpdateClient`, `PackageApplier`, `AgentLauncher` |
| `SzDiag.Hardware` | резолвер видях по PCI ID | `GpuResolver`, `VgaBiosScraper`, `GpuRepository` |
| `SzDiag.Kb` | Obsidian-vault базы знаний | `KbPaths`, `KnowledgeBaseScaffolder`, `ReportMarkdownBuilder` |

## Протокол (единственный источник имён — `SzDiag.Contracts/HubRoutes.cs`)

### SignalR-хаб `/agents` (`HubRoutes.Path`)

Токен агента — заголовок `X-SzDiag-Token` (`HubRoutes.TokenHeader`), проверяется middleware
на весь путь `/agents` (`Hub/Program.cs`).

**Агент → hub** (server-методы в `AgentHub`, зовутся `InvokeAsync`/`SendAsync` из `SignalRHubLink`):

| Метод (`HubRoutes`) | Сигнатура на hub | Что делает |
|---|---|---|
| `Register` | `Register(RegisterRequest{Sz,Hostname})` | IP из соединения; `SessionRegistry.Register` + `Kb.EnsureSkeleton` + `store.RecordOpenAsync` |
| `Heartbeat` | `Heartbeat(string sz)` | `Registry.Heartbeat` (обновляет `LastHeartbeat`, статус Online) |
| `ReportActivity` | `ReportActivity(sz, activity, since)` | `Registry.SetActivity`; `since=null` = простой. Fire-and-forget |
| `UploadReportFile` | `UploadReportFile(UploadReportPart{Sz,Timestamp,FileName,Content})` | `ReportStore.Save` → `kb/СЗ/<sz>/reports/<ts>/<file>` |

**Hub → агент** (client-методы; физически — `SignalRAgentCommandSender` через
`IHubContext<AgentHub>.Clients.Client(connId).SendAsync`; агент подписан в `SignalRHubLink`):

| Метод (`HubRoutes`) | Параметры | Обработчик на агенте |
|---|---|---|
| `Revert` | `sz` | `AgentSession` → `RevertCoordinator.TriggerAsync` (откат) |
| `RunTests` | `sz, filter?` | `Program.cs` `OnRunTests` → `TestReportRunner.RunAndUploadAsync` (стресс) |
| `RunDiag` | `sz, sections?` | `Program.cs` `OnRunDiag` → `DiagReportRunner.RunAndUploadAsync` (read-only снапшот → `diag.md`) |

Прямого RPC-возврата нет: hub **push-ит** команду, агент отвечает **отдельными** server-инвокациями
(`UploadReportFile`/`ReportActivity`). Новый вид результата = новый агент→hub метод по образцу.

### Management API `/api/*` (`Hub/ManagementApi.cs`)

CLI-токен — заголовок `X-SzDiag-Mgmt-Token` (`ManagementApi.TokenHeader`, **другой**, чем у агента),
проверяется endpoint-фильтром на всю группу `/api`. Пути **захардкожены** в `Cli/HubApiClient.cs`
(в `HubRoutes` их нет).

| Метод + путь | Сервис | Ответ |
|---|---|---|
| `GET /api/sessions` | `Registry.GetActive()` | `SessionInfo[]` |
| `POST /api/sessions/{sz}/close` | `SessionCloser.CloseAsync` | `Ok`/`NotFound` |
| `POST /api/sessions/{sz}/test` (тело `TestRunRequest{Filter,Config,SameConfig}`) | `TestRunTrigger.TriggerAsync` + метка конфигурации в SQLite и журнал | `Ok`/`NotFound`/`BadRequest` без метки |
| `POST /api/sessions/{sz}/journal` (тело `JournalNoteRequest{Text}`) | `JournalWriter.Manual` → `kb/СЗ/<sz>/журнал.md` | `Ok`/`BadRequest`; **активная сессия не требуется** |
| `POST /api/sessions/{sz}/diag?sections=` | `DiagRunTrigger.TriggerAsync` | `Ok`/`NotFound` |
| `GET /api/sessions/{sz}/target` | реестр + `ServiceAccount` | `TargetInfo{Sz,Ip,User,Ssh}`/`NotFound` |

### Раздача пакета агента `/agent/*` (`Hub/AgentPackageApi.cs`, для апдейтера)

Токен — тот же **агентский** `X-SzDiag-Token` (endpoint-фильтр на группе `/agent`). Файлы
берутся из `HubOptions.AgentDistRoot` (кладёт `build-dist` в `dist\host\hub\agent-dist\`).
Имена путей — в `HubRoutes` (`AgentVersionRoute`/`AgentPackageRoute`/`AgentPackageSha256Route`).

| Метод + путь | Отдаёт |
|---|---|
| `GET /agent/version` | версия пакета (plain text из `version.txt`) |
| `GET /agent/package` | `package.zip` (agent+ssh+ключ+testsuite, без appsettings/tools) |
| `GET /agent/package.sha256` | sha256 пакета (plain text) |

### Автообнаружение hub (`DiscoveryProtocol`, UDP `5098`)

Агент broadcast-ит `SZDIAG-DISCOVER:<token>` на все локальные подсети + `255.255.255.255`
(`HubDiscovery.FindHubAsync`), hub (`HubDiscoveryResponder`) отвечает unicast `SZDIAG-HUB:<port>`
только при совпадении `AgentToken`. Таймаут 3 c, повтор 500 мс. Если `AgentOptions.HubUrl` задан —
discovery не запускается.

### DTO (`SzDiag.Contracts`, все `sealed record`)

`RegisterRequest(Sz,Hostname)` · `SessionInfo(Sz,Ip,Hostname,Status,ConnectedAt,LastHeartbeat,Activity="",ActivitySince=null)`
· `SessionRecord(Sz,Ip,Hostname,OpenedAt,ClosedAt?)` (история) · `TargetInfo(Sz,Ip,User,Ssh)`
· `UploadReportPart(Sz,Timestamp,FileName,Content:byte[])`. Enum `SessionStatus{Online,Offline}`
(в JSON — число). Enum статусов тестов НЕТ (статус идёт меткой через `ReportActivity`).

## Агент (`SzDiag.Agent`)

### Режимы запуска (`Program.cs`)

- **Интерактивный**: спросить СЗ → `AccessSpec` → `WindowsSystemAccessManager.Open` (поднять
  доступ) → hub (явный `HubUrl` или discovery) → `AgentSession.StartAsync` (register) → heartbeat-цикл
  → ждать клавиш: `C` откат+выход, `Q` выход без отката. Крестик окна ловит `ConsoleCloseGuard`
  (P/Invoke `SetConsoleCtrlHandler`, `CTRL_CLOSE_EVENT`) → откат.
- **Watchdog** (`--revert <statePath>`): грузит `RevertState`, зовёт `Revert`, выходит. Ни консоли,
  ни SignalR. Запускается scheduled task по таймауту.

`RevertCoordinator` (`SemaphoreSlim`+флаг) гарантирует откат ровно один раз при любом числе триггеров
(крестик / `C` / hub `Revert`).

### Жизненный цикл доступа (`WindowsSystemAccessManager.Open`, после каждого шага `Persist()`)

| # | Шаг | Флаг `RevertState` |
|---|---|---|
| 1 | остановить системный `sshd`, если Running (держит порт) | `StoppedSystemSshd` |
| 2 | firewall-правило `szdiag-ssh-{sz}` на `SshPort` | `AddedFirewallRule` |
| 3 | `LocalAccountTokenFilterPolicy=1` (прежнее → `TokenPolicyPreviousValue`) | `SetTokenPolicy` |
| 4 | учётка `svc-diag` (`New-LocalUser` + Administrators по SID) | `CreatedUser` |
| 5 | `PortableSshServer.Start` — sshd **под SYSTEM** (задача `szdiag-sshd-{sz}`) | `GeneratedHostKeys`, `WroteAuthorizedKey`, `CreatedSshdTask` |
| 6 | watchdog scheduled task (`--revert`) под SYSTEM, `Now+WatchdogTimeout` | `CreatedWatchdogTask` |

`Revert` — обратный порядок, каждый шаг под своим флагом (идемпотентно). **Инвариант:** новый шаг
`Open` ⇒ парная ветка `Revert` под флагом, иначе следы на недоверенной машине.

`PortableSshServer` (план Б): свежие host-ключи ed25519 каждую сессию, свой `sshd_config`
(`Match Group administrators` → свой `AuthorizedKeysFile`), ACL SYSTEM+Administrators, запуск
транзиентной задачей под SYSTEM (`BuildRegisterTaskCommand`), готовность по поллингу порта
(`WaitForPort`, 5 c) вместо хендла процесса. `Stop` снимает задачу + добивает наш sshd по
`ConfigPath` в CommandLine (`BuildStopCommand`). Под SYSTEM есть `SeTcbPrivilege` для logon-token —
без него (дочерний процесс) `Connection reset` на userauth.

`RevertState.CreatedAuthorizedKeysFile` объявлен, но нигде не выставляется (удаление ключей идёт
с `WorkDir` по `GeneratedHostKeys`) — мёртвый флаг.

### AgentOptions (env-префикс `SZAGENT_`, относительные пути от `AppContext.BaseDirectory`)

`HubUrl=""` (пусто→discovery) · `AgentToken=""` · `ServiceAccount="svc-diag"` ·
`ServicePublicKeyPath="service_key.pub"` · `SshPort=22` · `WatchdogHours=6` · `HeartbeatSeconds=20` ·
`StatePath=C:\ProgramData\szdiag\state.json` · `TestSuitePath="testsuite.json"` · `LogPath=logs\agent.log` ·
`SshBinDir="ssh"` · `SshWorkDir=C:\ProgramData\szdiag\ssh`.

### Test-runner (`TestSuite`→`TestReportRunner`→`TestRunner`→`ICommandExecutor`/`IScreenCapturer`)

`testsuite.json` = `{Steps:[TestStep]}`. `TestStep`: `Type` (`command`|`screenshot`|`app`), `Name`,
`Id` (для фильтра), `Run` (PowerShell для command), `Exe`/`Args` (подстановка `{workdir}`)/
`DurationSeconds`/`KillImage` (для app-стресса), `ResultFile` (встроить текстом), `ArtifactFile`
(залить файлом), `RunToCompletion`, `CompletionWindowClass` (Win32-класс окна завершения).

`RunAndUploadAsync`: фильтр шагов → прогон в `Task.Run` с колбэком `OnStep`→`ReportActivity` →
`ReportMarkdownBuilder.Build`→`report.md` через `UploadReportFile` → скриншоты/артефакты отдельными
`UploadReportPart`. `allClean` = нет ошибок и нет `⚠`. Скриншот — `GdiScreenCapturer`
(`Graphics.CopyFromScreen`, только интерактивная сессия).

### Диагностика RunDiag (read-only снапшот)

`DiagnosticProbes` — встроенный каталог секций (`command`-пробы, Id=секция), не требует
`testsuite.json`, канал всегда доступен. Секции: `system cpu memory gpu storage temps drivers
events reboots whea reliability battery` (без `network`/`security`). Заточены под спонтанные
ребуты: `reboots` (Kernel-Power 41 со свойствами `BugcheckCode`/`PowerButtonTs`/`SleepInProgress`
+ dirty shutdown 6008 + BugCheck 1001), `whea` (WHEA-Logger **все уровни** — corrected идут
Warning и теряются в `events` Level=1,2), `memory` показывает `ConfiguredClockSpeed` vs паспортный
`Speed` (детект XMP/EXPO). `DiagReportRunner.RunAndUploadAsync(sz, sections?)` фильтрует секции
(`TestReportRunner.FilterSteps`), гоняет через тот же `TestRunner`, строит `diag.md`
(`DiagReportBuilder`, Kb), заливает одним `UploadReportPart`. Секции запускаются **точечно**
(`szcli diag run <СЗ> reboots,whea`), не всё пачкой — снапшот вместо россыпи ssh. gpu-проба даёт
`PCI\VEN_..&DEV_..&SUBSYS_..` прямо на вход hardware-резолверу.

**Передача проб в PowerShell** (`PowerShellRunner`): скрипт уходит через `-EncodedCommand`
(base64 UTF-16LE), **не** через stdin `-Command -` — последний в PS 5.1 обрывает многострочные
конвейеры (строка с хвостовым `|`/`,`), из-за чего снимался лишь первый ряд каждой секции. Тело
проб исторически держат **ASCII** (наследие stdin-режима; при EncodedCommand кириллица уже не
ломает парсер, но существующие пробы не переписывали — русские заголовки живут в C# `Name` →
`diag.md`).

## Апдейтер (`SzDiag.Updater`)

Точка входа на клиенте **вместо** прямого запуска агента — `SzDiag.Updater.exe`. Убирает ручной
цикл раздачи через share: на клиента кладётся один раз `Updater.exe` + `appsettings.json`, всё
остальное тянется само. `Program.cs` (оркестрация): найти hub (`HubUrl` или `HubDiscovery`,
**требуем hub**) → `HttpUpdateClient.GetVersionAsync` → сравнить с локальным `version.txt` → при
расхождении `DownloadPackageAsync` + сверка `GetPackageSha256Async` (`Hashing.Sha256File`) →
`PackageApplier.Apply` (распаковка поверх, **кроме** `appsettings.json`/`tools/`, атомарно через
staging) → `AgentLauncher.LaunchAndWait` (запуск `agent.exe` в наследованной консоли).

Деградация: старый hub без `/agent/*` (404) / битый sha256 / залоченный `agent.exe` → запустить
локального агента, если он есть, иначе внятный фейл. Читает те же `HubUrl`/`AgentToken` из общего
с агентом `appsettings.json` (`UpdaterOptions`, env-префикс `SZUPDATER_`). Пакет собирает
`build-dist` (`version.txt` = git short sha; zip без `appsettings`/`tools`/`Updater.exe`) в
`dist\host\hub\agent-dist\`. Сам Updater в пакет не входит — самообновление вне MVP.

## Хост (`SzDiag.Hub`)

- **`AgentHub`** — тонкий слой (см. таблицы протокола). IP берётся из соединения.
- **`SessionRegistry`** (singleton, `ConcurrentDictionary<sz,Entry{SessionInfo,ConnectionId}>`,
  `TimeProvider`): `Register`/`Heartbeat`/`SetActivity`/`MarkOfflineByConnection`/
  `MarkStaleOffline(maxAge)`/`TryGetConnectionId`/`GetActive`.
- **`SqliteSessionStore`** — таблица `sessions(id,sz,ip,hostname,opened_at,closed_at?)`, каждое
  открытие = строка. `RecordOpenAsync`/`RecordCloseAsync` (UPDATE последней незакрытой)/`GetHistoryAsync`.
- **`OfflineSweeper`** (`BackgroundService`): каждые `SweepInterval`(15c) → `MarkStaleOffline(HeartbeatTimeout=60c)`.
  Только метит Online→Offline, не удаляет.
- **KB-запись**: `Register`→`EnsureSkeleton(sz)` (идемпотентно, YAML-frontmatter + `запит/діагностика/дії.md`
  + `logs/`); `UploadReportFile`→`KbReportStore.Save` (санитайз `Path.GetFileName`). `EnsureSummarySkeleton`
  (`висновок.md`) hub'ом НЕ вызывается — точка для агента/CLI.
- **Оркестраторы** hub→агент: `SessionCloser`, `TestRunTrigger` — резолвят `connId` через
  `Registry.TryGetConnectionId(sz)`, зовут `IAgentCommandSender`; `false` при неизвестном connId.
- **Аутентификация**: `AgentToken` (`X-SzDiag-Token`, middleware на `/agents` + discovery),
  `ManagementToken` (`X-SzDiag-Mgmt-Token`, фильтр на `/api`). Оба из секции `Hub` конфига.

## CLI (`szcli`, `SzDiag.Cli`)

Диспетчер `switch` по `args[0]`, по умолчанию `watch`. Конфиг env-префикс `SZDIAG_`
(`HubBaseUrl=http://localhost:5000`, `ManagementToken`, `KbRoot=kb`, `GpuDbPath`, `PciIdsPath`).

- `watch` (дефолт) — Spectre `Live`, каждые 1000 мс `GET /api/sessions`, таблица СЗ/Статус/IP/Хост/Активность.
- `list` · `close <СЗ>` · `target <СЗ>` · `test run <СЗ> [фильтр]` · `diag run <СЗ> [секции]` — к соответствующим `/api`.
- `kb record/summary/search …` — локальная ФС через `SzDiag.Kb` (без HTTP).
- `hw import [path] / update / resolve "<PCI ID>"` — локальная БД + `VgaBiosScraper`.

## KB (`SzDiag.Kb`, Obsidian-vault, корень `kb/` — в .gitignore)

Пути — только `KbPaths`. **Контент базы знаний — на украинском** (папки, ключи, скелеты, проза).
Структура `kb/СЗ/<sz>/`: `<sz>.md` (единый frontmatter, ключи `сз/замовлення/дефект/замінено/
пристрій/симптом/статус/вердикт/дата`), `запит.md`, `діагностика.md`, `дії.md`, `висновок.md`
(встраивается `![[висновок]]` без своего YAML), `logs/`, `reports/<timestamp>/<file>`. Папки-разделы:
`СЗ/ Замовлення/ Дефекти/ Компоненти/ Пристрої/ Симптоми/`. Технические отчёты прогонов
(`report.md`/`diag.md`) — не kb-заметки, заголовки секций там из C# `Name` (пока русские).

## Hardware-резолвер (`SzDiag.Hardware`)

`PciId.Parse` (`PCI\VEN_..&DEV_..&SUBSYS_..`) → `GpuResolver` (кэш SQLite `gpu.db`: БД→miss→
`IGpuScraper`→запись). `VgaBiosScraper` (`TechPowerUpClient`+`VgaBiosParser` на AngleSharp)
дорезолвивает точную партнёрскую плату (SKU) и спеки прошивки по subsystem ID. `gpu-specs`-каталог
за CAPTCHA — вне scope (`NotImplementedGpuScraper` — заглушка).

## Рецепты расширения (точные места)

**Новая команда hub→агент** (образец `RunTests`), 6 согласованных мест:
1. `Contracts/HubRoutes.cs` — константа имени (+ DTO при нужде).
2. `Hub/IAgentCommandSender.cs` + `SignalRAgentCommandSender.cs` — `Clients.Client(connId).SendAsync(HubRoutes.X,...)`.
3. `Hub/` — сервис-оркестратор (образец `TestRunTrigger`), резолвит connId через `Registry.TryGetConnectionId`.
4. `Hub/ManagementApi.cs` — `group.MapPost(...)` (аутентификация группы применяется сама).
5. `Hub/Program.cs` — зарегистрировать сервис (образец строк `SessionCloser`/`TestRunTrigger`).
6. `Cli/HubApiClient.cs` + `Cli/Program.cs` — метод клиента + ветка команды.

**На агенте — приём команды** (образец `OnRunTests`):
1. `Agent/IHubLink.cs` — `OnX(handler)`; 2. `Agent/SignalRHubLink.cs` — `_conn.On<...>(HubRoutes.X,...)`;
3. `Agent/Program.cs` (прикладное, есть `link`/`reportRunner`) или `AgentSession.StartAsync`
(жизненный цикл). Долгие операции — через `Task.Run`, иначе блокируется поток SignalR.

**Возврат результата агент→hub**: `link.UploadReportFileAsync(UploadReportPart)` (файлы) /
`link.ReportActivityAsync(sz,label,since)` (статус). Новый вид — новый метод по образцу
`UploadReportFile` (константа + `IHubLink`/`SignalRHubLink` + приём в `AgentHub` + `HubRoutes`).

## Инварианты и подводные камни

- **Откат без следов**: каждый шаг `Open` ⇒ флаг + парная ветка `Revert`. Забыл → следы на клиенте.
- **Токены разные**: агентский `X-SzDiag-Token` ≠ управляющий `X-SzDiag-Mgmt-Token`.
- **UTF-8 с BOM** для файлов сборки (PowerShell 5.1 ломает кириллицу).
- **Пути от `AppContext.BaseDirectory`**, не от CWD.
- **`/api`-пути не в `HubRoutes`** — захардкожены в `HubApiClient` (менять в двух местах: hub-эндпоинт + клиент).
- **sshd только под SYSTEM** — дочерним процессом publickey-логин не работает.
- Секреты/`kb/`/`*.db` — в .gitignore, не коммитить.

## Быстрые команды

```powershell
dotnet build; dotnet test                 # ~174 теста, без хоста/клиента
dotnet test --filter FullyQualifiedName~RevertCoordinator
$env:SZDIAG_LIVE=1; dotnet test           # + live vgabios (ходит на TPU)
.\tools\build-dist.ps1 [-HubIp <LAN-IP>]  # публикация dist\host\ + dist\client\
```

E2e и траблшутинг (в т.ч. token-privilege плана Б, headless-управление по SSH) — [TESTING.md](TESTING.md).
