# Резолвер видеокарт по PCI hardware ID (pci.ids + SQLite-кэш)

Дата: 2026-07-20

## Цель

В диагностике видеокарта часто определяется только по Windows PCI hardware ID
(`PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1\...`), когда драйвера нет и модель
по-человечески не читается («Microsoft Basic Display Adapter»). Нужно по этому ID
определить: вендор, модель, GPU-чип и партнёра-производителя карты.

Разведка (см. историю решения) показала:
- **TPU за Cloudflare** с поведенческим JS-challenge — надёжный live-скрапинг требует
  headless-браузера, хрупок и против ToS. Отложен.
- **pci.ids** (pci-ids.ucw.cz) — открытая база (свободная лицензия, BSD/GPL),
  скачивается одним файлом (~1.7 МБ, HTTP 200, без защиты). Содержит 1978 NVIDIA
  device ID + AMD/Intel, вендоров и субсистемы. `2d04 → GB206 [GeForce RTX 5060 Ti]` —
  ровно то, что нужно.

Поэтому «скрапер TechPowerUp» реализуется как **резолвер PCI ID на базе pci.ids** с
SQLite-кэшем; TPU-скрапер — отложенный опциональный фоллбэк за интерфейсом-заглушкой.

## Порядок резолва (кэш-паттерн)

```
PCI ID → парсим VEN/DEV/SUBSYS/REV
      → шаг 1: lookup в нашей SQLite-БД (сидирована импортом pci.ids)
      → hit  → отдаём (вендор / модель / чип / партнёр)
      → miss → шаг 2: IGpuScraper (на этой итерации — заглушка; позже TPU)
             → шаг 3: результат скрапера пишем в SQLite → отдаём
```

Наша БД наполняется bulk-импортом pci.ids, поэтому шаг 1 работает по-настоящему сразу
(почти все карты уже там). Скрапер-фоллбэк ловит только то, чего нет даже в pci.ids
(совсем свежие карты) и — в будущем — детальные спеки, которых pci.ids не даёт.

## Выбор БД: SQLite

Уже в стеке (`SqliteSessionStore`, `Microsoft.Data.Sqlite`) — ноль новых зависимостей,
переиспользуем существующий async-паттерн. Embedded, один файл, zero-config — идеально
для локального справочника-кэша на сервисном боксе. Объём мизерный. Postgres/MySQL —
оверкилл, LiteDB — лишняя зависимость при живом SQLite.

## Новый проект: `SzDiag.Hardware`

Пятый… шестой проект в `src/` (+ зеркальные тесты). Зависит только от
`Microsoft.Data.Sqlite`. Не зависит от Hub/Agent/Kb. CLI ссылается на него для команды
`hw`. Компоненты (каждый — один файл, одна ответственность):

- **`PciId.cs`** — DTO `PciId` + статический `Parse(string)`: из Windows-строки достаёт
  `VendorId`, `DeviceId`, опц. `SubVendorId`/`SubDeviceId`, `Revision`. Все id —
  lowercase hex без префиксов.
- **`PciIdsParser.cs`** — парсер текста pci.ids в модель: вендоры (`id → name`),
  устройства (`vendorId, deviceId → name`), субсистемы (`vendorId, deviceId,
  subVendorId, subDeviceId → name`). Расщепляет имя устройства `GB206 [GeForce RTX
  5060 Ti]` на `Chip` (до скобки) и `Model` (в скобках).
- **`GpuRepository.cs`** (+ интерфейс `IGpuRepository`) — SQLite: `InitializeAsync`,
  `ImportAsync(PciIdsData)` (bulk upsert в транзакции), `LookupVendorAsync`,
  `LookupDeviceAsync`, `UpsertDeviceAsync` (шаг 3).
- **`IGpuScraper.cs`** — интерфейс шага 2 + `NotImplementedGpuScraper` (кидает
  `NotSupportedException` с внятным текстом «TPU-скрапер ещё не подключён»).
- **`GpuResolver.cs`** — оркестрация потока выше. Резолвит по частям: вендор, устройство,
  субвендор — независимо, чтобы при device-miss всё равно отдать вендора и партнёра.

## Модель данных

### DTO (`PciId.cs`)

```csharp
public sealed record PciId(
    string VendorId, string DeviceId,
    string? SubVendorId = null, string? SubDeviceId = null, string? Revision = null);
```

Формат Windows `SUBSYS_53621462`: младшее слово (`1462`) — субвендор (MSI), старшее
(`5362`) — субустройство. Парсер это учитывает.

### Результат резолва (`GpuResolver.cs`)

```csharp
public sealed record GpuResolution(
    string VendorId, string? VendorName,
    string DeviceId, string? DeviceName, string? Chip, string? Model,
    string? SubVendorId, string? SubVendorName,
    string? Revision, GpuSource Source);

public enum GpuSource { Cache, Scraper, Unresolved }
```

`Source`: `Cache` — device найден в БД; `Scraper` — дорезолвлено скрапером и записано;
`Unresolved` — device не в БД и скрапер не смог (вендор/партнёр при этом могут быть
известны).

