# TPU-обогащение видях (VGA BIOS) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** По PCI ID дорезолвивать точную партнёрскую плату (SKU) и её спеки из TechPowerUp VGA BIOS collection, кэшируя в `gpu.db`.

**Architecture:** Кэш-first. `GpuResolver` при наличии subsystem-id и промахе в БД зовёт `VgaBiosScraper.ScrapeCardAsync`: тот через `TechPowerUpClient` (HttpClient + детект bot-challenge) тянет search-список vgabios, фетчит detail-кандидатов и матчит по Subsystem Id, парсит через `VgaBiosParser` (AngleSharp, чистый, без сети). Результат пишется в таблицу `card`. Парсинг отделён от сети → тестируется на сохранённых HTML-фикстурах.

**Tech Stack:** net8.0, xUnit, Microsoft.Data.Sqlite, AngleSharp 1.1.2, HttpClient.

**Спека:** `docs/superpowers/specs/2026-07-20-tpu-gpu-enrichment-design.md` — читать перед стартом.

**Пре-реквизиты:** работаем в ветке `feat/hw-tpu-enrichment` (уже создана). Все команды — из корня репо `C:\Users\ENDI\RiderProjects\sz-diag`. Тесты гонять: `dotnet test tests/SzDiag.Hardware.Tests`.

---

## Карта файлов

**Создать:**
- `src/SzDiag.Hardware/ScrapeBlockedException.cs` — исключение bot-challenge.
- `src/SzDiag.Hardware/TechPowerUpClient.cs` — фетч HTML + детект challenge.
- `src/SzDiag.Hardware/VgaBiosParser.cs` — чистый парсер (search + detail) на AngleSharp + DTO `VgaBiosRow`, `VgaBiosDetail`.
- `src/SzDiag.Hardware/ScrapedCard.cs` — DTO результата скрапа карты.
- `src/SzDiag.Hardware/VgaBiosScraper.cs` — `IGpuScraper`-реализация (оркестрация).
- `tests/SzDiag.Hardware.Tests/fixtures/{vgabios-search.html,vgabios-detail.html,gpu-specs-challenge.html}` — фикстуры.
- `tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs`
- `tests/SzDiag.Hardware.Tests/TechPowerUpClientTests.cs`

**Изменить:**
- `src/SzDiag.Hardware/SzDiag.Hardware.csproj` — пакет AngleSharp.
- `src/SzDiag.Hardware/IGpuScraper.cs` — метод `ScrapeCardAsync`; `NotImplementedGpuScraper` реализует его.
- `src/SzDiag.Hardware/IGpuRepository.cs` + `GpuRepository.cs` — таблица `card`, `LookupCardAsync`/`UpsertCardAsync`.
- `src/SzDiag.Hardware/GpuResolver.cs` — `GpuResolution` (+`SubDeviceId`, +`Card`), ветка card-miss.
- `src/SzDiag.Cli/Program.cs` — инстанс `VgaBiosScraper`.
- `src/SzDiag.Cli/HwCommand.cs` — секция «Плата» в выводе `resolve`.
- `tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj` — копирование фикстур в output.
- `tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs` — тесты `card`.
- `tests/SzDiag.Hardware.Tests/GpuResolverTests.cs` — тесты card-ветки + обновить `FakeScraper`.
- `CLAUDE.md` — описание vgabios-обогащения.

---

## Task 1: Зависимость AngleSharp + захват фикстур

**Files:**
- Modify: `src/SzDiag.Hardware/SzDiag.Hardware.csproj`
- Modify: `tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj`
- Create: `tests/SzDiag.Hardware.Tests/fixtures/vgabios-search.html`
- Create: `tests/SzDiag.Hardware.Tests/fixtures/vgabios-detail.html`
- Create: `tests/SzDiag.Hardware.Tests/fixtures/gpu-specs-challenge.html`

- [ ] **Step 1: Добавить пакет AngleSharp в Hardware**

В `src/SzDiag.Hardware/SzDiag.Hardware.csproj` внутри существующего (или нового) `<ItemGroup>` добавить:

```xml
  <ItemGroup>
    <PackageReference Include="AngleSharp" Version="1.1.2" />
  </ItemGroup>
```

- [ ] **Step 2: Захватить фикстуры курлом**

Запустить (Bash-инструмент). vgabios открыт, отдаёт server-side HTML; gpu-specs отдаёт challenge-страницу — все три нужны как фикстуры.

```bash
FX="tests/SzDiag.Hardware.Tests/fixtures"
mkdir -p "$FX"
UA="Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36"
curl -s -A "$UA" "https://www.techpowerup.com/vgabios/?manufacturer=MSI&model=RTX+5060+Ti" -o "$FX/vgabios-search.html"
curl -s -A "$UA" "https://www.techpowerup.com/vgabios/275654/msi-rtx5060ti-16384-250315-2" -o "$FX/vgabios-detail.html"
curl -s -A "$UA" "https://www.techpowerup.com/gpu-specs/geforce-rtx-5060-ti.c4293" -o "$FX/gpu-specs-challenge.html"
```

Проверить (Bash), что фикстуры валидные:

```bash
FX="tests/SzDiag.Hardware.Tests/fixtures"
grep -c 'class="bioslist"' "$FX/vgabios-search.html"      # ожидаем 1
grep -c 'Subsystem Id' "$FX/vgabios-detail.html"          # ожидаем >=1
grep -c 'Automated bot check\|Drag the handle' "$FX/gpu-specs-challenge.html"  # ожидаем >=1
```

Expected: первый — `1`, второй — `>=1`, третий — `>=1`. Если vgabios-search отдал 0 (разметка сменилась) — остановиться и пересмотреть селекторы.

- [ ] **Step 3: Копировать фикстуры в output тест-проекта**

В `tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj` добавить `<ItemGroup>`:

```xml
  <ItemGroup>
    <None Include="fixtures\**\*.html" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 4: Собрать — проверить, что пакет и фикстуры на месте**

Run: `dotnet build tests/SzDiag.Hardware.Tests`
Expected: BUILD SUCCEEDED.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Hardware/SzDiag.Hardware.csproj tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj tests/SzDiag.Hardware.Tests/fixtures
git commit -m "build(hardware): AngleSharp + HTML-фикстуры vgabios для тестов парсинга"
```

---

## Task 2: `ScrapeBlockedException` + `TechPowerUpClient` (детект challenge)

**Files:**
- Create: `src/SzDiag.Hardware/ScrapeBlockedException.cs`
- Create: `src/SzDiag.Hardware/TechPowerUpClient.cs`
- Create: `tests/SzDiag.Hardware.Tests/TechPowerUpClientTests.cs`

- [ ] **Step 1: Написать исключение**

`src/SzDiag.Hardware/ScrapeBlockedException.cs`:

```csharp
namespace SzDiag.Hardware;

/// <summary>TPU вернул bot-challenge вместо страницы. Резолвер ловит и деградирует мягко.</summary>
public sealed class ScrapeBlockedException : Exception
{
    public ScrapeBlockedException(string message) : base(message) { }
}
```

- [ ] **Step 2: Написать падающий тест на детект challenge**

`tests/SzDiag.Hardware.Tests/TechPowerUpClientTests.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class TechPowerUpClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void EnsureNotBlocked_ChallengePage_Throws()
    {
        var html = Fixture("gpu-specs-challenge.html");
        Assert.Throws<ScrapeBlockedException>(() => TechPowerUpClient.EnsureNotBlocked(html));
    }

    [Fact]
    public void EnsureNotBlocked_NormalPage_Passes()
    {
        var html = Fixture("vgabios-detail.html");
        TechPowerUpClient.EnsureNotBlocked(html);   // не должно кинуть
    }
}
```

- [ ] **Step 3: Прогнать — убедиться, что не компилится/падает**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~TechPowerUpClientTests`
Expected: FAIL — `TechPowerUpClient` не существует.

- [ ] **Step 4: Написать `TechPowerUpClient`**

`src/SzDiag.Hardware/TechPowerUpClient.cs`:

```csharp
using System.Net.Http;

namespace SzDiag.Hardware;

/// <summary>Единственное место с сетью к TPU. Тянет HTML браузерным UA и ловит bot-challenge.
/// Парсинг — в VgaBiosParser (чистый, тестируется на фикстурах).</summary>
public sealed class TechPowerUpClient
{
    private const string Ua =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly string[] ChallengeMarkers =
        { "Automated bot check", "Drag the handle", "challenge-platform" };

    private readonly HttpClient _http;

    public TechPowerUpClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(Ua))
            _http.DefaultRequestHeaders.Add("User-Agent", Ua);
    }

    /// <summary>GET страницы TPU. Кидает <see cref="ScrapeBlockedException"/>, если это challenge.</summary>
    public async Task<string> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        var html = await _http.GetStringAsync(url, ct);
        EnsureNotBlocked(html);
        return html;
    }

    /// <summary>Проверка HTML на маркеры bot-challenge. Статик — чтобы тестировать на фикстуре.</summary>
    public static void EnsureNotBlocked(string html)
    {
        foreach (var m in ChallengeMarkers)
            if (html.Contains(m, StringComparison.OrdinalIgnoreCase))
                throw new ScrapeBlockedException($"TPU вернул bot-challenge (маркер: «{m}»)");
    }
}
```

- [ ] **Step 5: Прогнать — зелёные**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~TechPowerUpClientTests`
Expected: PASS (2 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hardware/ScrapeBlockedException.cs src/SzDiag.Hardware/TechPowerUpClient.cs tests/SzDiag.Hardware.Tests/TechPowerUpClientTests.cs
git commit -m "feat(hardware): TechPowerUpClient с детектом bot-challenge TPU"
```

---

## Task 3: `VgaBiosParser.ParseSearch` (парс search-списка)

**Files:**
- Create: `src/SzDiag.Hardware/VgaBiosParser.cs`
- Create: `tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs`

- [ ] **Step 1: Написать падающий тест на парс списка**

`tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class VgaBiosParseTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void ParseSearch_ReturnsRowsWithCardNameAndUrl()
    {
        var rows = VgaBiosParser.ParseSearch(Fixture("vgabios-search.html"));

        Assert.NotEmpty(rows);
        var r = rows.First(x => x.DetailUrl.Contains("275654"));
        Assert.Equal("MSI", r.Manufacturer);
        Assert.Equal("Ventus 2x OC Plus", r.CardName);
        Assert.StartsWith("/vgabios/275654", r.DetailUrl);
        Assert.Equal("2025-03-15", r.DateCompiled);        // дата без времени
        Assert.Equal("98.06.1F.00.CD", r.VbiosVersion);
    }
}
```

- [ ] **Step 2: Прогнать — падает (нет VgaBiosParser)**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~VgaBiosParseTests`
Expected: FAIL — `VgaBiosParser` не существует.

