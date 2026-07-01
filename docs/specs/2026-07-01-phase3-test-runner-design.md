# sz-diag Фаза 3 — дизайн: тест-раннер + отчёты со скринами

> Дата: 2026-07-01. Статус: согласовано с пользователем.
> Опирается на реализованные Фазы 1-2 (агент, hub, cli, SzDiag.Kb).

## Цель

Прогон набора диагностических тестов на клиентской машине и генерация отчёта
(markdown + скриншоты) в базе знаний по СЗ. Гибрид: CLI-диагностика (текст) + скриншоты
экрана как визуальный пруф.

## Ключевое ограничение

Скриншоты требуют интерактивной сессии рабочего стола (SSH её не имеет). Снять их может
только процесс в интерактивной сессии — **агент** на клиенте. Поэтому тест-раннер живёт
в агенте, а не гоняется по SSH.

## Поток

```
szcli test run <СЗ>
   → POST /api/sessions/<СЗ>/test (management-API)
   → hub пушит агенту команду RunTests(<СЗ>) по SignalR (как revert)
   → агент: прогоняет шаги testsuite.json (CLI-команды + скриншоты),
             собирает report.md
   → агент: заливает report.md + PNG на hub (UploadReportFile по одному файлу)
   → hub: пишет в kb/СЗ/<СЗ>/reports/<timestamp>/ через KbPaths
```

Прогон асинхронный: `test run` возвращается сразу («прогон запущен»), отчёт появляется
в kb по завершении.

## Границы

**В Фазе 3:** тест-раннер и захват экрана в агенте, приём отчёта на hub и запись в kb,
CLI-команда `test run`, дефолтный `testsuite.json`.
**Не в Фазе 3:** экспорт отчёта в PDF/HTML (Obsidian умеет экспорт при нужде); чанкинг
файлов > лимита; авто-открытие GUI-тулов агентом.

## Компоненты

### SzDiag.Kb (новое)
- `ReportMarkdownBuilder` — собирает `report.md` из модели результатов (шапка + шаги +
  ссылки на скрины). Тестируемо.
- `KbPaths.ReportsDir(sz)` / `ReportDir(sz, timestamp)` — пути отчётов.
- `TestReport` / `TestStepResult` — модель результата (переиспользуется агентом и hub).

### SzDiag.Hub (новое)
- `IAgentCommandSender.SendRunTestsAsync(connId, sz)` + SignalR-реализация; push-метод
  `RunTests` (client method на агенте).
- Метод хаба `UploadReportFile(UploadReportPart part)` — приём файла (относительный путь
  + байты).
- `IReportStore`/`KbReportStore` — пишет принятые файлы в `kb/СЗ/<СЗ>/reports/<ts>/`.
- Management-эндпоинт `POST /api/sessions/{sz}/test` — триггерит push (404 если СЗ нет).
- `AddSignalR(o => o.MaximumReceiveMessageSize = 10 * 1024 * 1024)`.

### SzDiag.Agent (новое)
- `TestSuite`/`TestStep` — загрузка `testsuite.json` (шаги: `command` | `screenshot`).
- `ICommandExecutor`/`ProcessCommandExecutor` — запуск CLI-команд (stdout/exit).
- `IScreenCapturer`/`GdiScreenCapturer` — снимок основного дисплея (BitBlt). Юнитами не
  покрывается (Windows GUI), проверка на VM.
- `TestRunner` — выполняет шаги, собирает `TestReport` (падение шага → фиксируется, прогон
  продолжается).
- `TestReportRunner` — оркестрация: run → ReportMarkdownBuilder → загрузка на hub через
  `IHubLink`.
- Обработка push `RunTests` в `IHubLink`/`AgentSession`.

### SzDiag.Cli (новое)
- Команда `test run <СЗ>` → `POST /api/sessions/{sz}/test`.

## Набор тестов (testsuite.json)

Упорядоченный список шагов. Каждый шаг:
- `{ "type": "command", "name": "…", "run": "<powershell/cmd>" }` — stdout + exit code;
- `{ "type": "screenshot", "name": "…" }` — снимок основного экрана.

Дефолтный набор (редактируемый):
`systeminfo`; диски+SMART (`Get-PhysicalDisk`, `Get-StorageReliabilityCounter`); память
(`Get-CimInstance Win32_PhysicalMemory`); драйверы (`driverquery`); ошибки Event Log
(`Get-WinEvent`); GPU (`dxdiag /t`); + 1-2 `screenshot`-шага (состояние экрана как пруф).

## Формат отчёта

`kb/СЗ/<СЗ>/reports/<timestamp>/`:
- `report.md` — шапка (СЗ, хост, дата/время); по каждому шагу: заголовок, для `command`
  — команда и вывод в code-блоке (или «ошибка: …»); для `screenshot` — `![[screen-N.png]]`;
- `screen-*.png` — снимки рядом с report.md.

Много прогонов → разные `<timestamp>`-папки (история сохраняется). Рендерится в Obsidian.

## Передача на hub

Агент шлёт файлы по SignalR по одному: `UploadReportFile(sz, relativePath, bytes)`.
Лимит `MaximumReceiveMessageSize` поднят до ~10 МБ (типичный PNG влезает). Чанкинг —
на будущее.

## Обработка ошибок

- Шаг упал (ненулевой код/исключение) — фиксируется в отчёте, прогон продолжается.
- Нет активного агента для СЗ — `POST …/test` возвращает 404.
- Скрин не снялся (нет интерактивной сессии) — шаг помечается «скрин недоступен», прогон
  не падает.
- Приём файла на hub санитизирует относительный путь (запрет выхода за пределы reports).

## Тестирование

Юниты:
- `ReportMarkdownBuilder` (SzDiag.Kb.Tests): сборка md из `TestReport` (command-вывод,
  ошибка, ссылки на скрины).
- `TestRunner` (SzDiag.Agent.Tests): с фейковыми `ICommandExecutor`/`IScreenCapturer` —
  порядок шагов, фиксация падения, продолжение.
- `TestSuite` (SzDiag.Agent.Tests): парсинг конфига.
- `KbReportStore` (SzDiag.Hub.Tests): запись принятых файлов в kb, санитизация пути.
- Интеграция hub: `UploadReportFile` → файл появляется в kb/СЗ/<n>/reports/.
`GdiScreenCapturer` — ручной/VM-прогон. Регресс существующих тестов.

## Разбивка на планы

- **План 1 (серверная часть):** `SzDiag.Kb` (модель отчёта, `ReportMarkdownBuilder`,
  пути) + `SzDiag.Hub` (приём файлов, `KbReportStore`, push `RunTests`, mgmt-эндпоинт,
  лимит) + `SzDiag.Cli` (`test run`). Даёт: hub принимает отчёт и пишет в kb; CLI триггерит.
- **План 2 (клиентская часть):** `SzDiag.Agent` (`TestSuite`, `TestRunner`,
  `GdiScreenCapturer`, `TestReportRunner`, обработка push `RunTests`, загрузка на hub).
  Даёт: агент прогоняет тесты, снимает скрины, заливает отчёт.

План 1 первым (серверный контракт), План 2 использует его.
