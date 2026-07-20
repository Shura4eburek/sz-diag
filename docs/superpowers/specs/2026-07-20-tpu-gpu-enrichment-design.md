# TPU-обогащение видях: спеки модели + плата по subsystem

Дата: 2026-07-20

## Цель

Резолвер (`SzDiag.Hardware`) сейчас по PCI ID отдаёт вендора, чип, референс-модель и
партнёра (субвендор `1462 → MSI`) из pci.ids. Не хватает двух вещей, которых в pci.ids
нет:

1. **Спеки референс-модели** (надёжная часть) — память (размер/тип/шина), техпроцесс,
   TBP, **разъёмы питания**, видеовыходы, дата выхода. Диагностически полезно: питание,
   «нет сигнала», оценка поколения.
2. **Точная плата по subsystem** (best-effort) — из `SUBSYS_53621462` (субдевайс `5362`)
   вытащить конкретную партнёрскую SKU (напр. «MSI Ventus»). Источник неструктурный,
   поэтому честно best-effort.

Источник — TechPowerUp. `IGpuScraper` был заглушкой ровно под этот шаг; теперь прошиваем
живую реализацию.

## Разведка (спайк 2026-07-20)

Щупали руками (`curl` + браузерный UA) с сервисного бокса:

- **Cloudflare не блочит.** `gpu-specs` и `vgabios` отдают HTTP 200 с полным HTML, без
  JS-challenge. Прошлый затык был в fetch-инфре Anthropic, а не в самом боксе. Значит
  **headless-браузер / Playwright / FlareSolverr не нужны** — хватает `HttpClient` +
  HTML-парсер. (Оговорка: Cloudflare может закрутить гайки позже — держим за интерфейсом
  и детектим challenge, см. «Обработка ошибок».)
- **Спеки — server-side.** На `gpu-specs`-странице TDP/шина/память лежат прямо в
  разметке, парсятся. URL угадывать нельзя (`.c4293` увёл на чужую карту) — резолвим
  через поиск/список gpu-specs.
- **subsystem→плата существует** — VGA BIOS collection. Детальная страница прошивки
  (`/vgabios/275654/...`) отдала: `Subsystem Id: 1462 5351`, `Device Id: 0x10DE 0x2D04`,
  `Manufacturer: MSI`, `Model: RTX 5060 Ti` + «Ventus» в теле. Точная торговая SKU лежит
  в теле/имени файла неструктурно → best-effort.

## Архитектура

Механизм — `HttpClient` + **AngleSharp** (лёгкая NuGet, CSS-селекторы; новая зависимость
только у `SzDiag.Hardware`, только на сервисном боксе, на клиента не заезжает). Живой
скрапер по TPU реализует уже существующий `IGpuScraper`, расширенный двумя методами.
Кэш-first ⇒ по TPU ходим только на miss — нагрузки почти нет.

Компоненты (каждый — один файл, одна ответственность):

- **`TechPowerUpClient.cs`** — низкоуровневый фетч: GET с браузерным UA и таймаутом,
  детект Cloudflare-challenge (кидает `ScrapeBlockedException`), общий хелпер загрузки
  HTML в AngleSharp-документ. Единственное место с сетью.
- **`TechPowerUpScraper.cs`** (реализует `IGpuScraper`) — три метода (см. ниже): парсинг
  gpu-specs и vgabios через `TechPowerUpClient` + AngleSharp-селекторы.
- **`GpuRepository.cs`** — расширяем: таблицы `device_spec`, `board`; методы
  `LookupSpec/UpsertSpec`, `LookupBoard/UpsertBoard`.
- **`GpuResolver.cs`** — две новые ветки miss (спеки, плата).

`NotImplementedGpuScraper` остаётся дефолтом до прошивки живого — обратную совместимость
не ломаем.

## Интерфейс скрапера (расширение `IGpuScraper`)

```csharp
public interface IGpuScraper
{
    // существующий: дорезолвить модель устройства, которого нет в pci.ids
    Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default);

    // новый: спеки референс-модели по vendor/device (+ известное имя модели для поиска)
    Task<GpuSpecs?> ScrapeSpecsAsync(string vendorId, string deviceId, string? model,
        CancellationToken ct = default);

    // новый: точная плата по subsystem (best-effort)
    Task<GpuBoard?> ScrapeBoardAsync(PciId id, CancellationToken ct = default);
}
```