- [ ] **Step 3: Написать `VgaBiosParser` c `ParseSearch` + DTO**

`src/SzDiag.Hardware/VgaBiosParser.cs`:

```csharp
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace SzDiag.Hardware;

/// <summary>Строка search-списка vgabios: производитель, торговое имя карты, ссылка на detail.</summary>
public sealed record VgaBiosRow(
    string Manufacturer, string Model, string CardName, string DetailUrl,
    string? DateCompiled, string? VbiosVersion, string? MemoryType);

/// <summary>Разобранная detail-страница прошивки.</summary>
public sealed record VgaBiosDetail(
    string? SubVendorId, string? SubDeviceId,
    string? MemorySize, string? MemoryType,
    string? CoreClock, string? BoostClock, string? MemoryClock,
    string? PowerTarget, string? PowerLimit, string? Outputs,
    string? VbiosVersion);

/// <summary>Чистый парсер HTML vgabios (AngleSharp, без сети). Публичный статик — как PciIdsParser.</summary>
public static class VgaBiosParser
{
    private static readonly HtmlParser Html = new();

    public static IReadOnlyList<VgaBiosRow> ParseSearch(string html)
    {
        var doc = Html.ParseDocument(html);
        var rows = new List<VgaBiosRow>();
        foreach (var tr in doc.QuerySelectorAll("table.bioslist tbody tr"))
        {
            var link = tr.QuerySelector("td.name a");
            if (link is null) continue;
            var url = link.GetAttribute("href") ?? "";
            var mfgr = tr.QuerySelector("td.mfgr")?.TextContent.Trim() ?? "";
            var model = link.TextContent.Trim();
            var cardName = tr.QuerySelector("td.name div.cardname")?.TextContent.Trim() ?? "";
            var tds = tr.QuerySelectorAll("td");
            // колонки: 0=mfgr 1=name 2=Date compiled 3=Version 4=Interface 5=Core/Mem/Boost 6=Memory 7=Links
            string? Cell(int i) => tds.Length > i ? tds[i].TextContent.Trim() : null;
            var date = Cell(2)?.Split(' ')[0];                 // «2025-03-15 00:00:00» → «2025-03-15»
            rows.Add(new VgaBiosRow(mfgr, model, cardName, url, date, Cell(3), Cell(6)));
        }
        return rows;
    }
}
```

- [ ] **Step 4: Прогнать — зелёные**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~VgaBiosParseTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Hardware/VgaBiosParser.cs tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs
git commit -m "feat(hardware): VgaBiosParser.ParseSearch — строки списка с торговым именем карты"
```

---

## Task 4: `VgaBiosParser.ParseDetail` (парс detail-страницы)

**Files:**
- Modify: `src/SzDiag.Hardware/VgaBiosParser.cs`
- Modify: `tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs`

- [ ] **Step 1: Дописать падающий тест на detail**

Добавить в `VgaBiosParseTests.cs` метод:

```csharp
    [Fact]
    public void ParseDetail_ExtractsSubsystemMemoryClocksPower()
    {
        var d = VgaBiosParser.ParseDetail(Fixture("vgabios-detail.html"));

        Assert.Equal("1462", d.SubVendorId);   // нормализовано в lowercase
        Assert.Equal("5351", d.SubDeviceId);
        Assert.Equal("16384 MB", d.MemorySize);
        Assert.Equal("GDDR7", d.MemoryType);
        Assert.Equal("2407 MHz", d.CoreClock);
        Assert.Equal("2602 MHz", d.BoostClock);
        Assert.Equal("1750 MHz", d.MemoryClock);
        Assert.Equal("180.0 W", d.PowerTarget);
        Assert.Equal("180.0 W", d.PowerLimit);
        Assert.Contains("HDMI", d.Outputs);
        Assert.Contains("DisplayPort", d.Outputs);
        Assert.Equal("98.06.1F.00.CD", d.VbiosVersion);
    }
```

- [ ] **Step 2: Прогнать — падает (нет ParseDetail)**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~ParseDetail`
Expected: FAIL — метод не существует.

- [ ] **Step 3: Дописать `ParseDetail` в `VgaBiosParser`**

Добавить в класс `VgaBiosParser`:

```csharp
    public static VgaBiosDetail ParseDetail(string html)
    {
        var doc = Html.ParseDocument(html);

        // Таблица «Graphics Card Info»: <tr><th>Label:</th><td>Value</td></tr>
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in doc.QuerySelectorAll("table tr"))
        {
            var th = tr.QuerySelector("th");
            var td = tr.QuerySelector("td");
            if (th is null || td is null) continue;
            var key = th.TextContent.Trim().TrimEnd(':').Trim();
            if (!map.ContainsKey(key)) map[key] = td.TextContent.Trim();
        }
        string? Get(string k) => map.TryGetValue(k, out var v) && v.Length > 0 ? v : null;

        // Subsystem Id: «1462 5351» → subven / subdev (lowercase hex)
        string? subVen = null, subDev = null;
        var sub = Get("Subsystem Id");
        if (sub is not null)
        {
            var parts = sub.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) { subVen = parts[0].ToLowerInvariant(); subDev = parts[1].ToLowerInvariant(); }
        }

        // Свободный VBIOS-блок: выходы и лимиты мощности — регулярками по тексту тела.
        var body = doc.Body?.TextContent ?? "";
        static string? Rx(string text, string pattern) =>
            System.Text.RegularExpressions.Regex.Match(text, pattern) is { Success: true } m
                ? m.Groups[1].Value.Trim() : null;

        var outputs = Rx(body, @"Connectors\s+(.+?)\s+Board power limit");
        var target = Rx(body, @"Target:\s*([\d.]+\s*W)");
        var limit  = Rx(body, @"Limit:\s*([\d.]+\s*W)");

        return new VgaBiosDetail(
            subVen, subDev,
            Get("Memory Size"), Get("Memory Type"),
            Get("GPU Clock"), Get("Boost Clock"), Get("Memory Clock"),
            target, limit, outputs,
            Get("VBIOS Version"));
    }
```

- [ ] **Step 4: Прогнать — зелёные**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~VgaBiosParseTests`
Expected: PASS (оба теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Hardware/VgaBiosParser.cs tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs
git commit -m "feat(hardware): VgaBiosParser.ParseDetail — subsystem/память/частоты/питание/выходы"
```

---

## Task 5: Таблица `card` + `LookupCardAsync`/`UpsertCardAsync` в репозитории

**Files:**
- Create: `src/SzDiag.Hardware/ScrapedCard.cs`
- Modify: `src/SzDiag.Hardware/IGpuRepository.cs`
- Modify: `src/SzDiag.Hardware/GpuRepository.cs`
- Modify: `tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs`

- [ ] **Step 1: Написать DTO `ScrapedCard`**

`src/SzDiag.Hardware/ScrapedCard.cs`:

```csharp
namespace SzDiag.Hardware;

/// <summary>Партнёрская плата + спеки из TPU VGA BIOS. Ключ — subsystem (subven/subdev).</summary>
public sealed record ScrapedCard(
    string SubVendorId, string SubDeviceId,
    string? Manufacturer, string? CardName,
    string? MemorySize, string? MemoryType,
    string? CoreClock, string? BoostClock, string? MemoryClock,
    string? PowerTarget, string? PowerLimit,
    string? Outputs, string? DateCompiled, string? VbiosVersion,
    string SourceUrl);
```

- [ ] **Step 2: Расширить интерфейс репозитория**

В `src/SzDiag.Hardware/IGpuRepository.cs` добавить в интерфейс:

```csharp
    Task<ScrapedCard?> LookupCardAsync(string subVendorId, string subDeviceId, CancellationToken ct = default);
    Task UpsertCardAsync(ScrapedCard card, CancellationToken ct = default);
```

- [ ] **Step 3: Написать падающие тесты репозитория**

Добавить в `tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs`:

```csharp
    private static ScrapedCard SampleCard(string? name = "Ventus 2x OC Plus") => new(
        "1462", "5351", "MSI", name,
        "16384 MB", "GDDR7", "2407 MHz", "2602 MHz", "1750 MHz",
        "180.0 W", "180.0 W", "1x HDMI, 3x DisplayPort", "2025-03-15", "98.06.1F.00.CD",
        "https://www.techpowerup.com/vgabios/275654/");

    [Fact]
    public async Task UpsertCard_ThenLookup_ReturnsCard()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.UpsertCardAsync(SampleCard());

        var card = await repo.LookupCardAsync("1462", "5351");
        Assert.NotNull(card);
        Assert.Equal("Ventus 2x OC Plus", card!.CardName);
        Assert.Equal("180.0 W", card.PowerTarget);
        Assert.Contains("HDMI", card.Outputs);
    }

    [Fact]
    public async Task LookupCard_Missing_ReturnsNull()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        Assert.Null(await repo.LookupCardAsync("1462", "ffff"));
    }

    [Fact]
    public async Task UpsertCard_Twice_Updates()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.UpsertCardAsync(SampleCard("Old Name"));
        await repo.UpsertCardAsync(SampleCard("Ventus 2x OC Plus"));
        var card = await repo.LookupCardAsync("1462", "5351");
        Assert.Equal("Ventus 2x OC Plus", card!.CardName);
    }
```

- [ ] **Step 4: Прогнать — падает (нет методов/таблицы)**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuRepositoryTests`
Expected: FAIL — `LookupCardAsync`/`UpsertCardAsync` не существуют.

- [ ] **Step 5: Добавить таблицу `card` в `InitializeAsync`**

В `src/SzDiag.Hardware/GpuRepository.cs`, в `InitializeAsync`, дополнить `cmd.CommandText` (внутри той же строки-скрипта, после `device`):

```csharp
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

- [ ] **Step 6: Реализовать `UpsertCardAsync` и `LookupCardAsync`**

Добавить в класс `GpuRepository`:

