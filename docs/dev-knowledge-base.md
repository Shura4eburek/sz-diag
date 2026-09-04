# Dev knowledge base (карта функционала sz-diag)

> Плотный справочник по всему функционалу для быстрой навигации и правок без повторного
> обхода кодовой базы. Общий замысел — [vision.md](vision.md); архитектура/инварианты для
> ежедневной работы — [../CLAUDE.md](../CLAUDE.md). Здесь — протокол, точки расширения,
> таблицы параметров и рецепты. Обновлено 2026-09-04.

## Проекты (9 в `src/` + зеркальные тесты в `tests/`)

| Проект | Роль | Ключевые типы |
|---|---|---|
| `SzDiag.Contracts` | DTO + имена протокола (единый источник) + автообнаружение | `HubRoutes`, `DiscoveryProtocol`, `HubDiscovery`, DTO-записи |
| `SzDiag.Hub` | ASP.NET Core на хосте: SignalR `/agents` + `/api/*` + `/agent/*` | `AgentHub`, `ManagementApi`, `AgentPackageApi`, `SessionRegistry` |
| `SzDiag.Cli` (`szcli`) | тонкий клиент к `/api` | `HubApiClient`, команды в `Program.cs` |
| `SzDiag.Agent` | консоль на клиенте (админ, `app.manifest`) | `AgentSession`, `WindowsSystemAccessManager`, `PortableSshServer` |
| `SzDiag.Updater` | точка входа на клиенте: самообновление агента с hub | `HttpUpdateClient`, `PackageApplier`, `AgentLauncher` |
| `SzDiag.ConsoleUi` | консольный UI, общий для hub/агента/CLI | `StickyHeader`, `SyncedConsoleWriter`, `MarkupText` |
| `SzDiag.Hardware` | резолвер видях по PCI ID | `GpuResolver`, `VgaBiosScraper`, `GpuRepository` |
| `SzDiag.Kb` | Obsidian-vault базы знаний | `KbPaths`, `KnowledgeBaseScaffolder`, `ReportMarkdownBuilder` |
| `SzDiag.Erp` | локальный API учётной системы → база знаний | `ErpApiClient`, `ErpSession`, `ErpJson`, `ErpBlockBuilder`, `SzFetchWriter` |

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
| `RevertResult` | `RevertResult(Sz,Done,Failed)` | Итог отката ДО отключения канала (self-revert по `C`, close с хоста — пока коннект ещё жив). `RevertResultStore.Set` (деталь для `close`) + `SessionRegistry.MarkRevertOutcome` (тот же `SessionInfo.RevertNote`, что и у headless-пути `/agent/revert-status` ниже) + запись в журнал СЗ |
| `PullAck` | `PullAck(RequestId,AcceptedAt)` | Агент подтверждает ПРИЁМ команды `Pull` сразу (`SendAsync`, не ждёт ответа hub) — `PullCoordinator.Acknowledge`; различает «клиент не принял» (таймаут без ack) от «принял, но давит нагрузка/большой файл» (тот же приём, что у `Exec`, п.171) |
| `PullChunk` | `PullChunk(RequestId,FullPath,Index,Data:byte[],Last)` | Очередной кусок файла (SignalR-сообщение ограничено 10 МБ, поэтому файл режется чанками по 1 МБ) |
| `PullResult` | `PullResult(RequestId,Files:PullFileInfo[],Error?)` | Сводка ПОСЛЕ всех чанков: имя/размер/sha256/`Skipped`+`SkipReason`(`OverLimit`) на файл, либо `Error` целиком по запросу (путь не найден/нет прав) |

**Hub → агент** (client-методы; физически — `SignalRAgentCommandSender` через
`IHubContext<AgentHub>.Clients.Client(connId).SendAsync`; агент подписан в `SignalRHubLink`):