Одна когезивная ответственность («достать из внешнего источника»), один consumer
(`GpuResolver`), один injection-point — поэтому не дробим на три интерфейса.
`NotImplementedGpuScraper` реализует все три метода броском `NotSupportedException`.

## Модель данных

### DTO

```csharp
public sealed record GpuSpecs(
    string? Chip, string? Model,
    string? MemorySize, string? MemoryType, string? MemoryBus,
    string? ProcessNode, string? Tbp, string? PowerConnectors,
    string? Outputs, string? ReleaseDate,
    string SourceUrl);

public sealed record GpuBoard(
    string SubVendorId, string SubDeviceId,
    string? Manufacturer, string? BoardName,
    string? DeviceId, string SourceUrl);
```

Все поля спеков nullable — на странице может не быть части данных; сохраняем что есть.
`GpuBoard.BoardName` — best-effort (может остаться null, если торговое имя не выцепилось;
`Manufacturer` при этом обычно известен).

### Схема SQLite (добавляется к vendor/device)

```sql
CREATE TABLE IF NOT EXISTS device_spec (
    vendor_id        TEXT NOT NULL,
    device_id        TEXT NOT NULL,
    memory_size      TEXT NULL,
    memory_type      TEXT NULL,
    memory_bus       TEXT NULL,
    process_node     TEXT NULL,
    tbp              TEXT NULL,
    power_connectors TEXT NULL,
    outputs          TEXT NULL,
    release_date     TEXT NULL,
    source_url       TEXT NOT NULL,
    PRIMARY KEY (vendor_id, device_id)
);
CREATE TABLE IF NOT EXISTS board (
    sub_vendor_id TEXT NOT NULL,
    sub_device_id TEXT NOT NULL,
    device_id     TEXT NULL,
    manufacturer  TEXT NULL,
    board_name    TEXT NULL,
    source_url    TEXT NOT NULL,
    PRIMARY KEY (sub_vendor_id, sub_device_id)
);
```

Спеки — на референс-модель (ключ `vendor_id+device_id`), одинаковы для всех плат чипа.
Плата — на subsystem (`sub_vendor_id+sub_device_id`). Разные TBP конкретных плат Ventus
vs Gaming не храним (глубоко, YAGNI) — TBP берём референсный.

### Результат резолва (расширение `GpuResolution`)

```csharp
public sealed record GpuResolution(
    string VendorId, string? VendorName,
    string DeviceId, string? DeviceName, string? Chip, string? Model,
    string? SubVendorId, string? SubVendorName, string? SubDeviceId,
    string? Revision, GpuSource Source,
    GpuSpecs? Specs, GpuBoard? Board);
```

Добавлены `SubDeviceId`, `Specs`, `Board`. `Source` (`Cache`/`Scraper`/`Unresolved`)
по-прежнему про device-модель. Спеки/плата — независимые best-effort довески: их
отсутствие не меняет `Source` и не роняет резолв.

## Порядок резолва (расширенный кэш-паттерн)

```
PCI ID → парсим VEN/DEV/SUBSYS/REV
  1. вендор/субвендор/device — как сейчас (БД → device-miss → ScrapeAsync → upsert)
  2. спеки: LookupSpec(ven,dev)
       hit  → отдаём
       miss → ScrapeSpecsAsync → UpsertSpec → отдаём   (best-effort)
  3. плата: если есть SubDeviceId → LookupBoard(subven,subdev)
       hit  → отдаём
       miss → ScrapeBoardAsync → UpsertBoard → отдаём   (best-effort)
```

Шаги 2–3 обёрнуты так, что `NotSupportedException` (заглушка) и `ScrapeBlockedException`
(Cloudflare)/сетевые ошибки ловятся → соответствующее поле остаётся null, резолв
продолжается. Скрапим только на miss — повторный резолв берёт из БД, по TPU не ходит.

## CLI

