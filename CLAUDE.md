# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Что это

Система удалённой диагностики клиентских машин в сервисном центре по номеру **СЗ**
(сервисная заявка, напр. `156864`). Claude работает на чистом сервисном боксе и
дотягивается до клиентских машин по SSH, не устанавливаясь на них. Агент на клиенте
временно открывает SSH-доступ и регистрируется под номером СЗ; центральный hub держит
маппинг `СЗ → IP`, а CLI показывает, какие СЗ онлайн. См. [docs/vision.md](docs/vision.md).

Модель угроз (важно): клиентская машина — наименее доверенное устройство, часто заражена.
Рабочий токен на неё **не заезжает**; диагностика идёт по сети (SSH). Весь доступ на
клиенте временный и **откатывается без следов** при закрытии СЗ.

## Команды

```powershell
dotnet build                       # сборка солюшена
dotnet test                        # все автотесты (~76), без хоста/клиента
dotnet test tests/SzDiag.Agent.Tests            # тесты одного проекта
dotnet test --filter FullyQualifiedName~RevertCoordinator   # один класс/тест
.\tools\build-dist.ps1             # публикация готового dist (host + client), см. ниже
.\tools\build-dist.ps1 -HubIp 192.168.1.50      # клиент — отдельная ВМ: LAN-IP хоста
```

`build-dist.ps1` публикует self-contained single-file exe (win-x64), генерит SSH-ключ
`secrets\svc_diag_key`, пишет конфиги/лаунчеры. Результат: `dist\host\` (hub + cli,
`start-hub.cmd`, `szcli.cmd`) и `dist\client\` (agent + `service_key.pub` + `testsuite.json`).
Ручной e2e-прогон и траблшутинг — в [docs/TESTING.md](docs/TESTING.md), включая раздел
про headless-управление по SSH (GUI-тулзы без десктопа падают/висят — обход через
`schtasks /it /rl highest`, но UAC/Secure Desktop так не обойти).

Целевой фреймворк — **net8.0**. Файл сборки должен быть **UTF-8 с BOM** (PowerShell 5.1
иначе ломает кириллицу — см. коммит 3e60857).

## Архитектура

Шесть проектов в `src/` + зеркальные тесты в `tests/`:

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
- **SzDiag.Hardware** — определение видеокарты по Windows PCI hardware ID
  (`PCI\VEN_..&DEV_..&SUBSYS_..`). `PciId.Parse` разбирает id, `PciIdsParser` парсит базу
  pci.ids, `GpuRepository` (SQLite `gpu.db`) хранит вендоров/устройства/платы, `GpuResolver`
  резолвит по кэш-паттерну БД→miss→`IGpuScraper`→запись. Живой `VgaBiosScraper`
  (`TechPowerUpClient` + `VgaBiosParser` на AngleSharp) дорезолвивает точную партнёрскую
  плату (SKU) и спеки прошивки из TechPowerUp VGA BIOS collection по subsystem ID (таблица
  `card`); `gpu-specs`-каталог за интерактивной CAPTCHA — вне scope, `NotImplementedGpuScraper`
  остаётся заглушкой device-фоллбэка. CLI: `szcli hw import/update/resolve`.
- **SzDiag.Kb** — работа с базой знаний в формате Obsidian-vault (`KbPaths` — единственное
  место с именами папок: `СЗ/`, `Заказы/`, `Дефекты/`, `Компоненты/`, `Устройства/`,
  `Симптомы/`). Hub пишет сюда скелет по каждой СЗ и отчёты. По СЗ, помимо каркаса,
  пишется `вывод.md` — итоговый вывод (блок «Для клиента» для колл-центра + технический
  разбор для обучения диагностике); паттерны «симптом → причина» копятся в `Симптомы/` и
  линкуются из техразбора. Единый индексируемый frontmatter живёт в `<sz>.md`; `вывод.md`
  встраивается через `![[вывод]]` без своего YAML (иначе Dataview задваивает СЗ).

**Аутентификация:** pre-shared токены. Агент↔hub — заголовок `X-SzDiag-Token`
(middleware в `Program.cs`); CLI↔hub — `X-SzDiag-Mgmt-Token` (endpoint filter). Оба
задаются в конфиге hub (`Hub.AgentToken` / `Hub.ManagementToken`).

**Жизненный цикл доступа (ключевой инвариант).** `WindowsSystemAccessManager.Open`
применяет шаги по порядку (OpenSSH Server → служба → firewall →
`LocalAccountTokenFilterPolicy` → учётка `svc-diag` → `administrators_authorized_keys` →
watchdog scheduled task) и **прогрессивно** пишет `RevertState` в файл после каждого шага
(переживает краш). `Revert` откатывает в обратном порядке **по флагам** — каждый шаг
идемпотентен, повторный вызов безопасен. Откат срабатывает тремя путями: клавиша `C` /
крестик окна (`ConsoleCloseGuard` ловит `CTRL_CLOSE_EVENT`), команда `close` с хоста
(hub → SignalR `Revert`), или watchdog scheduled task по таймауту (`WatchdogHours`).

При правке `Open` **всегда** добавляй парную ветку в `Revert` под своим флагом в
`RevertState`, иначе на клиентской машине останутся следы. На Windows ключ админа идёт в
`administrators_authorized_keys` (per-user `authorized_keys` OpenSSH для админов игнорирует).

## Конвенции

- Комментарии и пользовательский вывод — на русском (как в существующем коде).
- Секреты (`*.key`, `secrets/`), база знаний (`kb/`, данные клиентов) и рантайм-БД
  (`*.db`) в `.gitignore` — не коммитить.
- Пути к ключу/testsuite/appsettings резолвятся от `AppContext.BaseDirectory` (рядом с
  exe), а не от рабочего каталога — не завязывайся на CWD.
