# Tasks: Доступ без своего домена — ZeroTier, подписанный указатель hub и ключи флота

**Input**: `specs/002-domain-independent-access/` — plan.md, spec.md, research.md, data-model.md,
contracts/signpost-api.md, quickstart.md
**Tests**: включены — в проекте каждое изменение протокола/жизненного цикла покрыто xUnit
(`dotnet test`, ~1380 тестов); live-проверки — `quickstart.md`.
**Порядок историй**: US2 идёт раньше US1 — ключ подписи флота нужен указателю (plan.md, фазы поставки).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: можно параллельно (разные файлы, нет зависимостей от незавершённых задач)
- Все `.ps1` — UTF-8 **с BOM**; комментарии и вывод — на русском

---

## Phase 1: Setup

- [X] T001 Создать проект `src/SzDiag.Signpost/SzDiag.Signpost.csproj` (ASP.NET Core minimal API, `net8.0`, ссылка на `SzDiag.Contracts`) и добавить в `SzDiag.sln`
- [X] T002 [P] Создать тест-проекты `tests/SzDiag.Signpost.Tests/` и `tests/SzDiag.Contracts.Tests/` (xUnit, как соседние `tests/*`), добавить в `SzDiag.sln`
- [X] T003 [P] Добавить `src/SzDiag.Signpost/Dockerfile` (multi-stage `mcr.microsoft.com/dotnet/sdk:8.0` → `aspnet:8.0`, `ASPNETCORE_URLS=http://0.0.0.0:${PORT}`, контекст сборки — корень репо) и `src/SzDiag.Signpost/railway.json` (`builder: DOCKERFILE`, `dockerfilePath`, healthcheck `/healthz`) — *сделано: `railway.json` положен в корень репо (контекст сборки — корень), плюс `.dockerignore`*

---

## Phase 2: Foundational (блокирует US1, US2, US4)