```csharp
    public async Task UpsertCardAsync(ScrapedCard c, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO card (sub_vendor_id, sub_device_id, manufacturer, card_name,
                memory_size, memory_type, core_clock, boost_clock, memory_clock,
                power_target, power_limit, outputs, date_compiled, vbios_version, source_url)
            VALUES ($sv,$sd,$mf,$cn,$ms,$mt,$cc,$bc,$mc,$pt,$pl,$out,$dc,$vb,$url)
            ON CONFLICT(sub_vendor_id, sub_device_id) DO UPDATE SET
                manufacturer=excluded.manufacturer, card_name=excluded.card_name,
                memory_size=excluded.memory_size, memory_type=excluded.memory_type,
                core_clock=excluded.core_clock, boost_clock=excluded.boost_clock,
                memory_clock=excluded.memory_clock, power_target=excluded.power_target,
                power_limit=excluded.power_limit, outputs=excluded.outputs,
                date_compiled=excluded.date_compiled, vbios_version=excluded.vbios_version,
                source_url=excluded.source_url;
            """;
        void P(string n, string? v) => cmd.Parameters.AddWithValue(n, (object?)v ?? DBNull.Value);
        P("$sv", c.SubVendorId); P("$sd", c.SubDeviceId); P("$mf", c.Manufacturer); P("$cn", c.CardName);
        P("$ms", c.MemorySize); P("$mt", c.MemoryType); P("$cc", c.CoreClock); P("$bc", c.BoostClock);
        P("$mc", c.MemoryClock); P("$pt", c.PowerTarget); P("$pl", c.PowerLimit); P("$out", c.Outputs);
        P("$dc", c.DateCompiled); P("$vb", c.VbiosVersion); P("$url", c.SourceUrl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ScrapedCard?> LookupCardAsync(string subVendorId, string subDeviceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT manufacturer, card_name, memory_size, memory_type, core_clock, boost_clock,
                   memory_clock, power_target, power_limit, outputs, date_compiled, vbios_version, source_url
            FROM card WHERE sub_vendor_id = $sv AND sub_device_id = $sd;
            """;
        cmd.Parameters.AddWithValue("$sv", subVendorId);
        cmd.Parameters.AddWithValue("$sd", subDeviceId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new ScrapedCard(subVendorId, subDeviceId,
            S(0), S(1), S(2), S(3), S(4), S(5), S(6), S(7), S(8), S(9), S(10), S(11), S(12)!);
    }
```

- [ ] **Step 7: Прогнать — зелёные**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuRepositoryTests`
Expected: PASS (старые + 3 новых).

- [ ] **Step 8: Commit**

```bash
git add src/SzDiag.Hardware/ScrapedCard.cs src/SzDiag.Hardware/IGpuRepository.cs src/SzDiag.Hardware/GpuRepository.cs tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs
git commit -m "feat(hardware): таблица card + Lookup/UpsertCard (плата по subsystem)"
```

---

## Task 6: Расширить `IGpuScraper` методом `ScrapeCardAsync`

**Files:**
- Modify: `src/SzDiag.Hardware/IGpuScraper.cs`

- [ ] **Step 1: Добавить метод в интерфейс и заглушку**

Переписать `src/SzDiag.Hardware/IGpuScraper.cs`:

```csharp
namespace SzDiag.Hardware;

/// <summary>Дорезолв из внешнего источника (TPU). Реализация — VgaBiosScraper.</summary>
public interface IGpuScraper
{
    /// <summary>Device-модель, которой нет в pci.ids. Вне scope — остаётся заглушкой.</summary>
    Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default);

    /// <summary>Точная плата + спеки по subsystem из vgabios. model — имя из pci.ids для поиска.</summary>
    Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default);
}

/// <summary>Заглушка: живой скрапер не подключён. Резолвер ловит и отдаёт без дорезолва.</summary>
public sealed class NotImplementedGpuScraper : IGpuScraper
{
    public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        => throw new NotSupportedException("TPU-скрапер device-модели не подключён; обнови базу через `hw update`");

    public Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default)
        => throw new NotSupportedException("TPU vgabios-скрапер не подключён");
}
```

- [ ] **Step 2: Собрать src — зелёное (заглушка реализует оба метода, резолвер ещё не зовёт новый)**

Run: `dotnet build src/SzDiag.Hardware`
Expected: BUILD SUCCEEDED. (Тест-проект пока НЕ собираем — `FakeScraper` починим в Task 7 вместе с card-веткой.)

- [ ] **Step 3: Commit (только src, тесты обновим следующей задачей)**

```bash
git add src/SzDiag.Hardware/IGpuScraper.cs
git commit -m "feat(hardware): IGpuScraper.ScrapeCardAsync + заглушка"
```

---

## Task 7: `GpuResolution` (+`SubDeviceId`, +`Card`) и ветка card-miss в резолвере

**Files:**
- Modify: `src/SzDiag.Hardware/GpuResolver.cs`
- Modify: `tests/SzDiag.Hardware.Tests/GpuResolverTests.cs`

- [ ] **Step 1: Обновить `FakeScraper` в тестах + написать падающие тесты card-ветки**

В `tests/SzDiag.Hardware.Tests/GpuResolverTests.cs` заменить класс `FakeScraper` на версию с новым методом и добавить тесты. Новый `FakeScraper`:

```csharp
    private sealed class FakeScraper : IGpuScraper
    {
        private readonly PciDevice? _device;
        private readonly ScrapedCard? _card;
        private readonly Exception? _cardThrows;
        public bool DeviceCalled { get; private set; }
        public bool CardCalled { get; private set; }

        public FakeScraper(PciDevice? device = null, ScrapedCard? card = null, Exception? cardThrows = null)
        { _device = device; _card = card; _cardThrows = cardThrows; }

        public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        { DeviceCalled = true; return Task.FromResult(_device); }

        public Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default)
        {
            CardCalled = true;
            if (_cardThrows is not null) throw _cardThrows;
            return Task.FromResult(_card);
        }
    }

    private static ScrapedCard Card() => new(
        "1462", "5362", "MSI", "Ventus 2x OC Plus",
        "16384 MB", "GDDR7", "2407 MHz", "2602 MHz", "1750 MHz",
        "180.0 W", "180.0 W", "1x HDMI, 3x DisplayPort", "2025-03-15", "98.06.1F.00.CD",
        "https://www.techpowerup.com/vgabios/275654/");
