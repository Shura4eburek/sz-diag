# TPU-обогащение видях: точная плата + спеки из VGA BIOS collection

Дата: 2026-07-20

## Цель

Резолвер (`SzDiag.Hardware`) по PCI ID отдаёт вендора, чип, референс-модель и партнёра
(субвендор `1462 → MSI`) из pci.ids. Не хватает двух вещей, которых в pci.ids нет:

1. **Точная партнёрская плата (SKU)** по subsystem — из `SUBSYS_53621462` (субдевайс
   `5362`) вытащить конкретную карту, напр. «MSI RTX 5060 Ti Ventus 2x OC Plus».
2. **Спеки конкретной платы** — память (размер/тип), частоты (core/boost/mem), лимит
   мощности платы (≈TBP), видеовыходы, версия VBIOS, дата. Диагностически полезно:
   питание, «нет сигнала», оценка железки.

Источник — TechPowerUp **VGA BIOS collection** (`/vgabios/`). `IGpuScraper` был заглушкой
ровно под этот шаг; теперь прошиваем живую реализацию.

## Разведка (спайк 2026-07-20)

Щупали руками (`curl` + браузерный UA) с сервисного бокса. **Важная поправка к
первоначальной гипотезе:**

- **`gpu-specs` (каталог спеков) — за интерактивной bot-CAPTCHA.** Тело страницы —
  «Automated bot check in progress… Drag the handle to the target» (Turnstile-стиль),
  стабильно на всех попытках. Каталожные поля (шина в битах, техпроцесс, физразъёмы
  питания, дата релиза) оттуда без ручного решения капчи не достать. **Вне scope.**
- **`/vgabios/` (search + detail) — открыт, server-side HTML, без challenge.** И, что
  ключевое, отдаёт и точную плату, и реальные спеки конкретной прошивки. Значит
  **headless-браузер / Playwright не нужны** — обычный `HttpClient` + HTML-парсер.

### Что реально отдаёт vgabios

**Search-список** (`table.bioslist tbody tr`) — уже структурно, включая торговое имя:

```html
<td class="mfgr">MSI</td>
<td class="name" data-id="275654">
  <a href="/vgabios/275654/msi-rtx5060ti-16384-250315-2">RTX 5060 Ti 16 GB</a>
  <div class="cardname">Ventus 2x OC Plus</div>          <!-- точная SKU -->
</td>
<td>2025-03-15 00:00:00</td>   <!-- Date compiled -->
<td>98.06.1F.00.CD</td>        <!-- VBIOS Version -->
<td>PCI-E</td>                 <!-- Interface -->
<td>2407 / 1750 / 2602</td>    <!-- Core / Mem / Boost -->
<td>GDDR7</td>                 <!-- Memory -->
```

Фильтры формы: `manufacturer`, `model`, `memType`, `memSize`, `architecture`, `version`,
`interface`, `since`. **Subsystem-фильтра нет** → точный subsystem-матч добираем фетчем
detail-страниц кандидатов.

**Detail-страница** (`/vgabios/<id>/...`) — таблица `<tr><th>Label:</th><td>Value</td>`:

```
Manufacturer: MSI      Model: RTX 5060 Ti      Device Id: 10DE 2D04
Subsystem Id: 1462 5351      Interface: PCI-E
Memory Size: 16384 MB      Memory Type: GDDR7
GPU Clock: 2407 MHz      Boost Clock: 2602 MHz      Memory Clock: 1750 MHz
VBIOS Version: 98.06.1F.00.CD
```

Плюс свободный VBIOS-блок ниже: `Connectors 1x HDMI 3x DisplayPort`,
`Board power limit  Target: 180.0 W  Limit: 180.0 W`.

**Только на detail есть Subsystem Id** — он и есть ключ матча.

## Архитектура

Механизм — `HttpClient` + **AngleSharp** (лёгкая NuGet, CSS-селекторы; новая зависимость
только у `SzDiag.Hardware`, только на сервисном боксе — на клиента не заезжает). Живой
скрапер по vgabios реализует существующий `IGpuScraper`, расширенный одним методом
`ScrapeCardAsync`. Кэш-first ⇒ по TPU ходим только на miss.

Компоненты (каждый — один файл, одна ответственность):

- **`TechPowerUpClient.cs`** — низкоуровневый фетч: GET с браузерным UA и таймаутом,
  детект bot-challenge (`Automated bot check` / `Drag the handle` → `ScrapeBlockedException`),
  парс HTML в AngleSharp-документ. Единственное место с сетью.