- [X] T004 [P] Тесты конверта в `tests/SzDiag.Contracts.Tests/Signing/EnvelopeTests.cs`: подпись→проверка ok; испорченный `sig` → отказ; изменённый 1 байт `payload` → отказ; неизвестный `kid` → отказ; `kid` = «первые 16 hex SHA-256 от SPKI публичного ключа»
- [X] T005 [P] `src/SzDiag.Contracts/Signing/SignedEnvelope.cs` — record `{ payload: base64, sig: base64, kid: string }`; подпись над **сырыми UTF-8 байтами** `payload`, без пересериализации
- [X] T006 `src/SzDiag.Contracts/Signing/EnvelopeSigner.cs` и `EnvelopeVerifier.cs` — ECDSA P-256/SHA-256, формат подписи IEEE P1363 (`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`), загрузка ключей из PEM (PKCS#8 приватный, SPKI публичный), `Verify` возвращает причину отказа (`BadSignature`/`UnknownKid`/`Malformed`)
- [X] T007 [P] `src/SzDiag.Contracts/Signpost/HubRecord.cs`, `ReleaseRecord.cs`, `SignpostRoutes.cs` — поля строго по data-model.md (`kind`, `hubId`, `hubName`, `endpoints[{mode,url}]` с `mode ∈ zt|lan|tunnel`, `ztNetwork` 16 hex, `seq` long «unix-мс издателя, строго растёт», `issuedAt`, `takeover`; релиз: `version`, `buildTime`, `packageSha256` hex, `packageSize`, `packageUrls[]`, `seq`); маршруты `/signpost`, `/signpost/hub`, `/signpost/release`, `/package/{sha256}`, `/healthz`
- [X] T008 Запустить `dotnet build` и `dotnet test tests/SzDiag.Contracts.Tests` — зелёные

**Checkpoint**: конверты подписываются и проверяются — можно делать ключи флота и указатель.

---

## Phase 3: User Story 2 — апдейтер из любой сборки принимается любым hub (P1) 🎯 MVP

**Goal**: один набор секретов флота на все машины; `build-dist` его забирает, а не генерирует.
**Independent Test**: quickstart.md, «Фаза 1» — dist с ноута работает с hub на ПК и наоборот.

### Tests

- [X] T009 [P] [US2] Тесты бандла в `tests/SzDiag.Contracts.Tests/Fleet/FleetBundleTests.cs`: шифрование→расшифровка паролем ok; неверный пароль → `FleetBundleException(WrongPassword)`; испорченный `tag` → отказ; формат обёртки `{ v:1, kdf:"pbkdf2-sha256", iter:600000, salt, nonce, ct, tag }`
- [X] T010 [P] [US2] Дописать `tests/SzDiag.Hub.Tests/HubOptionsAgentTokenTests.cs`: токен флота + переходные `ExtraAgentTokens` принимаются, пустой — никогда — *покрыто существующими тестами в рабочей копии*

### Implementation

- [X] T011 [US2] `src/SzDiag.Contracts/Fleet/FleetSecrets.cs` (поля data-model.md: `formatVersion`=1, `signPrivateKey`, `signPublicKey`, `agentToken` и `managementToken` — «32 байта base64url», `svcDiagKey`/`svcDiagKeyPub`, `ztApiToken`, `ztNetwork`, `createdAt`) и `FleetBundle.cs` — AES-256-GCM, ключ PBKDF2-SHA256 600 000 итераций, соль 16 байт, nonce 12 байт
- [X] T012 [US2] Команды `szcli fleet encrypt|decrypt|sign-release` в `src/SzDiag.Cli/FleetCommands.cs` + регистрация в `src/SzDiag.Cli/Program.cs`: `encrypt` читает `secrets\` → stdout обёртка; `decrypt` stdin → раскладка в `secrets\` (`fleet-sign.key`, `fleet-sign.pub`, `svc_diag_key`, `svc_diag_key.pub`, `fleet.json`); пароль — из `SZDIAG_FLEET_PASSWORD` или запросом без эха; коды 0 / 4 неверный пароль — *сделано без `sign-release`: перенесено в T028, где известны параметры релиза; добавлен `keygen`, бандл в `decrypt` — через `--in` или `SZDIAG_FLEET_BUNDLE` (stdin занят запросом пароля)*
- [X] T013 [US2] `tools/fleet.ps1` (UTF-8 BOM) — `init`: проверка `railway whoami` (нет → код 3), проверка существующей `SZDIAG_FLEET_BUNDLE` через `railway variables --service szdiag-signpost --json` (есть и нет `-Force` → код 2), генерация ключа подписи (`szcli fleet` или .NET), токенов, `ssh-keygen -t ed25519` (через `cmd /c`, как в build-dist), запрос `ztApiToken`/`ztNetwork` (можно пустыми — заполняются в US3), миграция: текущий `AgentToken` из `dist\host\hub\appsettings.json` дописывается в `secrets\agent-tokens.txt`; `railway variables --service szdiag-signpost --set SZDIAG_FLEET_BUNDLE=… --set SZDIAG_SIGN_PUBKEY=… --skip-deploys`. `pull`: читает переменную → `szcli fleet decrypt`
- [X] T014 [US2] `tools/build-dist.ps1`: перед генерацией ключа (стр. ~110) — если нет `secrets\fleet.json`, вызвать `tools\fleet.ps1 pull`; если и так нет — стоп с кодом 6 и подсказкой `fleet.ps1 init|pull` (никакой молчаливой генерации `svc_diag_key` и `dev-token`); `$Token` и `ManagementToken` брать из `secrets\fleet.json`; `ExtraAgentTokens` — по-прежнему из `secrets\agent-tokens.txt` + прежнего конфига (переход, FR-015)
- [X] T015 [US2] `tools/build-dist.ps1`: класть `fleet-sign.pub` в `dist\client\` и в пакет агента; публичный ключ подписи зашивается в `appsettings.json` апдейтера/агента полем `SignPublicKey` — **приватный ключ в dist не попадает никогда** (FR-014: проверить листингом `dist\client\` в конце скрипта, найден `fleet-sign.key` → ошибка)
- [X] T016 [US2] Обновить `docs/dev-knowledge-base.md` (раздел о секретах/build-dist) и `docs/TESTING.md`: `fleet.ps1 init|pull`, пароль флота, коды 2/3/4/6

**Checkpoint**: US2 проверяется quickstart «Фаза 1» целиком.

---

## Phase 4: User Story 1 — апдейтер находит активный hub без своего домена (P1)

**Goal**: подписанный указатель на Railway + общий резолвер (зеркало исключено 2026-11-24).
**Independent Test**: quickstart.md, «Фаза 2», шаги 1–2 и 4–7 (на существующих адресах LAN/CF).

### Tests

- [X] T017 [P] [US1] `tests/SzDiag.Signpost.Tests/SignpostApiTests.cs` (WebApplicationFactory): `PUT /signpost/hub` с валидным конвертом → 200 и виден в `GET /signpost`; плохая подпись → 400; `seq` не растёт → 412; `PUT /package/{sha}` с несовпадающим sha256 тела → 400; `PUT /signpost/release` без загруженного пакета → 400; хранятся последние 3 пакета
- [X] T018 [P] [US1] `tests/SzDiag.Contracts.Tests/HubLocatorTests.cs` с фейковыми источниками: порядок `config → LAN → signpost`; отвергнутый источник попадает в `Rejected` с причиной (таймаут/подпись/seq/kid); `seq` меньше сохранённого в `SeenSeqStore` → отвергнут; релиз с `buildTime` старше установленного → не ставится
- [X] T019 [P] [US1] `tests/SzDiag.Updater.Tests/ReleaseFlowTests.cs`: пакет с sha256, не совпадающим с подписанным релизом, не применяется; все источники упали → запуск локального агента (код как сейчас)

### Implementation — сервис указателя

- [X] T020 [US1] `src/SzDiag.Signpost/Program.cs`: эндпоинты по contracts/signpost-api.md; публичный ключ из env `SZDIAG_SIGN_PUBKEY`; подпись проверяется **до** любых изменений; `SZDIAG_FLEET_BUNDLE` сервисом не читается
- [X] T021 [US1] `src/SzDiag.Signpost/PackageStore.cs` — пакеты в `${DATA_DIR}/packages/<sha256>.zip` (≤ 200 МБ, стриминг на диск со сверкой sha256), последний релиз — `${DATA_DIR}/release.json`; хранить последние 3 пакета
- [X] T022 [US1] Деплой: `railway volume add --service szdiag-signpost --mount-path /data`, переменная `DATA_DIR=/data`, `railway up --service szdiag-signpost` из корня репо; проверить `GET <свой указатель>/healthz` → `ok` — *сделано: volume `/data`, `DATA_DIR`, `RAILWAY_DOCKERFILE_PATH=src/SzDiag.Signpost/Dockerfile` (корневой `railway.json` Railway не подхватил); `/healthz` → ok, запись → 503 до `fleet.ps1 init`*

### Implementation — резолвер и клиенты

- [X] T023 [US1] `src/SzDiag.Contracts/SeenSeqStore.cs` — `signpost-seen.json` рядом с exe (`AppContext.BaseDirectory`), последний принятый `seq` по `kind`; часы клиента не используются
- [X] T024 [US1] `src/SzDiag.Contracts/HubLocator.cs` — `LocateAsync` / `LocateReleaseAsync` по contracts (источники `config HubUrl → HubDiscovery LAN → signpost`), `HubLocation { Url, Mode, Source, Rejected }`; источник указателя — HTTP GET с таймаутом 10 с, проверка `EnvelopeVerifier`
- [X] T025 [US1] `src/SzDiag.Updater/UpdaterOptions.cs` (+ `SignpostUrl`, `SignPublicKey`) и `src/SzDiag.Updater/Program.cs`: вместо `HubDiscovery.FindHubAsync` (стр. 62–64) — `HubLocator.LocateReleaseAsync`; пакет качается по `packageUrls` релиза или с LAN-hub, сверяется с `packageSha256` **подписанного** релиза; в лог — какой источник сработал и почему отвергнуты остальные; `build-info.json` (`buildTime`) из пакета — локальный порядок релизов. **Ключ проверки — только с диска** (`SignPublicKey` из `appsettings.json` или уже лежащий `fleet-sign.pub`), никогда из проверяемого пакета; `PackageApplier` не трогает `appsettings.json`, а `Updater.exe` в пакет не входит — US1 требует один раз заново раздать `Updater.exe` клиентам
- [X] T026 [US1] `src/SzDiag.Agent/AgentOptions.cs` + место резолва hub в `src/SzDiag.Agent/Program.cs`: агент (обычный старт, `--resume`, `--revert`) находит hub тем же `HubLocator.LocateAsync`
- [X] T027 [US1] `tools/build-dist.ps1`: `build-info.json` (`version`, `buildTime` UTC) в пакет; новое поле `SignpostUrl` в `appsettings.json` апдейтера/агента — из параметра `-SignpostUrl` или прежнего конфига, значения по умолчанию в репо нет — *зеркало (`MirrorBaseUrl`) исключено решением владельца 2026-11-24*
- [X] T028 [US1] (сначала добавить подкоманду `sign-release` в `src/SzDiag.Cli/FleetCommand.cs`: подписывает `ReleaseRecord` ключом `secrets\fleet-sign.key`) `tools/build-dist.ps1 -Publish`: `szcli fleet sign-release dist\host\hub\agent-dist\package.zip` → `release.json`; `PUT /package/{sha}` и `PUT /signpost/release` на signpost — *на GitHub ничего не публикуется (решение владельца 2026-11-24)*

### Implementation — публикация hub

- [X] T029 [US1] `src/SzDiag.Hub/HubOptions.cs` — секция `Hub.Signpost` (`Enabled`, `Url`, `PublishInterval`=30 с, `HubName`, `Takeover`=false, `SignKeyPath`=`secrets\fleet-sign.key`); `hubId` — из `secrets\hub-id` (создаётся при первом старте)
- [X] T030 [US1] `src/SzDiag.Hub/SignpostPublisher.cs` (hosted service) + регистрация в `src/SzDiag.Hub/Program.cs`: при старте и каждые 30 с — `HubRecord` с `endpoints` (LAN-адреса hub, адрес туннеля если включён; `zt` добавится в US3), `seq`=unix-мс; Railway недоступен → hub работает, повтор в фоне — *выгрузка `hub.json` в GitHub-зеркало исключена (решение владельца 2026-11-24)*
- [X] T031 [US1] Статус публикации в `/api/status` (`src/SzDiag.Hub/ManagementApi.cs`) и в статусбаре Desk (`src/SzDiag.Desk/`): «опубликован ЧЧ:ММ / нет связи с указателем / аренда у <hubName>» (FR-025)

**Checkpoint**: US1 проверяется quickstart «Фаза 2» шаги 1–2, 4–7.

---

## Phase 5: User Story 4 — два hub-а не перетягивают клиентов (P3)

**Goal**: аренда указателя.
**Independent Test**: quickstart.md, «Фаза 2», шаг 3.

- [X] T032 [P] [US4] `tests/SzDiag.Signpost.Tests/LeaseTableTests.cs`: пусто → занимает любой; живая + тот же `hubId` → продление; живая + другой `hubId` без `takeover` → 409 с `{ holderHubId, hubName, expiresAt }`; с `takeover` → смена держателя; истёкшая (90 с по часам сервиса, фейковое `TimeProvider`) → занимает любой
- [X] T033 [US4] `src/SzDiag.Signpost/LeaseTable.cs` (в памяти, `TimeProvider`) и подключение к `PUT /signpost/hub` в `src/SzDiag.Signpost/Program.cs`
- [X] T034 [US4] `src/SzDiag.Hub/SignpostPublisher.cs`: 409 → не перезаписывать, в консоль/лог «активен hub <hubName> до …», продолжать попытки; ключ `--takeover` при запуске hub (`src/SzDiag.Hub/Program.cs`, и в `start-hub.cmd` пробрасываются аргументы) → `takeover=true` в первой публикации; потеря аренды (409 при продлении) → сообщение в лог и `/api/status` — *`start-hub.cmd` теперь пробрасывает `%*`*

**Checkpoint**: US4 проверяется quickstart «Фаза 2» шаг 3.

---

## Phase 6: User Story 3 — доступ по своей ZeroTier-сети (P2)

**Goal**: агент входит в сервисную ZT-сеть через libzt, sshd и управляющий канал идут по ZT.
**Independent Test**: quickstart.md, «Фаза 4».

### Gate — спайк libzt (research R8)

- [X] T035 [US3] Консольный прототип `spikes/zt-spike/` (вне `SzDiag.sln`), `ZeroTier.Sockets` 1.8.4, `net8.0` win-x64, `libzt.dll` **рядом с exe** (`IncludeNativeLibrariesForSelfExtract=false`). Проверить 6 пунктов R8: загрузка; HttpClient через `SocketsHttpHandler.ConnectCallback`; SignalR через `HttpMessageHandlerFactory` + `WebSocketFactory` (фоллбэк SSE); accept ZT:22 → прокси `127.0.0.1:22` и скорость против quick tunnel; путь storage и что стирать; время до адреса. Результат — таблица в `specs/002-domain-independent-access/research.md` (R8)
- [X] T036 [US3] **Решение по гейту**: провал пунктов 1–3 → остановить Phase 6, перенести US3 в отдельную спеку, пометить в `docs/dev-backlog.md`; успех → продолжить

### Сеть

- [X] T056 [US3] **Свой контроллер** `src/SzDiag.ZtController/` (рантайм — Docker, не входит в публикацию `build-dist`): `Dockerfile` на `zerotier/zerotier` (1.14.x) + self-contained linux-x64 прокси (minimal API). `entrypoint.sh` поднимает `zerotier-one -d`, прокси слушает `$PORT`, требует `Authorization: Bearer $ZT_PROXY_TOKEN`, пускает только `GET|POST|DELETE /controller/network/{nwid}[/member/{mid}]`, `GET /status` и `GET /admin/identity` (бэкап `identity.secret`), подставляет `X-ZT1-Auth` из `authtoken.secret`. Volume `/var/lib/zerotier-one`; при пустом volume и заданном `ZT_IDENTITY_SECRET` identity восстанавливается. Деплой — отдельный сервис Railway рядом с указателем, публичный домен только у прокси. Тесты прокси (whitelist путей, 401 без токена, подстановка `X-ZT1-Auth`) — `tests/SzDiag.ZtController.Tests/`. Проба 2026-11-24 — research.md, «Свой контроллер на Railway»
- [X] T037 [US3] `tools/zt-network.ps1` (UTF-8 BOM) — через прокси своего контроллера (URL и токен из `secrets\fleet.json`; после T056): создать сервисную сеть (публичная), пул `10.88.0.0/24`, flow rules «разрешено только к/от ZT-адреса hub, всё остальное `drop`», назначить hub-машине статический адрес (`10.88.0.1` ПК, `.2` ноут); забрать `GET /admin/identity` и вместе с `ztNetwork` записать в бандл флота (`fleet.ps1` перешифровывает): потеря volume без бэкапа = новый ID сети

### Tests

- [ ] T038 [P] [US3] `tests/SzDiag.Agent.Tests/ZtRevertTests.cs`: шаг ZT в `Open` пишет `JoinedZt`/`ZtStoragePath`/`ZtNodeId`; `Revert` снимает его **первым** (вместе с quick tunnel), стирает `ZtStoragePath`; повторный `Revert` идемпотентен; упавший шаг не прерывает остальные (как существующие тесты `WindowsSystemAccessManager`)
- [X] T039 [P] [US3] `tests/SzDiag.Hub.Tests/LocalAddressFilterTests.cs`: запрос на локальный адрес из `DenySubnets` (`10.199.89.0/24`) → отказ; management API на адрес сервисной подсети → 403; loopback и LAN → ok
- [ ] T040 [P] [US3] `tests/SzDiag.Hub.Tests/ZtMemberSweeperTests.cs` (фейковый `IZtControllerClient`): член без активной СЗ старше 10 мин → удалён; член активной СЗ и сам hub → не тронуты; `close` СЗ → удаление её `ZtNodeId`

### Implementation — агент

- [ ] T041 [US3] `src/SzDiag.Agent/SzDiag.Agent.csproj`: `ZeroTier.Sockets` 1.8.4, `libzt.dll` копируется рядом с exe (не self-extract); `tools/build-dist.ps1` кладёт её в `dist\client\` и в пакет
- [ ] T042 [US3] (**не** `Send`/`Receive`/`NetworkStream` из `ZeroTier.Sockets` — баги, research.md R8; база — `spikes/zt-spike/ZtStream.cs` + тесты на точную длину, частичную запись и full-duplex) `src/SzDiag.Agent/ZtNode.cs` (старт/стоп ноды, storage `C:\ProgramData\szdiag\zt\<СЗ>`, случайный порт — не 9993, ожидание адреса с таймаутом 60 с) и `src/SzDiag.Agent/ZtStream.cs` (`Stream` поверх zts-сокета)
- [ ] T043 [US3] `src/SzDiag.Agent/ZtSshForwarder.cs` — accept на ZT:`SshPort` → двусторонний проброс на `127.0.0.1:<SshPort>`
- [ ] T044 [US3] `src/SzDiag.Agent/ZtHubTransport.cs` + `src/SzDiag.Agent/SignalRHubLink.cs` и HttpClient агента (`/tools`, `/agent/*`, `RevertStatusReporter`): если найденный hub — endpoint `mode=zt`, соединения идут через `ConnectCallback`/`WebSocketFactory` ZT-ноды
- [ ] T045 [US3] `src/SzDiag.Agent/RevertState.cs` (+ `JoinedZt`, `ZtStoragePath`, `ZtNodeId`, `ZtAddress`) и `src/SzDiag.Agent/WindowsSystemAccessManager.cs`: шаг 11 `Open` — вход в сеть + форвардер (необязательный: провал → доступ открыт, работают quick tunnel/прямой режим, причина — в `ReportAccess`); парная ветка в `Revert` — **первой**, рядом с quick tunnel; переподнятие после `--resume` тем же storage; `--revert` поднимает ноду для отчёта и стирает storage после
- [ ] T046 [US3] `src/SzDiag.Contracts/AccessMode.cs` (+ `Zt = "zt"`), `src/SzDiag.Agent/AccessReporter.cs`: `ReportAccess(Sz, AccessHost=<zt-ip>, AccessMode=zt, host-key)`; `ZtNodeId` — новым необязательным полем `AccessReportRequest` (`src/SzDiag.Contracts/AccessReportRequest.cs`, совместимо со старыми агентами)

### Implementation — hub и CLI

- [ ] T047 [US3] `src/SzDiag.Hub/ZtControllerClient.cs` (`IZtControllerClient`: list/delete member через прокси своего контроллера `<ztControllerUrl>/controller/network/{nid}/member/{mid}`, Bearer `ztControllerToken` из `secrets\fleet.json` — токен **никогда** не уходит агенту, FR-023) и `src/SzDiag.Hub/ZtMemberSweeper.cs` (hosted, раз в минуту, `SweepAfter`=10 мин); удаление члена при `close` в `src/SzDiag.Hub/ManagementApi.cs`/`SessionRegistry.cs`
- [X] T048 [US3] `src/SzDiag.Hub/LocalAddressFilter.cs` + регистрация в `src/SzDiag.Hub/Program.cs`; `Hub.DenySubnets` (по умолчанию `["10.199.89.0/24"]`) и `Hub.ZeroTier` (`Enabled`, `Network`, `SweepAfter`) в `src/SzDiag.Hub/HubOptions.cs`; фильтр по `HttpContext.Connection.LocalIpAddress`, не bind на IP
- [ ] T049 [US3] (**только после T048** — до него `/api` hub с ZT-адреса закрыт лишь токеном, research.md R9) `src/SzDiag.Hub/SignpostPublisher.cs`: endpoint `mode=zt` (адрес hub в сервисной подсети, если адаптер есть) первым в `endpoints`, `ztNetwork` в записи
- [ ] T050 [US3] `src/SzDiag.Cli/Program.cs` (`target`, стр. ~563–579): для `AccessMode.Zt` — `ssh -p <port> svc-diag@<zt-ip>` с пиннингом host-ключа, как для туннеля; `src/SzDiag.Agent/AccessReporter.cs`/hub — пересечение подсети `10.88.0.0/24` с маршрутами клиента → предупреждение в `ReportAccess`

**Checkpoint**: US3 проверяется quickstart «Фаза 4» целиком.

---

## Phase 7: Polish & Cross-Cutting

- [ ] T051 [P] `docs/dev-knowledge-base.md`: указатель, конверты, аренда, `HubLocator`, `AccessMode.zt`, `RevertState` ZT-шаг, новые опции hub
- [ ] T052 [P] `CLAUDE.md` — раздел «Статус / следующее»: абзац про фичу со ссылкой на спеку; `docs/live-checklist-<дата>.md` из quickstart
- [ ] T053 [P] `tools/doctor.ps1`: проверки — `secrets\fleet.json` есть, `/healthz` указателя отвечает, локальная ZT-сеть hub в сервисной подсети, личная сеть в `DenySubnets`
- [ ] T054 Удалить `spikes/zt-spike/` после переноса выводов в research.md
- [ ] T055 `dotnet build` + `dotnet test` целиком — зелёные; прогнать quickstart.md фазы 1–2 живьём (фаза 4 — на ближайшей заявке)

---

## Dependencies & Execution Order

- **Setup (T001–T003)** → **Foundational (T004–T008)** → всё остальное
- **US2 (T009–T016)** — после Foundational; поставляется отдельно (MVP)
- **US1 (T017–T031)** — после US2 (ключ подписи флота, `SignPublicKey` в dist)
- **US4 (T032–T034)** — после T020/T030 (сервис и публикатор)
- **US3 (T035–T050, T056)** — после US1; **T035–T036 — гейт (пройден 2026-11-24)**; T056 (контроллер) → T037 (сеть) → остальное
- **Polish** — после нужных историй

Внутри историй: тесты → модели → сервисы → интеграция; T013 зависит от T012, T014 от T013,
T025–T026 от T023–T024, T045 от T042–T044.

## Parallel Opportunities

- Setup: T002 ∥ T003
- Foundational: T004 ∥ T005 ∥ T007
- US2: T009 ∥ T010
- US1: T017 ∥ T018 ∥ T019; сервис (T020–T022) ∥ клиенты (T023–T027)
- US3: T038 ∥ T039 ∥ T040 после гейта; агент (T041–T046) ∥ hub (T047–T048)
- Polish: T051 ∥ T052 ∥ T053

## Implementation Strategy

1. **MVP = Setup + Foundational + US2** — сразу снимает текущую боль «апдейтер с ноута не
   работает с hub на ПК»; заплатка `ExtraAgentTokens` превращается в переходный список.
2. **+ US1 + US4** — указатель и аренда на существующих адресах (LAN/CF); деплой Railway.
3. **Спайк (T035)** → решение по гейту.
4. **+ US3** — ZeroTier-доступ; после него US1 проверяется «без домена» полностью.
5. Ревью всей ветки (`/code-review`) — один раз в конце, после `/speckit-converge`.
