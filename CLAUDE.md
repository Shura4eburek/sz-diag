# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Что это

Система удалённой диагностики клиентских машин в сервисном центре по номеру **СЗ**
(сервисная заявка, напр. `156864`). Claude работает на чистом сервисном боксе и
дотягивается до клиентских машин по SSH, не устанавливаясь на них. Агент на клиенте
временно открывает SSH-доступ и регистрируется под номером СЗ; центральный hub держит
маппинг `СЗ → IP`, а CLI показывает, какие СЗ онлайн. См. [docs/vision.md](docs/vision.md).

**Плотная карта всего функционала** (протокол SignalR/`/api`, точки расширения, таблицы
параметров, рецепты) — [docs/dev-knowledge-base.md](docs/dev-knowledge-base.md). Держи её в
актуальном состоянии при правках протокола/жизненного цикла.

Модель угроз (важно): клиентская машина — наименее доверенное устройство, часто заражена.
Рабочий токен на неё **не заезжает**; диагностика идёт по сети (SSH). Весь доступ на
клиенте временный и **откатывается без следов** при закрытии СЗ.

## Статус / следующее

**План Б (sshd под SYSTEM), RunDiag и апдейтер клиента — реализованы и e2e-проверены на
онлайн-СЗ (2026-07-21).** Живой SSH-коннект работает (план Б + ACL-фикс host-ключа);
`szcli diag run <СЗ> [секции]` даёт полный структурированный `diag.md` (железо/диски/SMART/
события/`reboots`/`whea`) вместо россыпи ad-hoc ssh — секции гоняются точечно; клиент
самообновляется через `SzDiag.Updater.exe` (находит hub, тянет свежий пакет агента —
конец ручному циклу раздачи через share). Апдейтер:
[docs/superpowers/specs/2026-07-21-agent-updater-design.md](docs/superpowers/specs/2026-07-21-agent-updater-design.md).
RunDiag:
[docs/superpowers/specs/2026-07-20-agent-diag-commands-design.md](docs/superpowers/specs/2026-07-20-agent-diag-commands-design.md).

Открытые направления:
- **Наполнение базы знаний** паттернами «симптом → причина» по мере заявок. Первый живой
  кейс — СЗ 159873 (спонтанные ребуты; вердикт питание/контакт, на стенде не воспроизводится),
  паттерн в `Симптомы/случайные перезагрузки.md` с матрицей дискриминаторов.
- **Упаковка стресс-тулов** (TM5/OCCT/FurMark) в образ/архив вместо гигов при раздаче —
  идея зафиксирована, спеки пока нет.

## Команды

```powershell
dotnet build                       # сборка солюшена
dotnet test                        # все автотесты (~174), без хоста/клиента
dotnet test tests/SzDiag.Agent.Tests            # тесты одного проекта
dotnet test --filter FullyQualifiedName~RevertCoordinator   # один класс/тест
$env:SZDIAG_LIVE=1; dotnet test    # + live-тест vgabios (реально ходит на TPU; по умолчанию skip)
.\tools\build-dist.ps1             # публикация готового dist (host + client), см. ниже
.\tools\build-dist.ps1 -HubIp 192.168.1.50      # клиент — отдельная ВМ: LAN-IP хоста
```