```

Тесты (добавить методы):

```csharp
    [Fact]
    public async Task Resolve_CardMiss_ScraperFills_AndPersists()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(card: Card());
        var resolver = new GpuResolver(repo, scraper);

        var res = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));
        Assert.NotNull(res.Card);
        Assert.Equal("Ventus 2x OC Plus", res.Card!.CardName);
        Assert.Equal("5362", res.SubDeviceId);
        Assert.True(scraper.CardCalled);

        // повторный резолв — карта из БД, скрапер card не зван
        var scraper2 = new FakeScraper(card: Card());
        var again = await new GpuResolver(repo, scraper2).ResolveAsync(
            PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));
        Assert.NotNull(again.Card);
        Assert.False(scraper2.CardCalled);
    }

    [Fact]
    public async Task Resolve_CardScraperBlocked_CardNull_DeviceIntact()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(cardThrows: new ScrapeBlockedException("blocked"));
        var res = await new GpuResolver(repo, scraper).ResolveAsync(
            PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));

        Assert.Null(res.Card);
        Assert.Equal("GeForce RTX 5060 Ti", res.Model);   // device-часть цела
    }

    [Fact]
    public async Task Resolve_NoSubDevice_CardScraperNotCalled()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(card: Card());
        var res = await new GpuResolver(repo, scraper).ResolveAsync(
            PciId.Parse(@"PCI\VEN_10DE&DEV_2D04"));        // без SUBSYS

        Assert.Null(res.Card);
        Assert.False(scraper.CardCalled);
    }
```

Также обновить существующий тест `Resolve_Hit_FromCache_ScraperNotCalled`: он создаёт `new FakeScraper(null)` — заменить на `new FakeScraper()` и проверку `scraper.Called` → `scraper.DeviceCalled`. Тест `Resolve_Miss_ScraperFills_AndPersists` использует `new FakeScraper(new PciDevice(...))` → заменить на `new FakeScraper(device: new PciDevice(...))` и `scraper.Called` → `scraper.DeviceCalled`.

- [ ] **Step 2: Прогнать — падает (нет полей Card/SubDeviceId, старая сигнатура резолвера)**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuResolverTests`
Expected: FAIL — `GpuResolution` не имеет `Card`/`SubDeviceId`.

- [ ] **Step 3: Расширить `GpuResolution` и `ResolveAsync`**

В `src/SzDiag.Hardware/GpuResolver.cs` заменить запись `GpuResolution` и метод `ResolveAsync`:

```csharp
public sealed record GpuResolution(
    string VendorId, string? VendorName,
    string DeviceId, string? DeviceName, string? Chip, string? Model,
    string? SubVendorId, string? SubVendorName, string? SubDeviceId,
    string? Revision, GpuSource Source,
    ScrapedCard? Card);
```

```csharp
    public async Task<GpuResolution> ResolveAsync(PciId id, CancellationToken ct = default)
    {
        var vendorName = await _repo.LookupVendorAsync(id.VendorId, ct);
        var subVendorName = id.SubVendorId is null ? null : await _repo.LookupVendorAsync(id.SubVendorId, ct);

        var device = await _repo.LookupDeviceAsync(id.VendorId, id.DeviceId, ct);
        var source = GpuSource.Cache;
        if (device is null)
        {
            source = GpuSource.Unresolved;
            try
            {
                var scraped = await _scraper.ScrapeAsync(id, ct);
                if (scraped is not null)
                {
                    await _repo.UpsertDeviceAsync(scraped, ct);
                    device = scraped;
                    source = GpuSource.Scraper;
                }
            }
            catch (NotSupportedException) { /* заглушка device-скрапера */ }
        }

        // Карта по subsystem — независимый best-effort довесок.
        ScrapedCard? card = null;
        if (id.SubVendorId is not null && id.SubDeviceId is not null)
        {
            card = await _repo.LookupCardAsync(id.SubVendorId, id.SubDeviceId, ct);
            if (card is null)
            {
                try
                {
                    var scrapedCard = await _scraper.ScrapeCardAsync(id, device?.Model, ct);
                    if (scrapedCard is not null)
                    {
                        await _repo.UpsertCardAsync(scrapedCard, ct);
                        card = scrapedCard;
                    }
                }
                catch (NotSupportedException) { /* заглушка */ }
                catch (ScrapeBlockedException) { /* TPU за challenge */ }
                catch (HttpRequestException) { /* сеть недоступна */ }
            }
        }

        return new GpuResolution(
            id.VendorId, vendorName,
            id.DeviceId, device?.Name, device?.Chip, device?.Model,
            id.SubVendorId, subVendorName, id.SubDeviceId,
            id.Revision, source, card);
    }
```

