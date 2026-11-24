# Dev knowledge base (карта функционала sz-diag)

> Плотный справочник по всему функционалу для быстрой навигации и правок без повторного
> обхода кодовой базы. Общий замысел — [vision.md](vision.md); архитектура/инварианты для
> ежедневной работы — [../CLAUDE.md](../CLAUDE.md). Здесь — протокол, точки расширения,
> таблицы параметров и рецепты. Обновлено 2026-11-25 (доступ без своего домена — specs/002).

## Проекты (14 в `src/` + зеркальные тесты в `tests/`)

| Проект | Роль | Ключевые типы |
|---|---|---|
| `SzDiag.Contracts` | DTO + имена протокола (единый источник) + поиск hub + подпись/флот | `HubRoutes`, `HubLocator`, `HubDiscovery`, `EnvelopeSigner`/`EnvelopeVerifier`, `FleetSecrets`/`FleetBundle`, `SeenSeqStore`, DTO-записи |
| `SzDiag.Hub` | ASP.NET Core на хосте: SignalR `/agents` + `/api/*` + `/agent/*` | `AgentHub`, `ManagementApi`, `AgentPackageApi`, `SessionRegistry` |
| `SzDiag.Cli` (`szcli`) | тонкий клиент к `/api` | команды в `Program.cs` |
| `SzDiag.HubClient` | клиент `/api`, общий для CLI и Desk | `HubApiClient`, `IHubApiClient`, `SzLiveness` |
| `SzDiag.Desk` | окно на Avalonia: список СЗ, чат сессии Claude, инспектор, передачи, статусбар | `HubPoller`, `MainViewModel`, `ChatViewModel`, `FeedBuilder`, `DeskClaudeHost` |
| `SzDiag.Claude` | ядро сессий Claude без UI: процесс, парсер stream-json, сессии, разрешения через MCP | `ClaudeSession`, `SessionManager`, `StreamJsonParser`, `PermissionBroker`, `DeskMcpServer` |
| `SzDiag.Agent` | консоль на клиенте (админ, `app.manifest`) | `AgentSession`, `WindowsSystemAccessManager`, `PortableSshServer` |
| `SzDiag.Updater` | точка входа на клиенте: самообновление агента с hub | `HttpUpdateClient`, `PackageApplier`, `AgentLauncher` |
| `SzDiag.ConsoleUi` | консольный UI, общий для hub/агента/CLI | `StickyHeader`, `SyncedConsoleWriter`, `MarkupText` |
| `SzDiag.Hardware` | резолвер видях по PCI ID | `GpuResolver`, `VgaBiosScraper`, `GpuRepository` |
| `SzDiag.Kb` | Obsidian-vault базы знаний | `KbPaths`, `KnowledgeBaseScaffolder`, `ReportMarkdownBuilder` |
| `SzDiag.Erp` | локальный API учётной системы → база знаний | `ErpApiClient`, `ErpSession`, `ErpJson`, `ErpBlockBuilder`, `SzFetchWriter` |
| `SzDiag.Signpost` | сервис указателя на своём Railway: подписанная запись «где hub» + аренда + релиз агента | `LeaseTable`, `PackageStore`, `SignpostKeys` |
| `SzDiag.ZtController` | свой ZeroTier-контроллер на Railway (zerotier-one) + Bearer-прокси к его API с белым списком | `ZtRoutes`, `entrypoint.sh` |

## Протокол (единственный источник имён — `SzDiag.Contracts/HubRoutes.cs`)

### SignalR-хаб `/agents` (`HubRoutes.Path`)

Токен агента — заголовок `X-SzDiag-Token` (`HubRoutes.TokenHeader`), проверяется middleware
на весь путь `/agents` (`Hub/Program.cs`).

**Агент → hub** (server-методы в `AgentHub`, зовутся `InvokeAsync`/`SendAsync` из `SignalRHubLink`):