`hw resolve "<PCI ID>"` — вывод расширяется секциями спеков и платы (печатаются, если
дорезолвлено). Пример:

```
PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1
  Вендор:   NVIDIA Corporation (10de)
  Модель:   GeForce RTX 5060 Ti
  Чип:      GB206
  Партнёр:  Micro-Star International (MSI) (1462)
  Плата:    MSI RTX 5060 Ti Ventus            (best-effort, TPU VGA BIOS)
  Ревизия:  a1
  Источник: локальная база (pci.ids)
  Спеки (TPU):
    Память:   16 GB GDDR7, 128-bit
    Техпроц.: 5 nm
    TBP:      180 W, 1x 8-pin
    Выходы:   3x DP 2.1, 1x HDMI 2.1
    Выход:    2025-04
```

Если TPU недоступен (Cloudflare/сеть) или заглушка — печатаем строку-подсказку, что
спеки/плата не дорезолвлены (`hw update` / позже), но вендор-модель-партнёр отдаём.

Путь к БД — существующий `CliOptions.GpuDbPath`. Флаг живого скрапера: в CLI
инстанцируем `TechPowerUpScraper` вместо `NotImplementedGpuScraper` (единственная точка
подмены в `Program.cs`).

## Обработка ошибок

- **Cloudflare-challenge**: `TechPowerUpClient` проверяет HTML на маркеры (`just a
  moment`, `challenge-platform`, `cf-mitigated`) → `ScrapeBlockedException`. Резолвер
  ловит → best-effort поле null, команда не падает.
- **Сеть/таймаут/404**: скрапер-метод возвращает null (или кидает, резолвер ловит) —
  graceful degradation, вендор-модель остаются.
- **Парс-miss** (поле не нашлось на странице): соответствующее поле `GpuSpecs`/`GpuBoard`
  = null, остальное сохраняем. Ни одно поле не обязательно, кроме `SourceUrl`.
- **Вежливость**: кэш-first (скрап только на genuine miss), одиночные запросы, браузерный
  UA, короткий таймаут. Массового обхода нет.

## Тестирование

Парсинг тестируем на **сохранённых HTML-фикстурах** из спайка (кладём в тест-проект) —
без живой сети в CI.

- **`TechPowerUpScraperTests`**: фикстура gpu-specs → корректно выцепляет память/техпроц/
  TBP/разъёмы/выходы/дату; фикстура vgabios-detail → `Subsystem Id`/`Manufacturer`/
  board-name; фикстура challenge-страницы → `ScrapeBlockedException`; отсутствующее поле
  → null (не падаем).
- **`GpuRepositoryTests`** (дополняем): Initialize создаёт `device_spec`/`board`;
  UpsertSpec + LookupSpec (insert→update по конфликту); UpsertBoard + LookupBoard.
- **`GpuResolverTests`** (дополняем, фейк-скрапером):
  - spec-miss → фейк отдаёт `GpuSpecs` → upsert → в резолве `Specs` заполнены; повторный
    резолв берёт спеки из БД (фейк не зван для спеков).
  - board-miss при наличии SubDeviceId → фейк отдаёт `GpuBoard` → upsert → `Board`
    заполнен.
  - скрапер кидает `ScrapeBlockedException`/`NotSupportedException` → `Specs`/`Board`
    null, device-часть цела, без исключения наружу.
- **Живой integration-тест** (реально дёргает TPU) — отдельным трейтом `[Trait("live",
  "true")]`, из CI исключён, гоняется руками для проверки, что селекторы не протухли.

## Вне scope (YAGNI)

- Точный разбор торгового имени сверх best-effort (полноценный «Ventus 2X OC 16G»).
- Headless-браузер/FlareSolverr как фоллбэк — только если Cloudflare реально закрутит
  гайки (пока не нужно, curl проходит).
- Пер-платные спеки (реальный TBP конкретной Ventus vs Gaming) — храним референсные.
- Полный дамп спеков (частоты, CUDA-ядра, ROP/TMU, FLOPS, длина платы) — берём
  диагностический минимум.
- Автовстраивание расшифровки в `report.md` и автозапись в KB `Компоненты/` — возможный
  следующий шаг, не сейчас.