Добавить `using System.Net.Http;` в начало файла (для `HttpRequestException`).

- [ ] **Step 4: Прогнать — зелёные**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuResolverTests`
Expected: PASS (обновлённые старые + 3 новых).

- [ ] **Step 5: Прогнать весь Hardware-проект**

Run: `dotnet test tests/SzDiag.Hardware.Tests`
Expected: PASS (все).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hardware/GpuResolver.cs tests/SzDiag.Hardware.Tests/GpuResolverTests.cs
git commit -m "feat(hardware): резолвер дорезолвивает карту по subsystem (кэш→скрапер→запись)"
```

---

## Task 8: `VgaBiosScraper` — оркестрация (search → detail → subsystem-матч)

**Files:**
- Create: `src/SzDiag.Hardware/VgaBiosScraper.cs`
- Modify: `tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs` (живой integration-тест, вне CI)

- [ ] **Step 1: Написать `VgaBiosScraper`**

`src/SzDiag.Hardware/VgaBiosScraper.cs`:

```csharp
using System.Web;

namespace SzDiag.Hardware;

/// <summary>Живой скрапер vgabios: по производителю+модели ищет прошивки, фетчит detail
/// кандидатов и матчит по Subsystem Id. Device-фоллбэк вне scope (заглушка).</summary>
public sealed class VgaBiosScraper : IGpuScraper
{
    private const string Base = "https://www.techpowerup.com";
    private readonly TechPowerUpClient _client;

    public VgaBiosScraper(TechPowerUpClient? client = null) => _client = client ?? new TechPowerUpClient();

    public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        => throw new NotSupportedException("device-фоллбэк vgabios не поддерживает");

    public async Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default)
    {
        if (id.SubVendorId is null || id.SubDeviceId is null || string.IsNullOrWhiteSpace(model))
            return null;

        // Производителя карты берём из субвендора через vgabios? — нет, фильтруем поиск по модели,
        // производителя матчим уже по Subsystem-строке detail (надёжнее, чем угадывать имя фильтра).
        var searchUrl = $"{Base}/vgabios/?model={HttpUtility.UrlEncode(NormalizeModel(model))}";
        var searchHtml = await _client.GetHtmlAsync(searchUrl, ct);
        var rows = VgaBiosParser.ParseSearch(searchHtml);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var detailHtml = await _client.GetHtmlAsync(Base + row.DetailUrl, ct);
            var d = VgaBiosParser.ParseDetail(detailHtml);
            if (d.SubVendorId == id.SubVendorId && d.SubDeviceId == id.SubDeviceId)
            {
                return new ScrapedCard(
                    id.SubVendorId, id.SubDeviceId,
                    row.Manufacturer, string.IsNullOrEmpty(row.CardName) ? null : row.CardName,
                    d.MemorySize, d.MemoryType ?? row.MemoryType,
                    d.CoreClock, d.BoostClock, d.MemoryClock,
                    d.PowerTarget, d.PowerLimit, d.Outputs,
                    row.DateCompiled, d.VbiosVersion ?? row.VbiosVersion,
                    Base + row.DetailUrl);
            }
        }
        return null;   // subsystem-матча нет — честно «плату не определили»
    }

    // «GeForce RTX 5060 Ti» → «RTX 5060 Ti» (vgabios-модели без вендорного префикса)
    private static string NormalizeModel(string model) => model
        .Replace("GeForce ", "").Replace("Radeon ", "").Trim();
}
```

- [ ] **Step 2: Живой integration-тест (вне CI, трейт live)**

Добавить в `VgaBiosParseTests.cs` (или отдельный файл — тут ок):

```csharp
    [Fact]
    [Trait("live", "true")]
    public async Task Live_ScrapeCard_Msi5060Ti_ResolvesBoard()
    {
        var id = PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53511462&REV_A1"); // subdev 5351 = Ventus 2x OC Plus
        var card = await new VgaBiosScraper().ScrapeCardAsync(id, "GeForce RTX 5060 Ti");
        Assert.NotNull(card);
        Assert.Equal("MSI", card!.Manufacturer);
        Assert.Contains("Ventus", card.CardName);
    }
```

- [ ] **Step 3: Собрать (не гонять live)**

Run: `dotnet build src/SzDiag.Hardware`
Expected: BUILD SUCCEEDED.

Run (только не-live тесты остаются зелёными): `dotnet test tests/SzDiag.Hardware.Tests --filter "live!=true"`
Expected: PASS. Живой тест исключён.

- [ ] **Step 4: (опционально, руками) прогнать live-тест для проверки селекторов**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter "live=true"`
Expected: PASS, если vgabios-разметка не протухла и сеть есть. Если FAIL из-за сети/challenge — это ок для CI (тест исключён), но стоит глянуть.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Hardware/VgaBiosScraper.cs tests/SzDiag.Hardware.Tests/VgaBiosParseTests.cs
git commit -m "feat(hardware): VgaBiosScraper — search→detail→subsystem-матч в ScrapedCard"
```

---

## Task 9: Подключить живой скрапер в CLI + вывод платы

**Files:**
- Modify: `src/SzDiag.Cli/HwCommand.cs`