| Метод (`HubRoutes`) | Параметры | Обработчик на агенте |
|---|---|---|
| `Revert` | `sz` | `AgentSession` → `RevertCoordinator.TriggerAsync` (откат) |
| `RunTests` | `sz, filter?` | `Program.cs` `OnRunTests` → `TestReportRunner.RunAndUploadAsync` (стресс) |
| `RunDiag` | `sz, sections?` | `Program.cs` `OnRunDiag` → `DiagReportRunner.RunAndUploadAsync` (read-only снапшот → `diag.md`) |
| `Exec` | `ExecRequest{Sz,RequestId,Script,TimeoutSeconds,Detached,Isolated,AsSystem}` | `AgentCommandWiring` → `ExecCommandHandler.Handle`; ack сразу, результат отдельным `ExecResult`. `Isolated` (только с `Detached`) — `BackgroundJobs` оборачивает задачу в транзиентную scheduled task под SYSTEM (`szdiag-job-<сз>-<jobId>`, как sshd) вместо дочернего процесса агента — переживает падение/закрытие агента (бэклог п.53). `AsSystem` (без `Detached`) — `SystemExecRunner` гоняет тот же скрипт синхронно под SYSTEM тем же механизмом транзиентной задачи: часть операций (задачи `UpdateOrchestrator`, объекты TrustedInstaller) недоступна даже админу (бэклог п.39) |
| `ExecStatus` | `ExecStatusRequest{Sz,RequestId,JobId,TailLines,Cancel}` | `ExecCommandHandler.Status`; `JobId="*"` — список задач, `Cancel=true` — снять задачу (дерево процессов). Этот канал короткий и проходит под полной нагрузкой — поэтому отмена/список едут им же (бэклог п.134/172/176) |
| `RestartAgent` | `sz` | `AgentCommandWiring` → `NativeAgentRestart.Run`: свежий независимый `Process.Start(powershell.exe)`, В ОБХОД `IPowerShellRunner`/exec-очереди — раньше `agent restart` сам ходил через exec-канал и был бесполезен ровно тогда, когда нужен (бэклог п.202/п.215). Fire-and-forget, как `Revert` |
| `Pull` | `PullRequest{Sz,RequestId,Path,MaxBytes,Recurse}` | Забрать файл(ы) с клиента чанками (обходит лимит `exec` в 200k символов вывода). Агент шлёт `PullAck` сразу, затем `PullChunk` по `PullLimits.ChunkBytes`=1 МБ на файл, `PullResult` — сводка ПОСЛЕ всех чанков. `PullCoordinator` собирает чанки в `Hub.PullRoot\<sz>\<Label ?? метка_времени>\`, сверяет sha256 |

Прямого RPC-возврата нет: hub **push-ит** команду, агент отвечает **отдельными** server-инвокациями
(`UploadReportFile`/`ReportActivity`). Новый вид результата = новый агент→hub метод по образцу.

### Management API `/api/*` (`Hub/ManagementApi.cs`)

CLI-токен — заголовок `X-SzDiag-Mgmt-Token` (`ManagementApi.TokenHeader`, **другой**, чем у агента),
проверяется endpoint-фильтром на всю группу `/api`. Пути **захардкожены** в `Cli/HubApiClient.cs`
(в `HubRoutes` их нет).

| Метод + путь | Сервис | Ответ |
|---|---|---|
| `GET /api/sessions` | `Registry.GetActive()` | `SessionInfo[]` |
| `POST /api/sessions/{sz}/close` | `SessionCloser.CloseAsync` | `Ok{CloseOutcome{Closed,Revert?}}`/`NotFound`; `Revert` — сводка из `RevertResultStore` (агент уже мог прислать её `RevertResult`'ом ДО этого close), а не только от `wasOnline`-ожидания |
| `POST /api/sessions/{sz}/test` (тело `TestRunRequest{Filter,Config,SameConfig}`) | `TestRunTrigger.TriggerAsync` + метка конфигурации в SQLite и журнал | `Ok`/`NotFound`/`BadRequest` без метки |
| `POST /api/sessions/{sz}/journal` (тело `JournalNoteRequest{Text}`) | `JournalWriter.Manual` → `kb/СЗ/<sz>/журнал.md` | `Ok`/`BadRequest`; **активная сессия не требуется** |
| `POST /api/sessions/{sz}/diag?sections=` | `DiagRunTrigger.TriggerAsync` | `Ok`/`NotFound` |
| `GET /api/sessions/{sz}/target` | реестр + `ServiceAccount` | `TargetInfo{Sz,Ip,User,Ssh}`/`NotFound` |
| `POST /api/sessions/{sz}/exec` (тело `ExecCommandRequest{Script,TimeoutSeconds,Detached,Isolated,AsSystem}`) | `ExecCoordinator.RunAsync` | `ExecResult`/`NotFound`/`504`; без явного `TimeoutSeconds` дефолт зависит от `Activity` сессии (`ExecLimits.StressDefaultTimeoutSeconds`, если идёт стресс-прогон — бэклог п.35a) |
| `GET /api/sessions/{sz}/exec/{jobId}?tail=` | `ExecCoordinator.StatusAsync` | `ExecJobStatus` (+`Error` из `err.txt` при parse-ошибке скрипта; `LastOutputAt` — mtime `out.txt`, `szcli exec --result` печатает по нему «последняя строка N сек назад», пока задача выполняется — бэклог п.208) |
| `GET /api/sessions/{sz}/exec` | `StatusAsync(sz, "*")` | список фоновых задач (сводка в `Tail`) |
| `DELETE /api/sessions/{sz}/exec/{jobId}` | `StatusAsync(cancel: true)` | отмена задачи; `Cancelled=true` в ответе |
| `POST /api/sessions/{sz}/agent/restart` | `RestartAgentTrigger.TriggerAsync` | `Ok`/`NotFound`; отдельный от `ExecCoordinator` путь — не заходит в exec-очередь вовсе (бэклог п.202/п.215) |
| `POST /api/sessions/{sz}/pull` (тело `PullCommandRequest{Path,MaxBytes,Recurse,Label}`) | `PullCoordinator.PullAsync` | `PullResponse`/`NotFound`/`504`; кладёт на хост в `pulled\<sz>\<Label ?? метка_времени>\`. `Label` (например `jobs/<jobId>`) — сюда попадает `szcli exec --result --save`, чтобы вывод detached-задачи не терялся вместе с клиентом (бэклог п.214) |

Exit-коды `szcli exec` (`ExecExitCode`): 0 успех · N — код скрипта как есть · 3 отказ/ошибка
агента · 4 таймаут. `--result` мапится по исходу задачи (п.103).

### Раздача пакета агента `/agent/*` (`Hub/AgentPackageApi.cs`, для апдейтера)

Токен — тот же **агентский** `X-SzDiag-Token` (endpoint-фильтр на группе `/agent`). Файлы
берутся из `HubOptions.AgentDistRoot` (кладёт `build-dist` в `dist\host\hub\agent-dist\`).
Имена путей — в `HubRoutes` (`AgentVersionRoute`/`AgentPackageRoute`/`AgentPackageSha256Route`).

| Метод + путь | Отдаёт |
|---|---|
| `GET /agent/version` | версия пакета (plain text из `version.txt`) |
| `GET /agent/package` | `package.zip` (agent+ssh+ключ+testsuite, без appsettings/tools) |
| `GET /agent/package.sha256` | sha256 пакета (plain text) |

### Итог отката вне SignalR `/agent/revert-status` (`Hub/RevertStatusApi.cs`)

Тот же токен/префикс, что у раздачи пакета. `agent.exe --revert` (watchdog-задача, headless-
откат после ребута) POST'ит `RevertStatusReport{Sz,Success,Summary}` — в этом режиме нет живого
SignalR-коннекта, чтобы ответить обычным путём. `Sz` валидируется `SzNumber.IsValid` (агентский
токен общий на всех агентов — иначе произвольная строка уезжала мимо vault), плюс сверка IP
вызова с IP, под которым эта СЗ зарегистрирована по SignalR (`RevertStatusApi.IsAuthorizedForSz`,
permissive, если сверять не с чем). `SessionRegistry.MarkRevertOutcome`: успех — `ISessionStore.
RecordCloseAsync` + `Remove(sz)`; неудача — `Status=Offline` + `SessionInfo.RevertNote`, и
`list`/`watch` показывают `⚠ откат` вместо `online`/`offline` (бэклог п.59) — без этого упавший
на середине откат оставлял доступ на клиенте, а hub считал СЗ штатной. Тот же `SessionInfo.
RevertNote` выставляет и живой SignalR-путь `RevertResult` выше — единое состояние сессии
независимо от того, кто откат инициировал (self-revert по `C`, close с хоста, watchdog).

### Диагностика самого hub `/healthz` (`Hub/HealthApi.cs`)

**Без токена** (`Program.cs` фильтрует токен-мидлварой только `HubRoutes.Path`, `/healthz` вне
неё — сознательно новая неаутентифицированная поверхность, данных не отдаёт). Отвечает даже
когда hub захлебнулся в thread pool starvation: не трогает `SessionRegistry`/SQLite/SignalR,
только счётчики самого рантайма (`ThreadPool.GetAvailableThreads`/`GetMaxThreads`,
`PendingWorkItemCount`, число потоков процесса). Раньше отличить «hub жив, но в starvation» от
«hub умер» можно было только руками через `Get-Process` (СЗ 160306, бэклог п.50: 3674 потока
при здоровых 26 сразу после рестарта). Парный сторож — `ThreadPoolWatchdog` (`BackgroundService`, опрос раз в 30 c): при первом
переходе `ThreadCount >= HubOptions.ThreadPoolWarnThreshold` пишет явную строку `THREAD POOL
STARVATION` (уносится в `hub-<дата>.log` тем же Tee, что и весь консольный вывод) — залипание
видно ДО того, как перестанут отвечать HTTP-запросы, а не после; повторно предупреждает только
если порог отпустило и снова превышен (не спамит на каждом тике).

### Автообнаружение hub (`DiscoveryProtocol`, UDP `5098`)

Агент broadcast-ит `SZDIAG-DISCOVER:<token>` на все локальные подсети + `255.255.255.255`
(`HubDiscovery.FindHubAsync`), hub (`HubDiscoveryResponder`) отвечает unicast `SZDIAG-HUB:<port>`
только при совпадении `AgentToken`. Таймаут 3 c, повтор 500 мс. Если `AgentOptions.HubUrl` задан —
discovery не запускается.

### DTO (`SzDiag.Contracts`, все `sealed record`)

`RegisterRequest(Sz,Hostname,BootTime=null,LastShutdown=null,AgentUser=null,AgentSessionId=null)`
(`AgentUser` — `WindowsIdentity.GetCurrent().Name`, `NT AUTHORITY\СИСТЕМА` после автостарт-задачи
vs `<машина>\<юзер>` при ручном запуске: из session 0 GUI-операции ломаются молча, п.220;
`AgentSessionId` — сессия Windows агента, 0 = служебная без рабочего стола) ·
`SessionInfo(Sz,Ip,Hostname,Status,ConnectedAt,LastHeartbeat,Activity="",ActivitySince=null,BootTime=null,LastRebootAt=null,RebootCount=0,RevertNote=null,AgentUser=null,AgentSessionId=null)`
(те же `AgentUser`/`AgentSessionId`, что и в `RegisterRequest` — прокинуты в реестр для
`client info`/CLI; `AgentInSessionZero` — вычисляемое свойство `AgentSessionId == 0`)
· `SessionRecord(Sz,Ip,Hostname,OpenedAt,ClosedAt?)` (история) · `TargetInfo(Sz,Ip,User,Ssh)`
· `UploadReportPart(Sz,Timestamp,FileName,Content:byte[])` ·
`RevertResult(Sz,Done:string[],Failed:RevertResultFailure[])` (агент → hub, живой канал) ·
`RevertStatusReport(Sz,Success,Summary)` (агент → hub, HTTP/headless) ·
`CloseOutcome(Closed,Revert:RevertResult?)` (hub → CLI, ответ `close`). Enum
`SessionStatus{Online,Offline}` (в JSON — число). Enum статусов тестов НЕТ (статус идёт меткой
через `ReportActivity`).

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
events reboots whea thermal livekernel reliability battery` (без `network`/`security`). Заточены
под спонтанные ребуты: `reboots` (Kernel-Power 41 со свойствами
`BugcheckCode`/`PowerButtonTs`/`SleepInProgress` + dirty shutdown 6008 + BugCheck 1001), `whea`
(WHEA-Logger **все уровни** — corrected идут Warning и теряются в `events` Level=1,2), `thermal`
(`Kernel-Processor-Power` Id 37/86 с явным `ProviderName` + распределение hard-off по времени
суток + явная строка «THERMTRIP не логируется» — его отсутствие не исключает перегрев, бэклог
п.36b), `memory` показывает `ConfiguredClockSpeed` vs паспортный `Speed` (детект XMP/EXPO).
Единый словарь имён секций для CLI/агента — `DiagSections` (Contracts). Единое окно поиска по
журналу (30 дней) + печать глубины журнала — `EventWindow` (п.123); историческая нумерация
`\Device\HarddiskN`/`RaidPortN` на момент события (не «на сейчас») — `DiskNumberHistory`,
источник `Partition/Diagnostic` 1006 (п.133). `DiagReportRunner.RunAndUploadAsync(sz, sections?)`
фильтрует секции
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
остальное тянется само. `Program.cs` (оркестрация): **`CloudInstallGuard.Check(baseDir)`** —
отказ (exit 4) до всего остального, если сам апдейтер запущен из OneDrive/Dropbox/…
(`CloudSyncPaths.IsSynced`, общая проверка с `ToolsDirectory` из Agent — бэклог п.41/п.63:
иначе `state.json`/логи сессии синхронизируются в личное облако клиента) → найти hub (`HubUrl`
или `HubDiscovery`, **требуем hub**) → `HttpUpdateClient.GetVersionAsync` → сравнить с локальным
`version.txt` → при
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
- `diag status <СЗ>` — свежий `diag.md` + текущая `Activity` сессии без нового прогона (упавшая
  диагностика видна сразу, а не как «висит», `DiagStatusCommand`).
- `kb record/summary/search …` — локальная ФС через `SzDiag.Kb` (без HTTP).
- `hw import [path] / update / resolve "<PCI ID>"` — локальная БД + `VgaBiosScraper`.
- `hw passport <СЗ>` — паспорт видеокарты (SUBSYS/vBIOS/PCIe/TDR) одной командой через `exec`,
  без файла рецепта рядом с exe (`GpuPassport`).
- `alive <СЗ>` — heartbeat/статус/boot-time из hub + TCP-пробы (22/445/135/3389) + ICMP + `arp -a`
  одной командой, вердикт словами через `AliveVerdict` (`AliveCommand`) — не гадать по шести
  ручным прогонам, вырубилась машина или просто давит нагрузкой sshd.
- `stress stop <СЗ>` — снять ВСЮ нагрузку разом: процессы стресс-тулов (и их подпроцессы —
  `linpack`/`gpu3d-Win64-Shipping` переживают закрытие оболочки), фоновые `exec --detach`-задачи,
  задачи планировщика `szdiag-*`, кроме `lhmmon` (наблюдатель должен жить) (`StressCommand`).
- `app run <СЗ> <имя>` / `app restart <СЗ> <имя>` — GUI-приложение клиента elevated в его
  интерактивной сессии (агент под SYSTEM в session 0 не создаёт окна) — известные приложения в
  `AppCommand.KnownApps` (маска процессов, служба, лаунчер).
- `disk scan <СЗ> [--map|--zone A-B] [--drive N]` — карта скорости чтения по всему накопителю
  точками либо сплошной прогон зоны, фоновой `exec --detach`-задачей (`DiskCommand`).
- `disk snapshot <СЗ> --label <текст>` — карта скоростей + SMART + журнал в один файл вне vault,
  с меткой «до/после» destructive-операции (`DiskSnapshotCommand`).
- `sleep-cycle start|stop <СЗ>` — цикл «сон → RTC-пробуждение» как воспроизводящий тест одной
  командой при живом агенте, останавливается стоп-файлом, есть предохранитель `--max-hours`
  (`SleepCycleCommand`; офлайн-случай — отдельным PE-рецептом).
- `exec <СЗ> ... --param Key=Value` — параметризация рецепта без правки файла в рабочем дереве:
  гасит существующее `$Key = ...` в начале скрипта и подставляет своё значение поверх копии
  текста, которая уезжает агенту (`ExecParams`, п.155); `$Sz` подставляется автоматически, если
  не задан явно.
- `exec <СЗ> ... --as-system` — синхронный запуск скрипта под SYSTEM транзиентной scheduled
  task (`SystemExecRunner`, тот же приём, что у sshd) — часть операций (задачи
  `UpdateOrchestrator`, объекты TrustedInstaller) недоступна даже админу-агенту. Несовместим
  по смыслу с `--detach` (для фонового запуска под SYSTEM — `--isolated`).
- `exec <СЗ> --in-session "<powershell>"` — прогнать скрипт в ИНТЕРАКТИВНОЙ сессии залогиненного
  пользователя, а не в session 0 агента (`InteractiveSessionExec`) — там же, где `app run`,
  для одноразовых команд без регистрации в `AppCommand.KnownApps`.
- `exec <СЗ> --result <jobId> --save` — забрать вывод фоновой задачи (`out.txt`/`err.txt`) на
  хост через `pull` в `pulled\<СЗ>\jobs\<jobId>\`, чтобы результат detached-задачи пережил
  потерю/переустановку клиента (бэклог п.214).
- `sz fetch <СЗ> [--force]` · `sz release` — локальный API учётной системы через `SzDiag.Erp`
  (без hub). Конфиг — секция `Erp` (`BaseUrl`, `TokenFile`, `TimeoutSeconds`=600).
  Артефакты: `kb/СЗ/<номер>/erp.json` + блок под маркерами `erp:початок`/`erp:кінець`
  в `запит.md` + пустые поля frontmatter + заметки заказа и устройства + строка в журнал.
  Коды возврата: `0` успех · `1` прочее · `2` кривой номер или аргументы · `3` сервис не
  поднят либо нет токена · `4` учётная программа не запущена или без логина · `5` захват
  занят (лечится `sz release`) · `6` не найдено либо неоднозначно · `7` интерфейс не
  распознан (обычно поверх висит окно с описанием обновления).

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
- **`PowerShellRunner`: скрипт длиннее ~11 КБ уезжает во временный `.ps1` (`-File`)** —
  `-EncodedCommand` раздувает аргумент в 2,67× и упирается в лимит 32 767 (дважды ловили на
  живых whea, п.101/196). Скрипт с ведущим `param(...)` заворачивается в `& { }` (п.102).
- **Фоновые exec-задачи**: скрипт оператора — отдельный `user.ps1`; parse-ошибка ловится
  обёрткой в `err.txt` и едет в `ExecJobStatus.Error` (п.177). Отмена/список — через канал
  статуса (`Cancel`, `JobId="*"`), потому что только он проходит под полной нагрузкой.
- Секреты/`kb/`/`*.db` — в .gitignore, не коммитить.

## Быстрые команды

```powershell
dotnet build; dotnet test                 # ~174 теста, без хоста/клиента
dotnet test --filter FullyQualifiedName~RevertCoordinator
$env:SZDIAG_LIVE=1; dotnet test           # + live vgabios (ходит на TPU)
.\tools\build-dist.ps1 [-HubIp <LAN-IP>]  # публикация dist\host\ + dist\client\
```

E2e и траблшутинг (в т.ч. token-privilege плана Б, headless-управление по SSH) — [TESTING.md](TESTING.md).