- **`VgaBiosScraper.cs`** (реализует `IGpuScraper`) — `ScrapeCardAsync`: поиск в
  vgabios по производителю+модели, фетч detail-кандидатов, матч по Subsystem Id, сбор
  `ScrapedCard`. `ScrapeAsync` (device-модель) остаётся броском `NotSupportedException` —
  device-фоллбэк вне scope (pci.ids покрывает).
- **`GpuRepository.cs`** — расширяем: таблица `card`; методы `LookupCard`/`UpsertCard`.
- **`GpuResolver.cs`** — одна новая ветка miss (карта по subsystem).

`NotImplementedGpuScraper` остаётся дефолтом до прошивки живого — обратную совместимость
не ломаем.

## Интерфейс скрапера (расширение `IGpuScraper`)

```csharp
public interface IGpuScraper
{
    // существующий: device-модель, которой нет в pci.ids. Живьём не реализуем (вне scope).
    Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default);

    // новый: точная плата + спеки по subsystem из vgabios. model — имя из pci.ids для поиска.
    Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default);
}
```

Один consumer (`GpuResolver`), один injection-point — не дробим на интерфейсы.
`NotImplementedGpuScraper` реализует оба метода броском `NotSupportedException`.

## Модель данных

### DTO (`ScrapedCard.cs`)

```csharp
public sealed record ScrapedCard(
    string SubVendorId, string SubDeviceId,
    string? Manufacturer, string? CardName,
    string? MemorySize, string? MemoryType,
    string? CoreClock, string? BoostClock, string? MemoryClock,
    string? PowerTarget, string? PowerLimit,
    string? Outputs, string? DateCompiled, string? VbiosVersion,
    string SourceUrl);
```

Всё, кроме subsystem-id и `SourceUrl`, nullable — на странице может не быть части полей;
сохраняем что есть. `CardName` — точная SKU («Ventus 2x OC Plus»); при промахе матча
остаётся null.

### Схема SQLite (добавляется к vendor/device)

```sql
CREATE TABLE IF NOT EXISTS card (
    sub_vendor_id TEXT NOT NULL,
    sub_device_id TEXT NOT NULL,
    manufacturer  TEXT NULL,
    card_name     TEXT NULL,
    memory_size   TEXT NULL,
    memory_type   TEXT NULL,
    core_clock    TEXT NULL,
    boost_clock   TEXT NULL,
    memory_clock  TEXT NULL,
    power_target  TEXT NULL,
    power_limit   TEXT NULL,
    outputs       TEXT NULL,
    date_compiled TEXT NULL,
    vbios_version TEXT NULL,
    source_url    TEXT NOT NULL,
    PRIMARY KEY (sub_vendor_id, sub_device_id)
);
```

Плата+спеки — одна сущность (приходят с одной detail-страницы, ключ — subsystem). Разбивать
на `board`/`device_spec` смысла нет (один источник).

### Результат резолва (расширение `GpuResolution`)

```csharp
public sealed record GpuResolution(
    string VendorId, string? VendorName,
    string DeviceId, string? DeviceName, string? Chip, string? Model,
    string? SubVendorId, string? SubVendorName, string? SubDeviceId,
    string? Revision, GpuSource Source,
    ScrapedCard? Card);
```

Добавлены `SubDeviceId` и `Card`. `Source` (`Cache`/`Scraper`/`Unresolved`) по-прежнему про
device-модель. `Card` — независимый best-effort довесок: его отсутствие не меняет `Source`
и не роняет резолв.

## Порядок резолва (расширенный кэш-паттерн)

```
PCI ID → парсим VEN/DEV/SUBSYS/REV
  1. вендор/субвендор/device — как сейчас (БД → device-miss → ScrapeAsync(stub) → Unresolved)
  2. карта: если есть SubDeviceId → LookupCard(subven, subdev)
       hit  → отдаём
       miss → ScrapeCardAsync(id, model) → UpsertCard → отдаём   (best-effort)
```

`ScrapeCardAsync` внутри: search vgabios по `manufacturer`(из субвендора)+`model`(из
pci.ids) → перебор detail-кандидатов → матч Subsystem Id == наш `subven subdev` → сбор
`ScrapedCard`. Нет матча → возвращает null (плату не определили честно). Шаг 2 обёрнут:
`NotSupportedException` (заглушка) и `ScrapeBlockedException`/сетевые ошибки ловятся →
`Card` = null, резолв продолжается. Скрапим только на miss.

