# OCCT для клиента (стресс combined + power)

Агент гоняет OCCT командой (проверено на OCCT Enterprise CLI **13.1.5**):

```
OCCTCmd.exe test --schedule="<dir>\schedule.json" ^
  --auto-start=true --auto-close=true ^
  --auto-save-report=true --report-file="<dir>\occt-report.html" ^
  --overwrite-report-file=true --auto-enable-sensors=true
```

Шаг в `testsuite.json` — `runToCompletion: true`: агент ждёт самозавершения OCCT
(предохранитель 780 с), снимает скрин в процессе и **прикладывает HTML-отчёт**
(`occt-report.html`) артефактом к `report.md` + заливает на hub рядом со скриншотами.

## Что положить в `client-tools\occt\`

`build-dist.ps1` копирует `client-tools\*` → `dist\client\tools\`, поэтому агент видит
их как `tools\occt\...`. Нужны:

| Файл | Откуда |
|---|---|
| `OCCTCmd.exe` | из установки OCCT (единый ~187 МБ self-contained) |
| `schedule.json` | из этого каталога (`deploy\occt\schedule.json`) — Combined 30 мин + PowerSupply 30 мин; рядом лежат профили smoke/long/infinite (см. ниже) |
| `*.oke` (лицензия) | лицензия OCCT Enterprise — CLI без неё не запускается |

> `OCCT.config.json` **не обязателен**: сенсоры включаются `--auto-enable-sensors=true`
> автодетектом, а тест целиком описан в `schedule.json`. Держать машинно-зависимый
> `OCCT.config.json` от другого стенда не нужно.

## schedule.json

Схема — как у расписаний в `OCCT.config.json` (объект с `ScheduleType` + `Periods[]`).
Здешний файл: два периода — `Combined` и `PowerSupply` по `00:30:00`. Потоки CPU в
конфиге стоят `Auto` (адаптируются к числу ядер), GPU-индекс `0`, память в процентах —
т.е. расписание **портируемо** между машинами. Хочешь другую длительность/набор —
поправь `Duration`/`TestType` (валидные типы: `Combined`, `PowerSupply`, `CpuOcct`,
`CpuLinpack`, `Memtest`, `Vram`, `Gpu3d`, `GpuUnreal`).

### ⚠️ Конечное расписание ≠ «машина выстояла»

Расписание с `IsInfinite: false` **само завершает тест** по истечении `Duration`, и снаружи
это выглядит ровно как «нагрузка держится, машина живая». На 160306 так и вышло: прогон
кончился через 10:11, а в выводах чуть не осело «выстояла 40 минут». Перед каждым прогоном
проверяй состав:

```powershell
.\tools\occt-schedule.ps1 -Info deploy\occt\schedule.json
# → ИТОГО: тест САМ завершится через 01:00:00
```

### Готовые профили

Собираются из эталона одной командой (`.\tools\occt-schedule.ps1 -Make`) — все настройки
тестов берутся из `schedule.json`, меняется только длительность:

| Файл | Что | Когда |
|---|---|---|
| `schedule-smoke.json` | по 5 мин на период (~10 мин) | быстрая проверка, что тул вообще поехал |
| `schedule-long.json` | по 1.5 ч на период (~3 ч) | штатный прогон на воспроизведение дефекта |
| `schedule-infinite.json` | `IsInfinite` на всех периодах | «до вырубона» — когда ловим hard-off |

Для редких событий длительность считай по истории: если дефект выбивает раз в ~11 минут,
для 95 % шанса поймать нужно ≥3 средних интервалов (бэклог п.45).

### Запуск только в своём окне

`OCCTCmd.exe` читает клавиши (`Use Q to exit`) и при **перенаправлённом stdout** получает EOF
и тихо умирает за ~45 секунд — без ошибок, без логов, с нулевой нагрузкой (бэклог п.40).
Рабочий запуск:

```powershell
$cmd = 'start "OCCT <СЗ>" /min cmd /c ""<путь>\OCCTCmd.exe" test --schedule="<путь>" --auto-start"'
Start-Process cmd -ArgumentList '/c', $cmd -WindowStyle Hidden
```

И через 60 секунд обязательно проверь по сенсорам, что нагрузка реально пошла (CPU/GPU load
≥ 90 %) — иначе «прогон» окажется часами простоя.

Требует **прав администратора** (как и весь агент).
