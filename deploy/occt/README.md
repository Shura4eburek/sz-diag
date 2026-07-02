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
| `schedule.json` | из этого каталога (`deploy\occt\schedule.json`) — расписание Combined 5 мин + PowerSupply 5 мин |
| `*.oke` (лицензия) | лицензия OCCT Enterprise — CLI без неё не запускается |

> `OCCT.config.json` **не обязателен**: сенсоры включаются `--auto-enable-sensors=true`
> автодетектом, а тест целиком описан в `schedule.json`. Держать машинно-зависимый
> `OCCT.config.json` от другого стенда не нужно.

## schedule.json

Схема — как у расписаний в `OCCT.config.json` (объект с `ScheduleType` + `Periods[]`).
Здешний файл: два периода — `Combined` и `PowerSupply` по `00:05:00`. Потоки CPU в
конфиге стоят `Auto` (адаптируются к числу ядер), GPU-индекс `0`, память в процентах —
т.е. расписание **портируемо** между машинами. Хочешь другую длительность/набор —
поправь `Duration`/`TestType` (валидные типы: `Combined`, `PowerSupply`, `CpuOcct`,
`CpuLinpack`, `Memtest`, `Vram`, `Gpu3d`, `GpuUnreal`).

Требует **прав администратора** (как и весь агент).