| Метод (`HubRoutes`) | Сигнатура на hub | Что делает |
|---|---|---|
| `Register` | `Register(RegisterRequest{Sz,Hostname,…,LanIp,AccessHost,AccessMode,SshHostKeyFingerprint})` → `RegisterResponse{SessionSecret}` | `SessionRegistry.Register` + `Kb.EnsureSkeleton` + `store.RecordOpenAsync`; **возвращает секрет сессии** — им headless-откат доказывает владение СЗ |
| `ReportAccess` | `ReportAccess(AccessReportRequest{Sz,AccessHost,AccessMode,SshHostKeyFingerprint,ZtNodeId})` | Чем машина доступна сейчас: `Registry.SetAccess` + запись `known_hosts` по СЗ; `ZtNodeId` (необязательный) запоминается в `SessionInfo.ZtNodeId` — по нему hub удаляет узел из сервисной сети при `close`. Отдельный метод, а не поле heartbeat: SignalR не умеет перегрузки, а имя quick tunnel меняется в течение сессии (после ребута оно **другое**) |
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
| `RunTests` | `sz, filter?, schedule?` | `Program.cs` `OnRunTests` → `TestReportRunner.RunAndUploadAsync` (стресс). `schedule` — профиль расписания OCCT (`default`/`smoke`/`long`/`infinite`, `OcctScheduleProfiles`) — подставляется в `{schedule}` testsuite.json (бэклог п.124/#60), явный профиль ещё и раздвигает таймаут шага под план, а не рубит прогон на середине |
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
| `POST /api/sessions/{sz}/test` (тело `TestRunRequest{Filter,Config,SameConfig,Schedule}`) | `TestRunTrigger.TriggerAsync` + метка конфигурации в SQLite и журнал | `Ok`/`NotFound`/`BadRequest` без метки или на незнакомом `Schedule` (`OcctScheduleProfiles.KnownProfiles`) |
| `GET /api/occt/schedule?profile=` | читает `<Hub.ToolsRoot>/occt/<файл>` (то, что РЕАЛЬНО раздаётся, не `deploy/occt` в репо) → `OcctSchedule.TryParsePeriods` | `OcctSchedulePlan{Periods,TotalFiniteSeconds,HasInfinite}`/`NotFound`/`BadRequest` — `szcli test run` печатает план ДО старта (бэклог п.124/#60, СЗ 161346: раздача 5+5 мин против репо 30+30) |
| `GET /api/sessions/{sz}/test-result` | `TestResultFinder.FindLatestArtifact` (kb reports или `Hub.PullRoot`) → `OcctReportParser.TryParse` (gzip+base64 `scheduleExecutionCompressed` из `occt-report.html`) | `OcctReportSummary{Periods,ElapsedSeconds}`/`NotFound`/`422`; `szcli test result <СЗ>` — errors/wheaErrors/executedDuration по периодам без ручной распаковки |
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

### Передачи push/pull `GET /api/transfers` (`Hub/TransferTracker.cs`)

Под management-токеном, `List<TransferInfo>` (`Id`, `Sz`, `Direction` Push/Pull, `What` —
инструмент или путь, `TotalBytes` (null — неизвестен), `DoneBytes`, `BytesPerSecond` — средняя
с начала, `StartedAt`, `State` Running/Done/Failed, `Note` — итог или причина, `FinishedAt`),
новые сверху; enum'ы числами. Учёт in-memory, завершённые держатся 10 минут
(`TransferTracker.KeepFinished`). `PushCoordinator`/`PullCoordinator` заводят передачу по
`requestId` и **всегда** закрывают её в `finally` (исход по умолчанию — «прервано», чтобы
исключение не оставило `Running` навсегда). Pull считает байты при приёме чанков; push — по
отдаче файлов в `/tools/{tool}/file`: агент передаёт `requestId` параметром `req`
(`ToolRoutes.Manifest/File(tool, …, requestId)`), манифест выставляет `TotalBytes`, файл
отдаётся через `CountingReadStream`. Старый агент без `req` — раздача работает как раньше,
передача закрывается итогом без байт. Повторный push уже доставленного — `Done` с `Note`
«скачано 0, пропущено N». Клиент: `IHubApiClient.GetTransfersAsync` (404 старого hub → пустой
список).

### Диагностика самого hub `/healthz` (`Hub/HealthApi.cs`)

DTO `HealthzResponse` — в `SzDiag.Contracts` (Desk читает его через
`IHubApiClient.GetHealthAsync`; null — hub не ответил).

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
discovery не запускается. Сам по себе broadcast теперь — второй источник `HubLocator` (ниже).

### Поиск hub — `HubLocator` (Contracts, общий для апдейтера и агента)

`LocateAsync` идёт по источникам **по порядку**, первый живой выигрывает:

| # | Источник | `HubLocation.Mode` | Условие |
|---|---|---|---|
| 1 | `HubUrl` из конфига | `config` | `GET /healthz` ответил (`ProbeTimeout` 4 с) |
| 2 | LAN-broadcast (`HubLocator.Broadcast` поверх `HubDiscovery`) | `lan-broadcast` | ответ на UDP 5098 |
| 3 | указатель → `endpoints` записи hub по порядку | `zt` / `lan` / `tunnel` | подпись + `kid` + `seq` сошлись; режим есть в `LocatorOptions.Modes`; адрес отвечает |

`Modes` по умолчанию `[lan, tunnel]`; `zt` добавляет только агент с нодой ZT
(`AgentHubFinder` → `[zt, lan, tunnel]`). zt-адрес обычным сокетом ОС не проверить — для него
есть хук `ztProbe(network, url, ct)` (`ZtHubSession.ProbeAsync`: поднять ноду, войти в сеть из
записи `ztNetwork`, дождаться `/healthz` через неё); Contracts о libzt не знает. Каждый отказ
(не отвечает / таймаут / подпись / чужой `kid` / `seq` старее / режим не поддержан / «ZeroTier в
этом процессе нет») — в `LastRejected` и в лог клиента, **и при неудаче тоже**: иначе на заявке
гадаешь, почему апдейтер «не видит hub». `LocateReleaseAsync` — подписанный релиз агента из
указателя (апдейтер, когда hub не найден). Ответу сервиса клиент **не доверяет**: подпись
перепроверяется на клиенте. Replay: `SeenSeqStore` (файл рядом с exe) помнит последний
принятый `seq` по виду записи — сравнение только с самим собой, часы клиента не участвуют.

### Подписанные конверты (`Contracts/Signing`)

`SignedEnvelope{payload,sig,kid}`: `payload` — base64 **сырых** UTF-8 байт JSON (подпись над ними
как есть, без пересериализации), `sig` — ECDSA P-256/SHA-256 в IEEE P1363 (Ed25519 в .NET 8 нет,
сторонняя криптография в Contracts потянулась бы в клиентские процессы), `kid` — первые 16 hex
SHA-256 от SPKI. `EnvelopeVerifier` различает «подпись не сошлась» (подмена) и «ключ не наш»
(устаревший клиент после ротации). Подписывает `EnvelopeSigner` — только там, где лежит
`secrets\fleet-sign.key` (hub, build-dist). Клиент берёт публичный ключ из `SignPublicKey`
конфига, иначе из `fleet-sign.pub` рядом с exe (`FleetPublicKey.Resolve`) — **только с диска до
обновления**, ключ из проверяемого пакета проверкой не был бы. Нагрузки (`SignpostRecords`):

| `kind` | Поля |
|---|---|
| `hub` (`HubRecord`) | `hubId` (файл `hub-id` рядом с exe hub), `hubName`, `endpoints[{mode,url}]` (порядок = предпочтение), `ztNetwork` (только если есть zt-адрес), `seq` (unix-мс, строго растёт), `issuedAt`, `takeover` |
| `release` (`ReleaseRecord`) | `version`, `buildTime`, `packageSha256`, `packageSize`, `packageUrls` (относительные `/package/{sha}` — от своего указателя), `seq` |

### Указатель `szdiag-signpost` (`SzDiag.Signpost`, свой Railway оператора)

База `https://<свой>.up.railway.app` — задаётся при сборке (`build-dist -SignpostUrl`), в репо не
пишется. Сервис **ничего не подписывает**: проверяет подпись ключом из `SZDIAG_SIGN_PUBKEY`, хранит
и отдаёт конверты. Авторизации на запись нет — её заменяет подпись. Env: `SZDIAG_SIGN_PUBKEY`,
`SZDIAG_FLEET_BUNDLE` (шифротекст, сервисом не читается), `DATA_DIR=/data` (volume),
`RAILWAY_DOCKERFILE_PATH=src/SzDiag.Signpost/Dockerfile`. Без ключа все `PUT` → `503`.

| Метод + путь (`SignpostRoutes`) | Ответ |
|---|---|
| `GET /signpost` | `{hub?, release?, lease?{holderHubId,hubName,expiresAt}}` |
| `PUT /signpost/hub` | `200 lease` · `400` подпись/`kind`/чужой `kid` · `409 lease` держателя · `412` `seq` не вырос |
| `PUT /signpost/release` | `200` · `400` (в т.ч. пакет с этим sha256 не загружен) · `412` |
| `PUT /package/{sha256}` + `X-SzDiag-Release: base64(конверт релиза на этот sha)` | `201` · `401` нет/чужой релиз · `400` sha256 тела ≠ путь; ≤ 100 МБ, хранятся 3 последних + пакет текущего релиза всегда |
| `GET /package/{sha256}` · `GET /healthz` | `application/zip`/`404` · `ok` |

**Аренда** (`LeaseTable`, в памяти — редеплой её теряет, живой hub переопубликуется за 30 с):
`expiresAt = получено + 90 с` по часам **сервиса** (часы hub могут врать). Пусто/истекла — любой
валидный `PUT hub` занимает; живая и тот же `hubId` — продление; другой `hubId` → `409`, с
`takeover=true` — смена держателя. Издатель — `SignpostPublisher` на hub (см. «Хост»).

### Свой ZeroTier-контроллер (`SzDiag.ZtController`, свой Railway оператора)

Образ `zerotier/zerotier` (встроенный контроллер) + ASP.NET-прокси к его API (`127.0.0.1:9993`,
`authtoken.secret` читается на каждый запрос). Входящий UDP не нужен — исходящий до корней
DIRECT. Наружу — только прокси: `Authorization: Bearer <ZT_PROXY_TOKEN>` (короче 32 символов —
прокси не стартует) + белый список `ZtRoutes`: `GET /status|/controller|/controller/network`,
`/controller/network/<nwid>` (создание — `POST …/<10 hex>______`), `GET …/member`,
`GET|POST|DELETE …/member/<10 hex>`; остальное API zerotier-one закрыто. Без токена — `/healthz`.
`GET /admin/identity` — бэкап `identity.secret` во флот: адрес контроллера = префикс ID сети,
потеря volume без бэкапа = новая сеть, и клиенты со старым указателем в неё не войдут;
`entrypoint.sh` восстанавливает identity из `ZT_IDENTITY_SECRET`, если volume пуст. Volume — под
`/var/lib/zerotier-one`. Токен прокси — **только на hub-машинах**, клиенту не уходит (FR-023).

**Сервисная сеть** (`tools\zt-network.ps1`, идемпотентно): публичная (`private=false` — ID лежит в
публичном указателе, авторизовать неизвестного клиента до первого байта нечем), подсеть
`10.88.0.0/24`, пул `.10–.250`, hub-машины статикой `10.88.0.1` (ПК) / `10.88.0.2` (ноут), только
IPv4 (`v4AssignMode.zt`). Flow rules: не-IPv4/не-ARP → drop; ARP → accept; к/от каждого
`HubAddresses/32` → accept; остальное → drop (клиент↔клиент не соединяется). Порты hub правила
**не режут** (stateless-правилам не отличить ответ ssh от нового соединения) — входящее на
hub-машину держат Windows Firewall (профиль адаптера ZT — **Public**) и `LocalAddressFilter`.
Итог скрипта — `secrets\zt.json` `{controllerUrl, controllerToken, identitySecret, network}`
(пишется до join — сеть и identity не теряются, даже если join упал). Коды: 0 · 2 нет URL/токена
· 3 контроллер не отвечает · 4 нет ZeroTier One · 5 адрес hub не назначился · 1 прочее.

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
`CloseOutcome(Closed,Revert:RevertResult?)` (hub → CLI, ответ `close`) ·
`AccessReportRequest(Sz,AccessHost?,AccessMode?,SshHostKeyFingerprint?,ZtNodeId?)` (`SessionInfo`
несёт тот же `ZtNodeId`). `AccessMode` — строки, не enum (ездит по SignalR, лежит в SQLite, от
старых агентов приходит null): `direct` · `tunnel` · `zt` (`AccessHost` = ZT-IP агента;
`target` печатает `ssh svc-diag@<zt-ip>` с пиннингом host-ключа, как для туннеля; адреса нет —
`Unavailable` «адрес в сети ZT неизвестен»). Enum
`SessionStatus{Online,Offline}` (в JSON — число). Enum статусов тестов НЕТ (статус идёт меткой
через `ReportActivity`).

## Агент (`SzDiag.Agent`)

### Режимы запуска (`Program.cs`)

- **Интерактивный**: спросить СЗ → `AccessSpec` → `WindowsSystemAccessManager.Open` (поднять
  доступ) → hub (явный `HubUrl` или discovery) → `AgentSession.StartAsync` (register) → heartbeat-цикл
  → ждать клавиш: `C` откат+выход, `Q` выход без отката. Крестик окна ловит `ConsoleCloseGuard`
  (P/Invoke `SetConsoleCtrlHandler`, `CTRL_CLOSE_EVENT`) → откат.
- **Watchdog** (`--revert <statePath>`): грузит `RevertState`, зовёт `Revert`, выходит. Ни консоли,
  ни SignalR. Запускается scheduled task по таймауту. Hub для отчёта ищет тем же `AgentHubFinder`
  (hub мог переехать); ZT поднимается **одноразовым** storage (`ZtHubSession.ForRevert` →
  `zt\revert-<8 hex>`): при живом агенте его нода с identity СЗ ещё в сети, две ноды с одной
  identity мешали бы друг другу. Бюджет отчёта с ZT — 90 с (без — 8 с); ZT не поднялся — статус
  не уходит, узел добирает `ZtMemberSweeper`.

Поиск hub при старте/`--resume`/`--revert` — `AgentHubFinder.FindAsync` (обёртка над
`HubLocator`). Hub найден по `zt` → HttpClient и SignalR (WebSockets) идут через
`ZtHubTransport` (`SocketsHttpHandler.ConnectCallback` ноды; новый handler на каждое
подключение — SignalR освобождает его вместе с подключением). Нода (`ZtHubSession`)
поднимается лениво, только когда указатель дал zt-адрес, а LAN не нашёл hub раньше, и живёт до
конца процесса: через неё идёт управляющий канал, отчёт об откате и проброс SSH. Storage:
основной агент и `--resume` — `C:\ProgramData\szdiag\zt\<СЗ>` (одна identity на сессию, после
ребута тот же узел и обычно тот же адрес). На старте `ZtStorage.WipeLeftovers(sz)` сносит
storage прошлых сессий (он создаётся при поиске hub — до `Open` и до `state.json`, крах между
ними оставил бы папку, о которой не знает ни один откат), кроме своей СЗ и `revert-*` моложе
10 мин. `ZtSubnetConflict.CheckLocal` (когда hub найден по zt) предупреждает, если LAN/VPN клиента пересекается с `10.88.0.0/24`
(libzt держит свой стек, трафик не ломается, но адреса в выводе двусмысленны). Порт ноды —
случайный 20000–40000 (`InitSetPort`; не 9993 — на машине может стоять свой ZeroTier One).
`libzt.dll` нативная, едет рядом с exe в пакете агента; нет её — агент работает без ZT.

`RevertCoordinator` (`SemaphoreSlim`+флаг) гарантирует откат ровно один раз при любом числе триггеров
(крестик / `C` / hub `Revert`).

### Жизненный цикл доступа (`WindowsSystemAccessManager.Open`, после каждого шага `Persist()`)

| # | Шаг | Флаг `RevertState` |
|---|---|---|
Номера — как в комментариях `Open` (2 в коде пропущен; до шагов — `RevertStaleState` чужой СЗ).

| # | Шаг | Флаг `RevertState` |
|---|---|---|
| 1 | остановить системный `sshd`, если Running (держит порт) | `StoppedSystemSshd` |
| 3 | firewall-правило `szdiag-ssh-{sz}` на `SshPort` | `AddedFirewallRule` |
| 4 | `LocalAccountTokenFilterPolicy=1` (прежнее → `TokenPolicyPreviousValue`) | `SetTokenPolicy` |
| 5 | учётка `svc-diag` (`New-LocalUser` + Administrators по SID) | `CreatedUser` |
| 6 | `PortableSshServer.Start` — sshd **под SYSTEM** (задача `szdiag-sshd-{sz}`) | `GeneratedHostKeys`, `WroteAuthorizedKey`, `CreatedSshdTask` |
| 7 | watchdog scheduled task (`--revert`) под SYSTEM, `Now+WatchdogTimeout` | `CreatedWatchdogTask` |
| 8 | автостарт `-AtStartup` → `agent.exe --resume` | `CreatedAutostartTask` |
| 9 | ярлык «Закрыть доступ» на общем рабочем столе | `CreatedDesktopShortcut` |
| 10 | quick tunnel на sshd (`szdiag-cfd-{sz}`), необязательный | `StartedQuickTunnel`, `DeployedCloudflared` |
| 11 | SSH через сервисную ZT-сеть: `ZtAccess.Start` → `ZtSshForwarder` (ZT:`SshPort` → `127.0.0.1:SshPort`), необязательный; только если нода уже поднята поиском hub | `JoinedZt` + `ZtStoragePath`, `ZtNodeId`, `ZtAddress`, `ZtNetwork` |

`Revert` — обратный порядок, каждый шаг под своим флагом (идемпотентно). **Инвариант:** новый шаг
`Open` ⇒ парная ветка `Revert` под флагом, иначе следы на недоверенной машине. Исключение из
обратного порядка — **наружные двери снимаются первыми**, раньше sshd: `quick tunnel`, сразу за
ним `zt-доступ` (флаг `JoinedZt` **или** непустой `ZtStoragePath`). `zt-доступ`: нода в процессе
есть → `IZtAccess.Stop` снимает проброс, а storage стирается **после остановки ноды** (через неё
ещё уйдёт отчёт об откате на hub); ноды нет (headless) → `ZtStorage.Wipe` сразу, не стёрлось —
шаг падает (`IOException`), файл состояния остаётся. Удалить свой узел на контроллере клиент не
может (токена нет) — это делает hub (`SessionCloser`/`ZtMemberSweeper`). `--resume`: проброс
умирает с процессом, после ребута нода поднимается поиском hub с тем же storage, и
`StartZt` переподнимает проброс. ZT-шаг не поднялся — доступ открыт, работают туннель/прямой
режим. Выбор режима для `ReportAccess` — `AccessReporter.BuildReport`: hub найден broadcast'ом
→ `direct`; есть `ZtAddress` → `zt` (раньше туннеля: P2P без Cloudflare и внешнего процесса);
есть имя туннеля → `tunnel`; иначе `direct`. `ZtNodeId` уходит при любом режиме, если `JoinedZt`.

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
`SshBinDir="ssh"` · `SshWorkDir=C:\ProgramData\szdiag\ssh` · `SignpostUrl=""` (пусто — указатель
не используется) · `SignPublicKey=""` (SPKI PEM; пусто — `fleet-sign.pub` рядом с exe; нет ни
того, ни другого — указателю не верим). Оба пишет build-dist, у `UpdaterOptions` — те же поля.

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

Hub не найден/не отвечает (specs/002) → `UpdateFromSignpostAsync`: `HubLocator.LocateReleaseAsync`
(режимы `[lan, tunnel]` — апдейтеру ZeroTier не нужен) → `SignedReleaseUpdater.ApplyAsync`
(пакет по `packageUrls` относительно `SignpostUrl`, сверка с подписанным sha256; старее
установленного по `buildTime` не ставит) → запуск агента, который уже сам ищет hub (в т.ч. по
zt). Отвергнутые источники печатаются строками `отвергнут <источник>: <причина>`. Релиза нет —
локальный агент, иначе exit 2.

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
  `Hub.ExtraAgentTokens` — токены соседних hub (несколько хостов): везде, где проверяется
  `AgentToken` (`/agents`, `/agent/*`, `/tools`, revert-status, discovery), принимается и любой из
  них (`HubOptions.IsAgentToken`). build-dist берёт их из `secrets\agent-tokens.txt` и переносит из
  прежнего конфига; с переходом на флот это **переходный** список для уже разданных апдейтеров
  (прежний токен машины build-dist добавляет туда сам).
- **Секреты флота** (specs/002-domain-independent-access): один набор на все машины-hub —
  `AgentToken`, `ManagementToken`, ssh-ключ `svc_diag_key`, ключ подписи `fleet-sign.key/.pub`
  (ECDSA P-256), параметры ZT (`ztControllerUrl`, `ztControllerToken`, `ztIdentitySecret` — бэкап
  identity контроллера, `ztNetwork`). Лежат в `secrets\` (`fleet.json` + ключи, `FleetSecretsStore`),
  в Railway — только шифротекстом паролем флота (`FleetBundle`: AES-256-GCM, PBKDF2-SHA256 600k)
  в переменной `SZDIAG_FLEET_BUNDLE` сервиса `szdiag-signpost` (свой Railway оператора); публичный
  ключ — `SZDIAG_SIGN_PUBKEY`. `tools\fleet.ps1 init` (один раз, на машине с текущими секретами:
  берёт её прежние ssh-ключ и `AgentToken`) / `pull` (на остальных; build-dist зовёт сам).
  build-dist **больше не генерирует** ключи и `dev-token`: нет секретов флота → exit 6; приватная
  часть в клиентском dist/пакете → exit 7. `-Token` игнорируется. Клиенту уезжают только
  `fleet-sign.pub` и `SignPublicKey` в `appsettings.json`. CLI: `szcli fleet keygen|encrypt|decrypt`
  (коды 0 · 1 · 2 уже есть · 4 неверный пароль · 5 бандл испорчен). ZT во флот: `tools\zt-network.ps1`
  → `secrets\zt.json` → `fleet.ps1 init` подхватывает сам / `fleet.ps1 push` (= `szcli fleet set-zt
  --from secrets\zt.json` + перешифровать и залить; пустое поле `zt.json` не затирает лежащее).
  `fleet.ps1` коды: 0 · 2 бандл уже есть (`init` без `-Force`) · 3 нет railway-логина/линка/бандла
  · 4 · 5 · 1. Пароль — запросом без эха или `SZDIAG_FLEET_PASSWORD`. build-dist берёт из флота
  `ztNetwork`/`ztControllerUrl`/`ztControllerToken` в `Hub.ZeroTier` (нет сети — hub без ZT),
  `-SignpostUrl` (переносится из прежнего конфига) — в `Hub.Signpost.Url` и в клиентский
  `SignpostUrl`; `libzt.dll` кладёт в пакет агента. `-Publish`: `szcli fleet sign-release
  <package.zip> --version --build-time --url /package/{sha256}` → `PUT /package/{sha}` +
  `PUT /signpost/release`; без `-SignpostUrl` или при отказе — exit 8.
- **`SignpostPublisher`** (`BackgroundService`): раз в `PublishInterval` (30 с; аренда живёт 90 с
  — три пропуска) подписывает `HubRecord` ключом `Signpost.SignKeyPath` и `PUT /signpost/hub`.
  `seq = max(unix-мс, прошлый+1)`. Адреса (`DefaultEndpoints`): сначала zt (`ZtEndpoints` — только
  при `ZeroTier.Enabled`, валидном `Network` и реальном адресе машины в `ZeroTier.Subnet`; ноут без
  ZeroTier One — без zt), затем LAN (частные IPv4 на физических адаптерах со шлюзом; VPN/ZT/
  гипервизоры отсеиваются по имени адаптера), затем `ExtraEndpoints` (руками; режим по умолчанию
  `tunnel`). `ztNetwork` в записи — только вместе с zt-адресом. Домены в публичный указатель
  **не пишутся никогда**. Ответы → `SignpostStates` в `HubStatusTracker` (`/api/status`,
  поле `Signpost`): `published` · `other-holder` (409: новые клиенты идут к держателю, перехват —
  `start-hub.cmd --takeover`, флаг снимается после первого успеха) · `unreachable` ·
  `misconfigured` (нет ключа / 400 чужой ключ / 412 часы ушли назад / 503 нет
  `SZDIAG_SIGN_PUBKEY`) · `off`. В консоль — только смена состояния. Указатель недоступен — hub
  работает как раньше.
- **`LocalAddressFilter`** — первым в конвейере, решает по `Connection.LocalIpAddress` (адрес,
  **на который** пришёл запрос; bind на конкретные IP не годится — у ноута LAN по DHCP; hub
  слушает `0.0.0.0`): адрес в `Hub.DenySubnets` → `Abort()` без ответа (личная ZT-сеть владельца
  hub не видит вовсе); адрес в `Hub.ZeroTier.Subnet` и путь `/api*` → `403` (сервисная сеть
  публичная — management закрыт независимо от токена и даже при `ZeroTier.Enabled=false`);
  остальное (в т.ч. `/agents`, `/agent/*`, `/tools` из сервисной сети) — дальше. IPv4-mapped
  IPv6 приводится к IPv4; `LocalIpAddress=null` (TestServer) — пропуск. Неразобранная подсеть —
  предупреждение на старте, а не молча выключенный фильтр.
- **ZT на hub**: `ZtControllerClient` (`IZtControllerClient`) — Bearer-прокси контроллера,
  `controller/network/{Network}/member[/{id}]`: список, член (`creationTime`, `ipAssignments`),
  удаление (404 = уже нет). `SessionCloser.CloseAsync` после `Remove(sz)` удаляет узел
  `ZtNodeId` (таймаут, ошибки глотаются — доберёт сторож). `ZtMemberSweeper` (раз в минуту, при
  `ControllerConfigured`): член без активной СЗ, старше `SweepAfter`, не hub (`HubAddresses`) —
  удаляется; нет `creationTime` — срок от первого раза, когда увидели. Причина: сеть публичная,
  а headless-откат клиента свой узел удалить не может.

  | Опция `Hub.*` | По умолчанию | Смысл |
  |---|---|---|
  | `DenySubnets` | `[<личная ZT-подсеть /24>]` | hub на эти локальные адреса не отвечает (`Abort`) |
  | `ZeroTier.Enabled` | `false` | сторож членов + zt-адрес в указателе |
  | `ZeroTier.Network` | `""` | id сервисной сети, 16 hex (из флота) |
  | `ZeroTier.Subnet` | `10.88.0.0/24` | клиенты оттуда ходят к hub, но не в `/api` (403) |
  | `ZeroTier.SweepAfter` | `00:10:00` | возраст «бесхозного» члена до удаления |
  | `ZeroTier.ControllerUrl` / `ControllerToken` | `""` | прокси своего контроллера; токен — только hub (FR-023) |
  | `ZeroTier.HubAddresses` | `[10.88.0.1, 10.88.0.2]` | узлы hub-машин — сторож не трогает |
  | `Signpost.Enabled` / `Url` | `false` / `""` | публикация в указатель (`https://<свой>.up.railway.app`) |
  | `Signpost.PublishInterval` | `00:00:30` | период продления аренды |
  | `Signpost.HubName` | `""` → имя машины | для людей («ПК», «ноут») |
  | `Signpost.Takeover` | `false` | перехват живой аренды (`--takeover` выставляет сам) |
  | `Signpost.SignKeyPath` | `""` | `secrets\fleet-sign.key` (относительный — от exe) |
  | `Signpost.ExtraEndpoints` | `[]` | `{Mode,Url}` сверх найденных; build-dist пустой |
- **`HubTunnelService`** (`BackgroundService`) — именованный Cloudflare Tunnel, публикующий
  hub наружу (`hub.<домен>`). Поднимает `cloudflared` дочерним процессом на старте, снимает
  при остановке, перезапускает через `RestartDelay`, если тот упал сам. Секция
  `Hub.Tunnel`: `Enabled` (рубильник), `Name` (имя/UUID туннеля), `ConfigPath` (yml с ingress),
  `ExecutablePath` (пусто — ищется в PATH и `Program Files (x86)\cloudflared`), `PidFile`.
  Служба Windows/автозапуск по входу **не используются намеренно**: туннель без hub отдаёт
  502, а поднятый hub без туннеля недоступен снаружи — 16.09.2026 так и было (hub слушал,
  `hub.<домен>` отвечал 530/1033, потому что `cloudflared` никто не запустил).
  Два страховочных пути от осиротевшего процесса: job object с `KILL_ON_JOB_CLOSE`
  (`KillOnCloseJob`) снимает `cloudflared` даже при `Stop-Process -Force` по hub, а pid-файл
  рядом с exe даёт следующему старту добить того, кто всё же выжил. `build-dist.ps1
  -TunnelName/-TunnelConfig/-TunnelExe` пишет секцию; без параметров она **переносится из
  существующего** `dist\host\hub\appsettings.json`, чтобы пересборка не разоружила хаб молча.

## CLI (`szcli`, `SzDiag.Cli`)

Диспетчер `switch` по `args[0]`, по умолчанию `watch`. Конфиг env-префикс `SZDIAG_`
(`HubBaseUrl=http://localhost:5000`, `ManagementToken`, `KbRoot=kb`, `GpuDbPath`, `PciIdsPath`).

- `watch` (дефолт) — Spectre `Live`, каждые 1000 мс `GET /api/sessions`, таблица СЗ/Статус/IP/Хост/Активность.
- `list` · `close <СЗ>` · `target <СЗ>` · `test run <СЗ> [фильтр] [--schedule default|smoke|long|infinite]` ·
  `test result <СЗ>` · `diag run <СЗ> [секции]` — к соответствующим `/api`.
- `diag status <СЗ>` — свежий `diag.md` + текущая `Activity` сессии без нового прогона (упавшая
  диагностика видна сразу, а не как «висит», `DiagStatusCommand`).
- `kb record/summary/search …` — локальная ФС через `SzDiag.Kb` (без HTTP).
- `fleet keygen [--agent-token T] [--management-token T] [--zt-from <zt.json>] [--force]` ·
  `fleet set-zt --from <zt.json>` · `fleet encrypt` (бандл в stdout) · `fleet decrypt [--in <файл>]`
  (иначе из `SZDIAG_FLEET_BUNDLE`) · `fleet sign-release <package.zip> --version <v> --build-time
  <ISO> --url <адрес> [--url …]` (`{sha256}` в адресе подставится; конверт релиза в stdout); у всех
  `--secrets <папка>` (по умолчанию `secrets`), без hub (`FleetCommand`). Обычно зовутся из
  `tools\fleet.ps1` и `build-dist -Publish`.
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
- `sz get <СЗ> [--json]` · `sz orders <СЗ|телефон> [--limit N] [--json]` ·
  `sz call <sz.get|api.*> [ключ=значение ...]` — REST-путь TeleAuto v1.6.0+ (`ErpRest`): бэкенд Telemart
  напрямую, без окон, захвата и `ErpSession`, таймаут 120 с. `sz get` пишет сырой ответ в
  `kb/СЗ/<номер>/erp-rest.json` (не `erp.json`: у `sz fetch` там другая форма) и больше ничего
  в kb не трогает. `sz call` пускает только `sz.get` и `api.*` — UI-инструменты кликают по экрану;
  аргументы — `ключ=значение` (json из PowerShell через `.cmd` теряет кавычки, бэклог п.269).
  Коды сверх `sz fetch`: `8` TeleAuto без PIN (`locked`) · `9` нет VPN (`unreachable`) ·
  `10` токен не выдан (`auth_failed`). Поля и ветки состава — `docs/teleauto-rest-api.md`.

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

## Сессии Claude в Desk (`SzDiag.Claude` + `SzDiag.Desk`)

**Запуск процесса** (`ClaudeLaunch`): `claude -p --input-format stream-json --output-format
stream-json --verbose --permission-mode <auto|default> --permission-prompt-tool mcp__desk__permission_prompt
--mcp-config run\<ключ>.mcp.json --append-system-prompt <SzBriefing.For(сз)> [--resume <id>]`,
рабочий каталог — корень репо, stdin UTF-8 без BOM, маркеры родительской сессии
(`ClaudeLaunch.InheritedSessionMarkers`) вычищаются. Режим — `PermissionMode` конфига Desk
(`auto` по умолчанию). **Профиль** — `ClaudeProfiles.Discover(домашняя папка)`: каталоги `.claude*`
с `.credentials.json` (`claude` = `~/.claude` без `CLAUDE_CONFIG_DIR`, `claude2` = `~/.claude2`);
выбирается кнопкой при «Начать сессию», пишется в `SessionRecord.Profile` и закреплён за разговором
(`DeskClaudeHost.ProfileFor`: пропавший с машины профиль — запуск невозможен, а не подмена). Процесс
стартует при **первом сообщении**, не при открытии чата. Схема событий — итоги спайка
(`docs/superpowers/specs/2026-09-25-desk-spike-notes.md`), фикстуры — `tests/SzDiag.Claude.Tests/Fixtures`.

**Файлы рядом с exe Desk:** `desk-sessions.json` (реестр `ключ → session_id, CreatedAt, Archived, Profile`),
`desk-tokens.json` (токены и стоимость за локальные сутки), `sessions\<ключ>.jsonl` (журнал: сырой
stream-json без служебных строк + строки `desk_user/desk_note/desk_permission_asked|answered/desk_crash`;
лента после перезапуска Desk строится только из него), `run\<ключ>.mcp.json` (порт и токен MCP —
переписываются на каждый запуск процесса), `run\resume-<ключ>.cmd` («открыть в терминале»).

**Состояния** (`SessionState`): `Stopped` → первое сообщение → `Working` → `result` → `Idle`
(следующее из очереди — снова `Working`); запрос разрешения — `WaitingPermission`; неожиданный
выход процесса — `Crashed` (карточка с хвостом stderr, «перезапустить» шлёт очередь); `Archived` —
СЗ пропала из `/api/sessions` (только переход «была → пропала», только после удачного опроса
списка СЗ, только если её нет дольше `MainViewModel.ArchiveAfter` = 3 мин — рестарт hub на
время опустошает список — и не посреди хода), новое сообщение снимает архив и продолжает
`--resume`. «■» во время хода: то, что отправлено после «■», уходит сразу после прерванного хода. «■» — control-запрос
`interrupt`; нет `result` за 10 с — процесс останавливается, очередь возвращается в поле ввода.

**Разрешения:** `DeskMcpServer` (127.0.0.1, случайный порт, `X-Desk-Token` на запуск, stateless)
держит запрос `permission_prompt` открытым, пока человек не ответит; ключ сессии — из пути
`/mcp/<ключ>`. `timeout` сервера в конфиге — сутки (без него `claude` рвёт тулзу через 300 с).
Остановка сессии/Desk и «■» отвечают на висящие запросы отказом.

**Обмен между сессиями (`PeerExchange`, часть 4).** Инструменты MCP-сервера `desk`:

| Инструмент | Аргументы | Что делает | Ошибки (`IsError`) |
|---|---|---|---|
| `permission_prompt` | `tool_name`, `input`, `tool_use_id` | ждёт решения оператора | — |
| `peers` | — | другие живые СЗ с сессией Desk: `<СЗ> · железо · статус · сессия <State> · похоже: …` | — |
| `ask_peer` | `key`, `question`, `live` = false | без `live` — выжимка kb (`діагностика.md` хвост 3000 симв. + 30 строк `журнал.md`); `live` — вопрос в сессию соседа, ответ = финальный текст её хода | своя СЗ; в kb пусто; нет активной сессии; глубина 1 (сам отвечаешь соседу); встречный вопрос; уже есть вопрос без ответа; лимит `PeerLivePerHour` (20/ч); таймаут `PeerTimeoutMinutes` (5 мин); сосед прервал ход/упал/остановлен |

`peers`/`ask_peer` передаются в `--allowedTools` (`ClaudeLaunch.PeerTools`) — без карточки
разрешения. Живой вопрос встаёт в отдельную очередь соседа **впереди** очереди оператора; сосед
переходит в `SessionState.AnsweringPeer`, в его ленте — `от <СЗ>: …` с иконкой чата (строка журнала
`desk_peer_question`). Отмена/таймаут снимает неотправленный вопрос. Пары в полёте —
`PeerExchange.Active` (иконка чата с номером соседа в списке СЗ).

Данные о соседях — `SzPeerDirectory` (Desk): живые СЗ из `HwProfileCache.Live`, у которых есть
неархивная запись сессии; выжимка — только для ключа `^\d{6}$`. Профиль железа — `HwProfile.Script`
синхронным exec раз на `BootTime`, по одной СЗ за опрос, неудача — повтор через 5 мин;
похожесть — `HwProfile.Similarity` (CPU / плата / память целым профилем «планки × объём @ частота»).

**Рецепт: две одинаковые сборки** (как 161432). Обе СЗ в Desk с сессиями → на карточках `≈`.
В сессии A: «спроси соседа, что он нашёл» (`ask_peer` без live — выжимка), при нужде — «спроси
живьём, на каком профиле памяти его прогон прошёл чисто». Сравнение идёт через соседа, а не
через `szcli … <чужая СЗ>`: сессия привязана к своей машине.

### Тема Desk — дизайн-система Shirabe (`Theme/`)

Вид Desk — язык Shirabe (артефакт https://claude.ai/artifact/TYJjNu1apzCZjcvczfoPxy, спека
`specs/001-desk-shirabe-design/`): рама `Bg.Base`, острова `Border.island` (заявки 268 · чат ·
инспектор 308), тулбар 40, статус-бар 24, один акцент `#2f6ae0`.

| Файл | Что | Правило |
|---|---|---|
| `Theme/Tokens.axaml` | токены `Bg.* Border.* Text.* Accent.* Status.* Space.* Radius.* Size.* Font.*` + перекрытие ресурсов Fluent | **цвет — только отсюда**: литерал `#rrggbb` в `Views/**` роняет `ThemeTests` |
| `Theme/Typography.axaml` | классы `heading title body body-medium small caption code code-small secondary section accent error warning` | размеры только 11–16, цифры `tnum` |
| `Theme/Icons.axaml` | `Icon.*` — пути 16×16 набора Shirabe | рисовать `<Path Classes="icon" Data="{StaticResource Icon.X}"/>`; эмодзи в Views запрещены (`ThemeTests`) |
| `Theme/Controls.axaml` | стили компонентов: `Button.primary/ghost/danger/stop/icon/sm/row/caption`, `Border.card/code/badge/island`, вкладки, поля, списки | состояние — словом и формой, не только цветом |
| `Views/StatusDot.cs` | знак статуса: заливка / кольцо / ромб / треугольник / контур (`DotKind`) | статусы СЗ/сессии — через `Converters.*ToDot`; пульсирует только `Live` |

Шрифты: Inter — пакет `Avalonia.Fonts.Inter` (`WithInterFont()` в `Program` и `TestApp`), JetBrains
Mono — `Assets/Fonts` (OFL рядом). Тесты Desk рисуют настоящим Skia (`UseHeadlessDrawing = false`):
заглушка шрифтов headless не подбирает Inter Medium/SemiBold и роняет окна.

### Инспектор Desk (`ViewModels/Inspector`)

Вкладки — `IInspectorTab` (`Interval`, `RefreshOnReboot`, `RefreshAsync`, `Clear`);
`InspectorViewModel` обновляет **только видимую**: при открытии, по интервалу (тик раз в секунду
из окна) и вкладку вырубонов — при росте ⚡N выбранной СЗ. Порядок: Обзор (0, без опроса),
Вырубоны (`/api/sessions/{sz}/reboots`, при открытии), Задачи (`ExecJobsAsync` + хвост выбранной
`ExecStatusAsync`, 3 с), Сенсоры (синхронный exec хвоста `SensorPaths.LhmCsv`, 5 с; «не пишутся» —
строка не менялась 30 с по часам бокса), Журнал (`KbPaths.Journal` с диска, `FileSystemWatcher`),
Железо (`szcli hw passport`, кэш на СЗ), Действия. Таймаут клиента hub (30 с) приходит как
`TaskCanceledException` — вкладки показывают «агент не ответил», прежние данные не стираются;
инспектор глотает отмену только при смене СЗ.

**`SzcliRunner`** — `cli\SzDiag.Cli.exe` рядом с найденным `szcli.cmd` (`DeskClaudeHost.Szcli`),
`NO_COLOR=1`, UTF-8, ANSI вычищается; коды `-1` — szcli не найден (собери dist), `-2` — прервано.
**`FreezeProbe`** — `cli\freeze\<СЗ>.json` рядом с szcli, тот же признак, что у `szcli list`.
Действия (`ActionsViewModel`): `diag run`, `test run … --config` (метка обязательна), `freeze`,
`unfreeze` (подтверждение), `note`, `sz fetch`, `close` / `close --force "причина"` (подтверждение);
пока идёт одна команда, остальные не запускаются. `CliXml` и `SensorPaths` — в HubClient/Contracts,
общие с CLI.

**`GET /api/status`** (`HubStatus`, под management-токеном): `AgentPackageVersion` — из
`agent-dist/version.txt`; `KbBackup` (`Enabled`, `LastRunAt`, `Outcome` строкой, `Message`) и
`Tunnel` (`State` из `TunnelStates`: off / not-found / running / restarting, `Since`) — в
`HubStatusTracker` отчитываются `KbBackupService` и `HubTunnelService`. Старый hub (404) —
статусбар без деталей.

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

**Флот и сервисная сеть с нуля** (specs/002, `quickstart.md`): Railway — сервисы `szdiag-signpost`
(env `SZDIAG_SIGN_PUBKEY`, volume `/data`) и `szdiag-zt-controller` (`ZT_PROXY_TOKEN` ≥ 32 симв.,
volume `/var/lib/zerotier-one`) → `tools\zt-network.ps1 -ControllerUrl https://<свой>.up.railway.app`
на ПК (`10.88.0.1`; ноут — `-HubAddress 10.88.0.2`, URL/токен из флота) → `toolsleet.ps1 init`
(или `push`, если флот уже есть) → `build-dist.ps1 -SignpostUrl https://<свой>.up.railway.app
[-Publish]` → `start-hub.cmd`. На второй hub-машине: `fleet.ps1 pull` (build-dist зовёт сам) →
`zt-network.ps1 -HubAddress 10.88.0.2` → build-dist. Профиль адаптера ZT на hub — Public.
**Новый режим адреса hub** (по образцу `zt`): константа в `HubEndpointMode` → публикация в
`SignpostPublisher.DefaultEndpoints` → ветка в `HubLocator.LocateAsync` (если обычный сокет ОС
до адреса не дотянется — хук по образцу `ztProbe`) → режим в `LocatorOptions.Modes` того процесса,
который его умеет.
**Новое поле в записи указателя**: `SignpostRecords` (+ `SignpostJson`); старые клиенты должны
пережить лишнее поле — подпись идёт над сырыми байтами, пересериализации нет.

**Возврат результата агент→hub**: `link.UploadReportFileAsync(UploadReportPart)` (файлы) /
`link.ReportActivityAsync(sz,label,since)` (статус). Новый вид — новый метод по образцу
`UploadReportFile` (константа + `IHubLink`/`SignalRHubLink` + приём в `AgentHub` + `HubRoutes`).

## Инварианты и подводные камни

- **Откат без следов**: каждый шаг `Open` ⇒ флаг + парная ветка `Revert`. Забыл → следы на клиенте.
- **Токены разные**: агентский `X-SzDiag-Token` ≠ управляющий `X-SzDiag-Mgmt-Token`.
- **Секрет сессии** `X-SzDiag-Session` — третий заголовок, не токен: выдаётся hub на `Register`
  под конкретную СЗ, живёт в `state.json` агента и предъявляется на `/agent/revert-status`.
  Нужен потому, что агентский токен общий на весь флот, а за Cloudflare Tunnel у всех
  агентов ещё и одинаковый `RemoteIpAddress` — прежняя сверка по IP там не различает машины.
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
- **`ZeroTier.Sockets` 1.8.4: их `Send`/`Receive` и `NetworkStream` не использовать.** `Send(buf,
  offset, size)`/`Receive` передают в нативный вызов длину **всего массива** (`Buffer.ByteLength`)
  вместо `size` — мусорный хвост в поток / запись за границу при `offset>0`; адрес берётся
  `UnsafeAddrOfPinnedArrayElement` **без закрепления** на время блокирующего вызова (GC сдвинет
  массив — порча памяти); базовый `Stream.ReadAsync/WriteAsync` сериализует чтение и запись одним
  семафором — full-duplex WebSocket виснет. Обход — `Zt/ZtStream.cs` + `LibztSocketIo.cs`: свой
  P/Invoke `CSharp_zts_bsd_send/recv` с `fixed` и точной длиной, дескриптор — из приватного `_fd`
  их `Socket` reflection'ом (Connect/Bind/Listen/Accept у них корректны); чтение — один
  выделенный поток на соединение в `Pipe` (поток на каждый `ReadAsync` под SignalR = голодание пула
  под OCCT, п.50), запись — независимо. В `dotnet test` libzt не поднимается — всё за
  `IZtNode`/`IZtSocketIo`-фейками. Шум `Socket error: recv, 10093` после `Stop()` — норма.
- **Публичный указатель — только адреса, которые hub нашёл сам** (LAN, zt) + `ExtraEndpoints`;
  реальные URL Railway, домены и id сетей в репо/доках не пишем — плейсхолдеры
  `https://<свой>.up.railway.app`.
- **Токен ZT-контроллера и приватный ключ подписи на клиента не заезжают**: build-dist падает
  (exit 7), если в клиентский dist/пакет попал `fleet-sign.key`/`svc_diag_key`/`fleet.json`/
  `agent-tokens.txt`. Клиенту — только
  `fleet-sign.pub`/`SignPublicKey` и агентский токен.
- Секреты/`kb/`/`*.db` — в .gitignore, не коммитить.

## Быстрые команды

```powershell
dotnet build; dotnet test                 # ~174 теста, без хоста/клиента
dotnet test --filter FullyQualifiedName~RevertCoordinator
$env:SZDIAG_LIVE=1; dotnet test           # + live vgabios (ходит на TPU)
.\tools\build-dist.ps1 [-HubIp <LAN-IP>]  # публикация dist\host\ + dist\client\
```

E2e и траблшутинг (в т.ч. token-privilege плана Б, headless-управление по SSH) — [TESTING.md](TESTING.md).