### Схема SQLite

```sql
CREATE TABLE IF NOT EXISTS vendor (
    vendor_id TEXT PRIMARY KEY,
    name      TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS device (
    vendor_id TEXT NOT NULL,
    device_id TEXT NOT NULL,
    name      TEXT NOT NULL,
    chip      TEXT NULL,
    model     TEXT NULL,
    source    TEXT NOT NULL DEFAULT 'pci.ids',
    PRIMARY KEY (vendor_id, device_id)
);
```

Субсистемы для MVP не храним отдельной таблицей — партнёр резолвится через `vendor`
(субвендор `1462 → MSI`). Точная партнёрская саб-карта в pci.ids редка; таблицу
`subsystem` добавим, если понадобится (YAGNI сейчас). Спеки TPU (TDP/разъёмы/длина) —
будущая таблица `gpu_spec`, вне scope.

## CLI: команда `hw`

Тонкий хендлер `SzDiag.Cli/HwCommand.cs` (по образцу `KbCommand`), диспатч из
`Program.cs` (`case "hw"`). Путь к БД — `CliOptions.GpuDbPath` (default `gpu.db` рядом с
exe, резолвится от `AppContext.BaseDirectory`, как `KbRoot`). Путь к pci.ids —
`CliOptions.PciIdsPath` (default `pci.ids` рядом с exe).

- `szcli hw import [<путь к pci.ids>]` — распарсить файл и залить в БД (bulk).
  Печатает, сколько вендоров/устройств импортировано.
- `szcli hw update` — скачать свежий pci.ids с `https://pci-ids.ucw.cz/v2.2/pci.ids`
  (HttpClient GET → сохранить в `PciIdsPath` → import). Тонкая обёртка над import.
- `szcli hw resolve "<PCI ID>"` — распарсить, прогнать резолвер, напечатать: вендор,
  модель, чип, партнёр, ревизия, источник. При `Unresolved` — честно сказать, что
  device не в базе (подсказать `hw update`).

Пример вывода `resolve`:

```
PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1
  Вендор:   NVIDIA Corporation (10de)
  Модель:   GeForce RTX 5060 Ti
  Чип:      GB206
  Партнёр:  Micro-Star International (MSI) (1462)
  Ревизия:  a1
  Источник: локальная база (pci.ids)
```

## Обработка ошибок

- `PciId.Parse` при отсутствии `VEN`/`DEV` → `FormatException` с исходной строкой в
  тексте (это обязательные поля; SUBSYS/REV опциональны).
- `GpuResolver`: если скрапер кидает (заглушка) — ловим, возвращаем `Unresolved` с уже
  известными вендором/партнёром, не роняем команду.
- `hw import` при отсутствии файла → внятное сообщение с ожидаемым путём и подсказкой
  `hw update`.
- SQLite: `InitializeAsync` идемпотентен (`IF NOT EXISTS`); upsert через
  `INSERT ... ON CONFLICT ... DO UPDATE`.

## Тестирование

Зеркальный проект `tests/SzDiag.Hardware.Tests` (xUnit, temp-файл БД с `IDisposable`
cleanup — как `KbRecorderTests`). CLI-хендлер отдельными тестами не покрываем (тонкий,
как `KbCommand`); логика — в `Hardware.Tests`.

- **`PciIdParseTests`**: полная строка → VEN/DEV/SUBSYS(sub-vendor/sub-device split)/REV;
  строка без SUBSYS/REV; мусор без VEN/DEV → `FormatException`.
- **`PciIdsParserTests`**: мини-текст pci.ids (вендор + устройства + субсистема с
  табами и комментариями) → корректные вендоры/устройства; расщепление `GB206 [GeForce
  RTX 5060 Ti]` → chip=`GB206`, model=`GeForce RTX 5060 Ti`; имя без скобок → chip=имя,
  model=null.
- **`GpuRepositoryTests`**: Initialize создаёт таблицы; Import + LookupDevice возвращает
  устройство; LookupVendor; Upsert нового устройства и повторный (конфликт → update).
- **`GpuResolverTests`**: hit из БД (скрапер не зовётся — проверить фейк-скрапером, что
  не вызван); device-miss + фейк-скрапер, возвращающий модель → upsert → `Source.Scraper`
  и запись видна повторным резолвом; device-miss + `NotImplementedGpuScraper` →
  `Source.Unresolved`, но `VendorName`/`SubVendorName` заполнены, без исключения.

## Вне scope (YAGNI)

- TPU live-скрапинг (headless/Cloudflare) — отдельный будущий проект; сейчас только
  интерфейс-заглушка `IGpuScraper`.
- Детальные спеки (TDP, разъёмы питания, длина, тактовые) и таблица `gpu_spec`.
- Таблица `subsystem` для точной партнёрской саб-карты.
- Автовстраивание расшифровки в `report.md` и автозапись в KB `Компоненты/` — возможные
  следующие шаги, не сейчас.
- Определение класса устройства (GPU vs прочее) — резолвер работает с любым PCI ID,
  применяется к видяхам.
