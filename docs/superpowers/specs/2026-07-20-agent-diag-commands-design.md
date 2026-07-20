# Агентские диагностические команды (RunDiag) — дизайн

**Дата:** 2026-07-20
**Статус:** ✅ реализовано 2026-07-21 (все секции кроме `network`/`security`), ждёт e2e на онлайн-СЗ

## Проблема (оптимизация токенов)

Сейчас, чтобы поставить диагноз клиентской машине, Claude заходит по SSH и гоняет **россыпь
ad-hoc PowerShell-команд** (`systeminfo`, `Get-PhysicalDisk`, SMART, `Get-PnpDevice`,
`Get-WinEvent`, температуры, дампы…). Каждая — round-trip; Claude сам решает, что запускать,
парсит сырой вывод. Это дорого по токенам, медленно и невоспроизводимо между СЗ.

Инфраструктура для решения **уже есть**: тест-раннер (`TestReportRunner`→`TestRunner`) исполняет
`command`-шаги и заливает структурированный Markdown-отчёт в KB через `UploadReportFile`. Не хватает
только **курируемого набора read-only диагностических проб** и **отдельной команды** для их запуска.

## Цель

Одна команда `szcli diag run <СЗ> [секции]` → агент гоняет встроенный набор диагностических проб
(read-only, без стресс-тулов) → в `kb/СЗ/<sz>/reports/<ts>/diag.md` ложится структурированный
снапшот, который Claude читает **один раз**. Вместо десятков ssh-round-trip — один отчёт.

## Решение: команда `RunDiag` + встроенный каталог проб

Зеркалит `RunTests`, но:
- **read-only** (никаких стресс-нагрузок, `taskkill`, скриншотов) — безопасно на живой машине;
- **пробы встроены в агента** (`DiagnosticProbes` — статический каталог `TestStep`-команд), не
  требуют файла на диске — работает всегда, даже без `testsuite.json`;
- **секционный фильтр** (как `filter` у тестов): `RunDiag <sz> "storage,events"` гоняет подмножество;
  без фильтра — полный профиль.

Отчёт строится тем же `ReportMarkdownBuilder` (или его вариантом с группировкой по секциям) и
заливается тем же `UploadReportPart`. KB-интеграция и жизненный цикл — бесплатно.

### Каталог секций (что заменяет какие ssh-команды)

| Секция | Пробы (PowerShell/CIM) | Зачем |
|---|---|---|
| `system` | `Get-ComputerInfo` (подмножество), модель/BIOS/TPM/SecureBoot, uptime | базовый контекст машины |
| `cpu` | `Win32_Processor` (имя, ядра, загрузка) | идентификация + нагрузка |
| `memory` | `Win32_PhysicalMemory` (объём, слоты, скорость, партномера) | конфиг ОЗУ, пустые слоты |
| `gpu` | `Win32_VideoController` + `Get-PnpDevice` (PCI ID, версия драйвера) | **питает hardware-резолвер** (PCI ID → плата) |
| `storage` | `Get-PhysicalDisk`+`Get-StorageReliabilityCounter` (SMART: износ, реаллокации, температура, часы), разделы, свободно | здоровье дисков — частая причина |
| `temps` | `MSAcpi_ThermalZoneTemperature` (root/wmi) | перегрев |
| `drivers` | `Get-PnpDevice -Status Error/Unknown` | битые/неопознанные устройства |
| `network` | адаптеры, IP, скорость линка | сетевые проблемы |
| `events` | `Get-WinEvent`: критические/ошибки System+Application, WHEA (аппаратные), disk, bugcheck | недавние сбои |
| `reliability` | `Win32_ReliabilityRecords`, список minidump'ов | история крашей/BSOD |
| `battery` | `Win32_Battery` + design vs full charge (износ) | ноутбуки |
| `security` | статус Defender, быстрая сводка угроз | клиент часто заражён (модель угроз) |

**Рекомендуемый MVP:** `system, cpu, memory, gpu, storage, temps, drivers, events, reliability, battery`
(без `network`/`security` в первой итерации — добавить по потребности).

### Вторичная команда `RunCollect` (артефакты) — опционально

`szcli diag collect <СЗ> [minidumps|logs]` — агент собирает и заливает файлы, которые иначе тащить
по SFTP вручную: minidump'ы (`C:\Windows\Minidump`), `setupapi.dev.log`, CBS.log (хвост). Тем же
`UploadReportPart` (Content=байты файла). Отдельная итерация после `RunDiag`.

## Точки правки (по рецепту из dev-knowledge-base)

- **Contracts**: `HubRoutes.RunDiag="RunDiag"` (+ при нужде `RunCollect`). DTO не нужен — `sz`+`filter?`
  как у `RunTests`.
- **Hub**: `IAgentCommandSender.SendRunDiagAsync` + реализация; сервис `DiagRunTrigger` (образец
  `TestRunTrigger`); `POST /api/sessions/{sz}/diag?sections=` в `ManagementApi`; регистрация в `Program.cs`.
- **Agent**: `IHubLink.OnRunDiag` + `SignalRHubLink._conn.On<string,string?>`; обработчик в `Program.cs`
  (через `Task.Run`) → новый `DiagnosticRunner` поверх `TestRunner` с каталогом `DiagnosticProbes` →
  заливка `diag.md`.
- **CLI**: `HubApiClient.TriggerDiagAsync` + ветка `diag run`/`diag collect` в `Program.cs`.
- **Новый код агента**: `DiagnosticProbes` (статический `IReadOnlyList<TestStep>` по секциям),
  опц. `DiagReportBuilder` (группировка по секциям в Markdown). Пробы — обычные `command`-шаги,
  переиспользуют `TestRunner`/`PowerShellCommandExecutor` без изменений.

## Тестирование

- Юниты: `DiagnosticProbes` содержит ожидаемые секции/id; фильтр секций отбирает подмножество
  (переиспользовать `FilterSteps`); `DiagReportBuilder` рендерит секции. Реальные CIM-пробы — ручной e2e.
- E2e: `szcli diag run <СЗ>` на онлайн-СЗ → `diag.md` в KB с непустыми секциями.

## Что НЕ входит (YAGNI)

- Не пишем свой WMI-слой — только PowerShell/CIM-строки в каталоге проб.
- Не тащим сторонние сенсоры (LibreHardwareMonitor и пр.) в первой итерации — только штатные
  `MSAcpi_ThermalZoneTemperature`/SMART.
- Не заменяем стресс-тесты (`RunTests`) — `RunDiag` рядом, read-only, для быстрого снапшота.
- Не парсим отчёт на hub — hub только сохраняет файл; интерпретирует Claude, читая KB.

## Почему отдельная команда, а не шаги в testsuite.json

Диагностика должна быть **always-available, read-only, быстрой** и не зависеть от наличия
`testsuite.json`/стресс-тулов на клиенте. Встроенный каталог + отдельный `RunDiag` дают это;
шаги в `testsuite.json` смешали бы безопасный снапшот со стресс-нагрузкой и потребовали бы файла.