- [ ] **Step 1: Заменить заглушку на `VgaBiosScraper`**

В `src/SzDiag.Cli/HwCommand.cs:44` заменить:

```csharp
            var res = await new GpuResolver(repo, new NotImplementedGpuScraper()).ResolveAsync(id);
```

на:

```csharp
            var res = await new GpuResolver(repo, new VgaBiosScraper()).ResolveAsync(id);
```

Program.cs трогать не нужно — скрапер инстанцируется только здесь.

- [ ] **Step 2: Дописать секцию «Плата» в методе `Print`**

В `src/SzDiag.Cli/HwCommand.cs`, в методе `private static void Print(GpuResolution r)`, сразу после закрывающего `);` блока `Console.WriteLine($"""...Источник: {source}\n""");` (перед `}` метода) добавить:

```csharp
        if (r.Card is { } card)
        {
            Console.WriteLine("  Плата (TPU VGA BIOS):");
            var title = string.Join(" ", new[] { card.Manufacturer, r.Model, card.CardName }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (title.Length > 0) Console.WriteLine($"    Карта:    {title}");
            if (card.MemorySize is not null || card.MemoryType is not null)
                Console.WriteLine($"    Память:   {card.MemorySize} {card.MemoryType}".TrimEnd());
            if (card.CoreClock is not null)
                Console.WriteLine($"    Частоты:  {card.CoreClock} / {card.BoostClock} / {card.MemoryClock} (core/boost/mem)");
            if (card.PowerTarget is not null)
                Console.WriteLine($"    Питание:  target {card.PowerTarget}, limit {card.PowerLimit}");
            if (card.Outputs is not null)
                Console.WriteLine($"    Выходы:   {card.Outputs}");
            if (card.VbiosVersion is not null)
                Console.WriteLine($"    VBIOS:    {card.VbiosVersion} ({card.DateCompiled})");
        }
        else if (r.SubDeviceId is not null)
        {
            Console.WriteLine("  Плата:    не определена (нет subsystem-матча в TPU / источник недоступен)");
        }
```

`.Where(...)` требует `using System.Linq;` — в `SzDiag.Cli` включён ImplicitUsings (net8), так что LINQ доступен без ручного using. Если сборка ругнётся — добавить `using System.Linq;` в шапку файла.

- [ ] **Step 3: Собрать солюшн**

Run: `dotnet build`
Expected: BUILD SUCCEEDED.

- [ ] **Step 4: Ручная проверка (опц., нужна сеть)**

Run: `dotnet run --project src/SzDiag.Cli -- hw resolve "PCI\VEN_10DE&DEV_2D04&SUBSYS_53511462&REV_A1"`
Expected: печатает вендора/модель/партнёра + секцию «Плата» с «MSI … Ventus». Первый вызов идёт в сеть, повторный — из `gpu.db`. (Требует, чтобы pci.ids был импортирован: `hw update` заранее.)

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Cli/Program.cs src/SzDiag.Cli/HwCommand.cs
git commit -m "feat(cli): hw resolve подключает vgabios-скрапер и печатает плату"
```

---

## Task 10: Документация + завершение

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Обновить описание `SzDiag.Hardware` в CLAUDE.md**

В `CLAUDE.md`, в буллете про `SzDiag.Hardware`, дописать про vgabios-обогащение. Найти предложение про `NotImplementedGpuScraper` (TPU-скрапер отложен) и заменить на актуальное:

```
резолвит по кэш-паттерну БД→miss→`IGpuScraper`→запись. Живой `VgaBiosScraper`
дорезолвивает точную партнёрскую плату (SKU) и спеки прошивки из TechPowerUp VGA BIOS
collection по subsystem ID (таблица `card`); `gpu-specs`-каталог за CAPTCHA — вне scope.
CLI: `szcli hw import/update/resolve`.
```

- [ ] **Step 2: Полный прогон тестов солюшна**

Run: `dotnet test --filter "live!=true"`
Expected: PASS (все ~76 + новые Hardware-тесты, кроме live).

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: vgabios-обогащение видях (точная плата + спеки прошивки)"
```

- [ ] **Step 4: Завершение ветки**

**REQUIRED SUB-SKILL:** Use superpowers:finishing-a-development-branch — проверить тесты, показать опции (merge/PR/keep/discard), выполнить выбор.

---

## Заметки для исполнителя

- **Порядок колонок search-таблицы** (Task 3) завязан на текущую разметку TPU
  (`mfgr, name, Date compiled, Version, Interface, Core/Mem/Boost, Memory, Links`). Если
  live-тест (Task 8) покажет расхождение — поправить индексы `Cell(i)` по факту фикстуры.
- **Регулярки power/outputs** (Task 4) — по свободному тексту тела; если поле не нашлось,
  метод отдаёт null (тесты это допускают для отсутствующих, но фикстура MSI 5060 Ti их
  содержит). Не заменять на «выброс».
- **subsystem нормализуется в lowercase** и там (PciId.Parse), и тут (ParseDetail) — матч
  идёт строкой-в-строку lowercase hex. Не сравнивать регистрозависимо.
- **Число фетчей** в ScrapeCardAsync = число строк search-результата. Для узких моделей это
  единицы; фильтр по модели уже сужает. Не добавлять параллелизм (вежливость к TPU).