Чтобы перебор кандидатов не разросся: сначала фильтруем search по производителю (субвендор
`1462 → MSI`) и модели — обычно единицы строк; фетчим их detail и сверяем subsystem.

## CLI

`hw resolve "<PCI ID>"` — вывод расширяется секцией платы (печатается, если дорезолвлено).
Пример:

```
PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1
  Вендор:   NVIDIA Corporation (10de)
  Модель:   GeForce RTX 5060 Ti
  Чип:      GB206
  Партнёр:  Micro-Star International (MSI) (1462)
  Ревизия:  a1
  Источник: локальная база (pci.ids)
  Плата (TPU VGA BIOS):
    Карта:    MSI RTX 5060 Ti Ventus 2x OC Plus
    Память:   16384 MB GDDR7
    Частоты:  2407 / 2602 / 1750 MHz (core/boost/mem)
    Питание:  target 180.0 W, limit 180.0 W
    Выходы:   1x HDMI, 3x DisplayPort
    VBIOS:    98.06.1F.00.CD (2025-03-15)
```

Если плата не дорезолвлена (нет subsystem-матча / TPU недоступен / заглушка) — печатаем
строку-подсказку, но вендор-модель-партнёр отдаём.

Путь к БД — существующий `CliOptions.GpuDbPath`. Живой скрапер: в `Program.cs`
инстанцируем `VgaBiosScraper` вместо `NotImplementedGpuScraper` (единственная точка
подмены).

## Обработка ошибок

- **bot-challenge**: `TechPowerUpClient` проверяет HTML на маркеры (`Automated bot check`,
  `Drag the handle`, `challenge-platform`) → `ScrapeBlockedException`. Резолвер ловит →
  `Card` null, команда не падает.
- **Сеть/таймаут/404**: скрапер-метод возвращает null (или кидает, резолвер ловит) —
  graceful degradation, вендор-модель остаются.
- **Нет subsystem-матча**: `ScrapeCardAsync` → null (плату честно не определили). Не
  выдумываем: лучше «плата не определена», чем чужая SKU.
- **Парс-miss** (поле не нашлось): соответствующее поле `ScrapedCard` = null, остальное
  сохраняем. Обязательны только subsystem-id и `SourceUrl`.
- **Вежливость**: кэш-first (скрап только на genuine miss), фильтр по производителю режет
  число фетчей до единиц, браузерный UA, короткий таймаут. Массового обхода нет.

## Тестирование

Парсинг тестируем на **сохранённых HTML-фикстурах** vgabios (search-список + detail),
захваченных курлом в Task 1 и закоммиченных в тест-проект — без живой сети в CI.

- **`TechPowerUpClientTests`**: фикстура challenge-страницы → `ScrapeBlockedException`;
  нормальная страница → документ распарсен.
- **`VgaBiosParseTests`** (парсинг из фикстур, без сети — методы парсинга статические/
  internal, принимают HTML-строку): search-список → строки с mfgr/cardname/clocks/date/
  version/detail-url; detail-страница → subsystem/device/memory/clocks/power/outputs/vbios;
  отсутствующее поле → null (не падаем).
- **`GpuRepositoryTests`** (дополняем): Initialize создаёт `card`; UpsertCard + LookupCard
  (insert→update по конфликту subsystem).
- **`GpuResolverTests`** (дополняем, фейк-скрапером):
  - card-miss при наличии SubDeviceId → фейк отдаёт `ScrapedCard` → upsert → в резолве
    `Card` заполнен; повторный резолв берёт из БД (фейк не зван).
  - фейк кидает `ScrapeBlockedException`/`NotSupportedException` → `Card` null, device-часть
    цела, без исключения наружу.
  - нет SubDeviceId → скрапер не зван, `Card` null.
- **Живой integration-тест** (реально дёргает vgabios) — трейтом `[Trait("live","true")]`,
  из CI исключён, руками проверять, что селекторы не протухли.

## Вне scope (YAGNI)

- **`gpu-specs`** и его каталожные поля (шина в битах, техпроцесс, физразъёмы питания 8-pin,
  дата релиза модели) — за интерактивной CAPTCHA, не берём.
- Headless-браузер/FlareSolverr — не нужны (vgabios открыт); вернёмся, только если vgabios
  тоже закроют.
- Живой device-фоллбэк (`ScrapeAsync`) — pci.ids покрывает, остаётся заглушкой.
- Автовстраивание расшифровки в `report.md` и автозапись в KB `Компоненты/` — возможный
  следующий шаг, не сейчас.