`build-dist.ps1` публикует self-contained single-file exe (win-x64), генерит SSH-ключ
`secrets\svc_diag_key`, пишет конфиги/лаунчеры. Результат: `dist\host\` (hub + cli,
`start-hub.cmd`, `szcli.cmd`) и `dist\client\` (agent + `updater` + `service_key.pub` +
`testsuite.json`). Точка входа на клиенте — **`SzDiag.Updater.exe`** (`SzDiag.Updater`):
находит hub, сверяет `version.txt`, при расхождении качает свежий пакет агента с hub
(`/agent/version|package|package.sha256`, из `dist\host\hub\agent-dist\`, кладёт build-dist)
и запускает `agent.exe`. Убирает ручной цикл раздачи через share — на клиент достаточно
один раз положить `SzDiag.Updater.exe` + `appsettings.json`. Спека/план —
[docs/superpowers/specs/2026-07-21-agent-updater-design.md](docs/superpowers/specs/2026-07-21-agent-updater-design.md).
Ручной e2e-прогон и траблшутинг — в [docs/TESTING.md](docs/TESTING.md), включая раздел
про headless-управление по SSH (GUI-тулзы без десктопа падают/висят — обход через
`schtasks /it /rl highest`, но UAC/Secure Desktop так не обойти).

Целевой фреймворк — **net8.0**. Файл сборки должен быть **UTF-8 с BOM** (PowerShell 5.1
иначе ломает кириллицу — см. коммит 3e60857).

## Архитектура

Семь проектов в `src/` + зеркальные тесты в `tests/`:

- **SzDiag.Contracts** — DTO и константы, общие для агента и hub (`HubRoutes` — единый
  источник имён SignalR-методов и заголовков, чтобы строки не расходились). Меняешь протокол
  → правь здесь, а не хардкодь строки на концах.
- **SzDiag.Hub** — ASP.NET Core сервис на хосте. `AgentHub` (SignalR, путь `/agents`) —
  тонкий слой, агенты вызывают `Register`/`Heartbeat`/`UploadReportFile`. `ManagementApi`
  (`/api/*`, minimal API) — для CLI: `sessions`, `close`, `test`, `target`. Состояние:
  `SessionRegistry` (in-memory активные СЗ) + `SqliteSessionStore` (история). `OfflineSweeper`
  (hosted service) метит СЗ офлайн по таймауту heartbeat.
- **SzDiag.Cli** (`szcli`) — тонкий клиент к `/api`. Команды: `watch` (по умолчанию),
  `list`, `close <СЗ>`, `target <СЗ>`, `test run <СЗ>`, `kb record/summary/search …`.
- **SzDiag.Agent** — консоль на клиенте (`net8.0`, требует прав админа, `app.manifest`).
  Открывает доступ, коннектится к hub по SignalR, шлёт heartbeat, по команде hub гоняет
  тест-раннер и заливает отчёт. `--revert <statePath>` — режим watchdog/автозакрытия.
- **SzDiag.Updater** — тонкий `SzDiag.Updater.exe`, точка входа на клиенте вместо агента.
  Находит hub (`HubDiscovery`, вынесен в Contracts), сверяет `version.txt`, при расхождении
  качает пакет агента с hub (`/agent/version|package|package.sha256`, sha256-проверка),
  распаковывает поверх (`PackageApplier` — кроме `appsettings.json`/`tools/`) и запускает
  `agent.exe`. Пакет собирает `build-dist` в `dist\host\hub\agent-dist\`.
- **SzDiag.Hardware** — определение видеокарты по Windows PCI hardware ID
  (`PCI\VEN_..&DEV_..&SUBSYS_..`). `PciId.Parse` разбирает id, `PciIdsParser` парсит базу
  pci.ids, `GpuRepository` (SQLite `gpu.db`) хранит вендоров/устройства/платы, `GpuResolver`
  резолвит по кэш-паттерну БД→miss→`IGpuScraper`→запись. Живой `VgaBiosScraper`
  (`TechPowerUpClient` + `VgaBiosParser` на AngleSharp) дорезолвивает точную партнёрскую
  плату (SKU) и спеки прошивки из TechPowerUp VGA BIOS collection по subsystem ID (таблица
  `card`); `gpu-specs`-каталог за интерактивной CAPTCHA — вне scope, `NotImplementedGpuScraper`
  остаётся заглушкой device-фоллбэка. CLI: `szcli hw import/update/resolve`.
- **SzDiag.Kb** — работа с базой знаний в формате Obsidian-vault. **Контент базы знаний —
  на украинском** (сервис/колл-центр украиноязычные): имена папок, frontmatter-ключи,
  скелеты и проза. `KbPaths` — единственное место с именами папок: `СЗ/`, `Замовлення/`,
  `Дефекти/`, `Компоненти/`, `Пристрої/`, `Симптоми/`. Файлы по СЗ: `запит.md`,
  `діагностика.md`, `дії.md`, `висновок.md`. Frontmatter-ключи: `сз/замовлення/дефект/
  замінено/пристрій/симптом/статус/вердикт/дата`. Hub пишет сюда скелет по каждой СЗ и
  отчёты. По СЗ, помимо каркаса, пишется `висновок.md` — итоговый вывод (блок «Для клієнта»
  для колл-центра + технічний розбір для обучения диагностике); паттерны «симптом → причина»
  копятся в `Симптоми/` и линкуются из техразбора. Единый индексируемый frontmatter живёт в
  `<sz>.md`; `висновок.md` встраивается через `![[висновок]]` без своего YAML (иначе Dataview
  задваивает СЗ).

**Аутентификация:** pre-shared токены. Агент↔hub — заголовок `X-SzDiag-Token`
(middleware в `Program.cs`); CLI↔hub — `X-SzDiag-Mgmt-Token` (endpoint filter). Оба
задаются в конфиге hub (`Hub.AgentToken` / `Hub.ManagementToken`).

**Жизненный цикл доступа (ключевой инвариант).** `WindowsSystemAccessManager.Open`
применяет шаги по порядку (остановка системного sshd, если занял порт → firewall →
`LocalAccountTokenFilterPolicy` → учётка `svc-diag` → портативный sshd **под SYSTEM**
(транзиентная scheduled task, свои host-ключи + свой `authorized_keys`) → watchdog
scheduled task) и **прогрессивно** пишет `RevertState` в файл после каждого шага
(переживает краш). `Revert` откатывает в обратном порядке **по флагам** — каждый шаг
идемпотентен, повторный вызов безопасен. Откат срабатывает тремя путями: клавиша `C` /
крестик окна (`ConsoleCloseGuard` ловит `CTRL_CLOSE_EVENT`), команда `close` с хоста
(hub → SignalR `Revert`), или watchdog scheduled task по таймауту (`WatchdogHours`).

sshd поднимается транзиентной задачей под SYSTEM (`szdiag-sshd-<СЗ>`, `PortableSshServer`):
у LocalSystem есть `SeTcbPrivilege` для logon-token при publickey-логине — у админ-агента
дочерним процессом его нет (`Connection reset` на userauth). Свой sshd владеет собственным
`AuthorizedKeysFile` в рабочей папке (`Match Group administrators` в конфиге), системный
`administrators_authorized_keys` не трогаем.

При правке `Open` **всегда** добавляй парную ветку в `Revert` под своим флагом в
`RevertState`, иначе на клиентской машине останутся следы (задача, живой sshd, ключи).

## Конвенции

- Комментарии и пользовательский вывод (код, консоль) — на русском (как в существующем коде).
- **База знаний (kb) — на украинском**: имена папок, frontmatter-ключи, скелеты и проза
  (сервис/колл-центр украиноязычные). Технические отчёты прогонов (`report.md`/`diag.md`) —
  не kb-заметки, заголовки секций там из C# `Name`.
- Секреты (`*.key`, `secrets/`), база знаний (`kb/`, данные клиентов) и рантайм-БД
  (`*.db`) в `.gitignore` — не коммитить.
- Пути к ключу/testsuite/appsettings резолвятся от `AppContext.BaseDirectory` (рядом с
  exe), а не от рабочего каталога — не завязывайся на CWD.
