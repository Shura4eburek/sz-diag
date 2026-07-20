# Резолвер видеокарт по PCI hardware ID — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Новый проект `SzDiag.Hardware` — определение видеокарты по Windows PCI hardware ID через локальную базу pci.ids в SQLite, с кэш-паттерном `БД → miss → скрапер-заглушка → запись` и CLI `szcli hw`.

**Architecture:** `PciId.Parse` разбирает строку `PCI\VEN_..&DEV_..&SUBSYS_..&REV_..`. `PciIdsParser` парсит файл pci.ids. `GpuRepository` (SQLite) хранит вендоров/устройства и наполняется импортом. `GpuResolver` резолвит по частям (вендор/устройство/партнёр), при device-miss зовёт `IGpuScraper` (пока `NotImplementedGpuScraper`). CLI `hw import/update/resolve`.

**Tech Stack:** C# / net8.0, `Microsoft.Data.Sqlite` 8.0.11 (как в Hub), xUnit.

**Отклонения от спеки:** субсистем-таблицы нет — партнёр резолвится через `vendor`-таблицу (субвендор = обычный вендор pci.ids), поэтому `PciIdsParser` субсистем-строки игнорирует. Это уже заложено в спеке (YAGNI-раздел).

---

### Task 1: Каркас проекта `SzDiag.Hardware` + тест-проект

**Files:**
- Create: `src/SzDiag.Hardware/SzDiag.Hardware.csproj`
- Create: `tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj`
- Modify: `sz-diag.sln`

- [ ] **Step 1: Создать проекты через dotnet new**

```bash
dotnet new classlib -n SzDiag.Hardware -o src/SzDiag.Hardware -f net8.0
dotnet new xunit -n SzDiag.Hardware.Tests -o tests/SzDiag.Hardware.Tests -f net8.0
rm src/SzDiag.Hardware/Class1.cs tests/SzDiag.Hardware.Tests/UnitTest1.cs
```

- [ ] **Step 2: Переписать `src/SzDiag.Hardware/SzDiag.Hardware.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.11" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Переписать `tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>

    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SzDiag.Hardware\SzDiag.Hardware.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 4: Добавить проекты в решение**

```bash
dotnet sln sz-diag.sln add src/SzDiag.Hardware/SzDiag.Hardware.csproj tests/SzDiag.Hardware.Tests/SzDiag.Hardware.Tests.csproj
```

- [ ] **Step 5: Проверить сборку решения**

Run: `dotnet build`
Expected: Build succeeded, 0 Error(s), новые проекты в списке.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hardware tests/SzDiag.Hardware.Tests sz-diag.sln
git commit -m "build(hardware): каркас проекта SzDiag.Hardware + тесты"
```

---

### Task 2: `PciId` — разбор Windows PCI hardware ID

**Files:**
- Create: `src/SzDiag.Hardware/PciId.cs`
- Test: `tests/SzDiag.Hardware.Tests/PciIdParseTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hardware.Tests/PciIdParseTests.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class PciIdParseTests
{
    [Fact]
    public void Parse_FullWindowsId_ExtractsAllFields()
    {
        var id = PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1\4&1FC990D7&0&0019");

        Assert.Equal("10de", id.VendorId);
        Assert.Equal("2d04", id.DeviceId);
        Assert.Equal("1462", id.SubVendorId);   // младшее слово SUBSYS
        Assert.Equal("5362", id.SubDeviceId);    // старшее слово SUBSYS
        Assert.Equal("a1", id.Revision);
    }

    [Fact]
    public void Parse_NoSubsysNoRev_LeavesThoseNull()
    {
        var id = PciId.Parse(@"PCI\VEN_1002&DEV_73FF");

        Assert.Equal("1002", id.VendorId);
        Assert.Equal("73ff", id.DeviceId);
        Assert.Null(id.SubVendorId);
        Assert.Null(id.Revision);
    }

    [Fact]
    public void Parse_Garbage_Throws()
    {
        Assert.Throws<FormatException>(() => PciId.Parse("не pci вовсе"));
    }
}
```

- [ ] **Step 2: Запустить тест — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~PciIdParseTests`
Expected: FAIL — компиляция: типа `PciId` нет.

- [ ] **Step 3: Реализовать `PciId`**

Создать `src/SzDiag.Hardware/PciId.cs`:

```csharp
using System.Text.RegularExpressions;

namespace SzDiag.Hardware;

/// <summary>Разобранный Windows PCI hardware ID. VendorId/DeviceId обязательны,
/// остальное опционально. Все id — lowercase hex без префиксов.</summary>
public sealed record PciId(
    string VendorId, string DeviceId,
    string? SubVendorId = null, string? SubDeviceId = null, string? Revision = null)
{
    /// <summary>Разбирает строку вида
    /// PCI\VEN_10DE&amp;DEV_2D04&amp;SUBSYS_53621462&amp;REV_A1\...
    /// SUBSYS: младшее слово — субвендор, старшее — субустройство (формат Windows).</summary>
    public static PciId Parse(string raw)
    {
        var ven = Match(raw, "VEN_([0-9A-Fa-f]{4})");
        var dev = Match(raw, "DEV_([0-9A-Fa-f]{4})");
        if (ven is null || dev is null)
            throw new FormatException($"не PCI hardware ID: «{raw}»");

        string? subVen = null, subDev = null;
        var subsys = Match(raw, "SUBSYS_([0-9A-Fa-f]{8})");
        if (subsys is not null)
        {
            subDev = subsys.Substring(0, 4);
            subVen = subsys.Substring(4, 4);
        }
        return new PciId(ven, dev, subVen, subDev, Match(raw, "REV_([0-9A-Fa-f]{2})"));
    }

    private static string? Match(string input, string pattern)
    {
        var m = Regex.Match(input, pattern);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
}
```

- [ ] **Step 4: Запустить тест — убедиться, что проходит**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~PciIdParseTests`
Expected: PASS (3 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hardware/PciId.cs tests/SzDiag.Hardware.Tests/PciIdParseTests.cs
git commit -m "feat(hardware): разбор Windows PCI hardware ID"
```

---

### Task 3: `PciIdsParser` — парсер файла pci.ids

**Files:**
- Create: `src/SzDiag.Hardware/PciIdsParser.cs`
- Test: `tests/SzDiag.Hardware.Tests/PciIdsParserTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hardware.Tests/PciIdsParserTests.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class PciIdsParserTests
{
    // Вендор без таба; устройство — 1 таб; субсистема — 2 таба; комментарии/пустые строки.
    private const string Sample =
        "# комментарий\n" +
        "10de  NVIDIA Corporation\n" +
        "\t2d04  GB206 [GeForce RTX 5060 Ti]\n" +
        "\t\t1462 5362  RTX 5060 Ti Gaming\n" +
        "\t2505  GA106\n" +
        "1462  Micro-Star International Co., Ltd. [MSI]\n";

    [Fact]
    public void Parse_ReadsVendorsAndDevices()
    {
        var data = PciIdsParser.Parse(Sample);

        Assert.Equal("NVIDIA Corporation", data.Vendors["10de"]);
        Assert.Equal("Micro-Star International Co., Ltd. [MSI]", data.Vendors["1462"]);
        Assert.Equal(2, data.Devices.Count);   // субсистем-строки не считаются устройствами
    }

    [Fact]
    public void Parse_SplitsChipAndModel()
    {
        var data = PciIdsParser.Parse(Sample);

        var rtx = data.Devices.Single(d => d.DeviceId == "2d04");
        Assert.Equal("10de", rtx.VendorId);
        Assert.Equal("GB206", rtx.Chip);
        Assert.Equal("GeForce RTX 5060 Ti", rtx.Model);

        var plain = data.Devices.Single(d => d.DeviceId == "2505");
        Assert.Equal("GA106", plain.Chip);
        Assert.Null(plain.Model);
    }
}
```

- [ ] **Step 2: Запустить тест — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~PciIdsParserTests`
Expected: FAIL — типов `PciIdsParser`/`PciIdsData`/`PciDevice` нет.

- [ ] **Step 3: Реализовать парсер**

Создать `src/SzDiag.Hardware/PciIdsParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace SzDiag.Hardware;

/// <summary>Устройство из pci.ids. Chip — текст до «[», Model — внутри «[...]» (если есть).</summary>
public sealed record PciDevice(string VendorId, string DeviceId, string Name, string? Chip, string? Model);

/// <summary>Разобранный pci.ids: вендоры (id → имя) и устройства.</summary>
public sealed record PciIdsData(IReadOnlyDictionary<string, string> Vendors, IReadOnlyList<PciDevice> Devices);

/// <summary>
/// Парсер формата pci.ids. Вендор — строка без отступа «id  name»; устройство — с одним
/// табом; субсистема — с двумя (игнорируется). Строки с «#» и пустые пропускаются.
/// </summary>
public static class PciIdsParser
{
    public static PciIdsData Parse(string text)
    {
        var vendors = new Dictionary<string, string>();
        var devices = new List<PciDevice>();
        string? currentVendor = null;

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (rawLine.Length == 0 || rawLine.TrimStart().StartsWith("#")) continue;
            if (rawLine.StartsWith("\t\t")) continue;                 // субсистема — не нужна

            if (rawLine.StartsWith("\t"))                             // устройство
            {
                if (currentVendor is null) continue;
                var (id, name) = SplitIdName(rawLine.Substring(1));
                if (id is null) continue;
                var (chip, model) = SplitChipModel(name!);
                devices.Add(new PciDevice(currentVendor, id, name!, chip, model));
            }
            else if (!char.IsWhiteSpace(rawLine[0]))                 // вендор
            {
                var (id, name) = SplitIdName(rawLine);
                if (id is null) continue;
                currentVendor = id;
                vendors[id] = name!;
            }
        }
        return new PciIdsData(vendors, devices);
    }

    // «10de  NVIDIA Corporation» → ("10de", "NVIDIA Corporation"). Разделитель — два пробела.
    private static (string? Id, string? Name) SplitIdName(string line)
    {
        var m = Regex.Match(line, "^([0-9a-fA-F]{4})\\s+(.+)$");
        return m.Success ? (m.Groups[1].Value.ToLowerInvariant(), m.Groups[2].Value.Trim()) : (null, null);
    }

    // «GB206 [GeForce RTX 5060 Ti]» → ("GB206", "GeForce RTX 5060 Ti"); без скобок → (name, null).
    private static (string? Chip, string? Model) SplitChipModel(string name)
    {
        var m = Regex.Match(name, "^(.*?)\\s*\\[(.+)\\]\\s*$");
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : (name, null);
    }
}
```

- [ ] **Step 4: Запустить тест — убедиться, что проходит**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~PciIdsParserTests`
Expected: PASS (2 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hardware/PciIdsParser.cs tests/SzDiag.Hardware.Tests/PciIdsParserTests.cs
git commit -m "feat(hardware): парсер файла pci.ids (вендоры/устройства, chip/model)"
```

---

### Task 4: `GpuRepository` — SQLite-хранилище

**Files:**
- Create: `src/SzDiag.Hardware/IGpuRepository.cs`
- Create: `src/SzDiag.Hardware/GpuRepository.cs`
- Test: `tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class GpuRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szgpu-{Guid.NewGuid():N}.db");
    private GpuRepository NewRepo() => new($"Data Source={_dbPath}");

    private static PciIdsData Sample() => new(
        new Dictionary<string, string> { ["10de"] = "NVIDIA Corporation", ["1462"] = "MSI" },
        new[] { new PciDevice("10de", "2d04", "GB206 [GeForce RTX 5060 Ti]", "GB206", "GeForce RTX 5060 Ti") });

    [Fact]
    public async Task ImportThenLookup_ReturnsDeviceAndVendor()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.ImportAsync(Sample());

        var dev = await repo.LookupDeviceAsync("10de", "2d04");
        Assert.NotNull(dev);
        Assert.Equal("GeForce RTX 5060 Ti", dev!.Model);
        Assert.Equal("NVIDIA Corporation", await repo.LookupVendorAsync("10de"));
        Assert.Equal("MSI", await repo.LookupVendorAsync("1462"));
    }

    [Fact]
    public async Task LookupDevice_Missing_ReturnsNull()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        Assert.Null(await repo.LookupDeviceAsync("10de", "ffff"));
    }

    [Fact]
    public async Task Upsert_InsertsThenUpdates()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();

        await repo.UpsertDeviceAsync(new PciDevice("10de", "aaaa", "Old", "Old", null));
        await repo.UpsertDeviceAsync(new PciDevice("10de", "aaaa", "New [Model]", "New", "Model"));

        var dev = await repo.LookupDeviceAsync("10de", "aaaa");
        Assert.Equal("Model", dev!.Model);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

- [ ] **Step 2: Запустить тест — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuRepositoryTests`
Expected: FAIL — типов `IGpuRepository`/`GpuRepository` нет.

- [ ] **Step 3: Реализовать интерфейс**

Создать `src/SzDiag.Hardware/IGpuRepository.cs`:

```csharp
namespace SzDiag.Hardware;

/// <summary>Локальный справочник PCI-устройств (SQLite). Наполняется импортом pci.ids
/// и дозаписью из скрапера.</summary>
public interface IGpuRepository
{
    Task InitializeAsync(CancellationToken ct = default);
    Task ImportAsync(PciIdsData data, CancellationToken ct = default);
    Task<string?> LookupVendorAsync(string vendorId, CancellationToken ct = default);
    Task<PciDevice?> LookupDeviceAsync(string vendorId, string deviceId, CancellationToken ct = default);
    Task UpsertDeviceAsync(PciDevice device, CancellationToken ct = default);
}
```

- [ ] **Step 4: Реализовать `GpuRepository`**

Создать `src/SzDiag.Hardware/GpuRepository.cs`:

```csharp
using Microsoft.Data.Sqlite;

namespace SzDiag.Hardware;

/// <summary>SQLite-реализация справочника. Схема идемпотентна; upsert через ON CONFLICT.</summary>
public sealed class GpuRepository : IGpuRepository
{
    private readonly string _connectionString;
    public GpuRepository(string connectionString) => _connectionString = connectionString;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
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
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ImportAsync(PciIdsData data, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var vcmd = conn.CreateCommand())
        {
            vcmd.Transaction = tx;
            vcmd.CommandText = """
                INSERT INTO vendor (vendor_id, name) VALUES ($id, $name)
                ON CONFLICT(vendor_id) DO UPDATE SET name = excluded.name;
                """;
            var vid = vcmd.CreateParameter(); vid.ParameterName = "$id"; vcmd.Parameters.Add(vid);
            var vname = vcmd.CreateParameter(); vname.ParameterName = "$name"; vcmd.Parameters.Add(vname);
            foreach (var (id, name) in data.Vendors)
            {
                vid.Value = id; vname.Value = name;
                await vcmd.ExecuteNonQueryAsync(ct);
            }
        }

        await using (var dcmd = conn.CreateCommand())
        {
            dcmd.Transaction = tx;
            dcmd.CommandText = """
                INSERT INTO device (vendor_id, device_id, name, chip, model, source)
                VALUES ($ven, $dev, $name, $chip, $model, 'pci.ids')
                ON CONFLICT(vendor_id, device_id) DO UPDATE SET
                    name = excluded.name, chip = excluded.chip, model = excluded.model, source = excluded.source;
                """;
            var pven = dcmd.CreateParameter(); pven.ParameterName = "$ven"; dcmd.Parameters.Add(pven);
            var pdev = dcmd.CreateParameter(); pdev.ParameterName = "$dev"; dcmd.Parameters.Add(pdev);
            var pname = dcmd.CreateParameter(); pname.ParameterName = "$name"; dcmd.Parameters.Add(pname);
            var pchip = dcmd.CreateParameter(); pchip.ParameterName = "$chip"; dcmd.Parameters.Add(pchip);
            var pmodel = dcmd.CreateParameter(); pmodel.ParameterName = "$model"; dcmd.Parameters.Add(pmodel);
            foreach (var d in data.Devices)
            {
                pven.Value = d.VendorId; pdev.Value = d.DeviceId; pname.Value = d.Name;
                pchip.Value = (object?)d.Chip ?? DBNull.Value;
                pmodel.Value = (object?)d.Model ?? DBNull.Value;
                await dcmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<string?> LookupVendorAsync(string vendorId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM vendor WHERE vendor_id = $id;";
        cmd.Parameters.AddWithValue("$id", vendorId);
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    public async Task<PciDevice?> LookupDeviceAsync(string vendorId, string deviceId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name, chip, model FROM device WHERE vendor_id = $ven AND device_id = $dev;";
        cmd.Parameters.AddWithValue("$ven", vendorId);
        cmd.Parameters.AddWithValue("$dev", deviceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PciDevice(vendorId, deviceId, reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    public async Task UpsertDeviceAsync(PciDevice device, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO device (vendor_id, device_id, name, chip, model, source)
            VALUES ($ven, $dev, $name, $chip, $model, 'scraper')
            ON CONFLICT(vendor_id, device_id) DO UPDATE SET
                name = excluded.name, chip = excluded.chip, model = excluded.model, source = excluded.source;
            """;
        cmd.Parameters.AddWithValue("$ven", device.VendorId);
        cmd.Parameters.AddWithValue("$dev", device.DeviceId);
        cmd.Parameters.AddWithValue("$name", device.Name);
        cmd.Parameters.AddWithValue("$chip", (object?)device.Chip ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)device.Model ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
```

- [ ] **Step 5: Запустить тест — убедиться, что проходит**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuRepositoryTests`
Expected: PASS (3 теста).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hardware/IGpuRepository.cs src/SzDiag.Hardware/GpuRepository.cs tests/SzDiag.Hardware.Tests/GpuRepositoryTests.cs
git commit -m "feat(hardware): SQLite-справочник PCI-устройств (import/lookup/upsert)"
```

---

### Task 5: `IGpuScraper` (заглушка) + `GpuResolver`

**Files:**
- Create: `src/SzDiag.Hardware/IGpuScraper.cs`
- Create: `src/SzDiag.Hardware/GpuResolver.cs`
- Test: `tests/SzDiag.Hardware.Tests/GpuResolverTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hardware.Tests/GpuResolverTests.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class GpuResolverTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szres-{Guid.NewGuid():N}.db");
    private GpuRepository _repo = null!;

    private async Task<GpuRepository> SeededRepoAsync()
    {
        _repo = new GpuRepository($"Data Source={_dbPath}");
        await _repo.InitializeAsync();
        await _repo.ImportAsync(new PciIdsData(
            new Dictionary<string, string> { ["10de"] = "NVIDIA Corporation", ["1462"] = "MSI" },
            new[] { new PciDevice("10de", "2d04", "GB206 [GeForce RTX 5060 Ti]", "GB206", "GeForce RTX 5060 Ti") }));
        return _repo;
    }

    private sealed class FakeScraper : IGpuScraper
    {
        private readonly PciDevice? _result;
        public bool Called { get; private set; }
        public FakeScraper(PciDevice? result) => _result = result;
        public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task Resolve_Hit_FromCache_ScraperNotCalled()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(null);
        var res = await new GpuResolver(repo, scraper)
            .ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));

        Assert.Equal(GpuSource.Cache, res.Source);
        Assert.Equal("GeForce RTX 5060 Ti", res.Model);
        Assert.Equal("NVIDIA Corporation", res.VendorName);
        Assert.Equal("MSI", res.SubVendorName);
        Assert.False(scraper.Called);
    }

    [Fact]
    public async Task Resolve_Miss_ScraperFills_AndPersists()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(new PciDevice("10de", "ffff", "GH100 [H100]", "GH100", "H100"));
        var resolver = new GpuResolver(repo, scraper);

        var res = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_FFFF"));
        Assert.Equal(GpuSource.Scraper, res.Source);
        Assert.Equal("H100", res.Model);
        Assert.True(scraper.Called);

        // записано в БД — повторный резолв берёт из кэша
        var again = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_FFFF"));
        Assert.Equal(GpuSource.Cache, again.Source);
    }

    [Fact]
    public async Task Resolve_Miss_StubScraper_Unresolved_ButVendorKnown()
    {
        var repo = await SeededRepoAsync();
        var res = await new GpuResolver(repo, new NotImplementedGpuScraper())
            .ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_EEEE&SUBSYS_00001462"));

        Assert.Equal(GpuSource.Unresolved, res.Source);
        Assert.Null(res.Model);
        Assert.Equal("NVIDIA Corporation", res.VendorName);
        Assert.Equal("MSI", res.SubVendorName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

- [ ] **Step 2: Запустить тест — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuResolverTests`
Expected: FAIL — типов `IGpuScraper`/`NotImplementedGpuScraper`/`GpuResolver`/`GpuSource`/`GpuResolution` нет.

- [ ] **Step 3: Реализовать `IGpuScraper` + заглушку**

Создать `src/SzDiag.Hardware/IGpuScraper.cs`:

```csharp
namespace SzDiag.Hardware;

/// <summary>Шаг 2 кэш-паттерна: дорезолвить устройство, которого нет в локальной базе.
/// Реализация TPU отложена (Cloudflare/headless); пока — заглушка.</summary>
public interface IGpuScraper
{
    Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default);
}

/// <summary>Заглушка: скрапер ещё не подключён. Резолвер ловит это и отдаёт Unresolved.</summary>
public sealed class NotImplementedGpuScraper : IGpuScraper
{
    public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        => throw new NotSupportedException("TPU-скрапер ещё не подключён; обнови локальную базу через `hw update`");
}
```

- [ ] **Step 4: Реализовать `GpuResolver`**

Создать `src/SzDiag.Hardware/GpuResolver.cs`:

```csharp
namespace SzDiag.Hardware;

/// <summary>Откуда взялась модель устройства.</summary>
public enum GpuSource { Cache, Scraper, Unresolved }

/// <summary>Результат резолва PCI ID. Вендор/партнёр могут быть известны даже при Unresolved.</summary>
public sealed record GpuResolution(
    string VendorId, string? VendorName,
    string DeviceId, string? DeviceName, string? Chip, string? Model,
    string? SubVendorId, string? SubVendorName,
    string? Revision, GpuSource Source);

/// <summary>Оркестрация кэш-паттерна: БД → miss → скрапер → запись. Резолвит вендора,
/// устройство и партнёра независимо — при device-miss отдаёт что известно.</summary>
public sealed class GpuResolver
{
    private readonly IGpuRepository _repo;
    private readonly IGpuScraper _scraper;

    public GpuResolver(IGpuRepository repo, IGpuScraper scraper)
    {
        _repo = repo;
        _scraper = scraper;
    }

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
            catch (NotSupportedException) { /* заглушка — остаёмся Unresolved */ }
        }

        return new GpuResolution(
            id.VendorId, vendorName,
            id.DeviceId, device?.Name, device?.Chip, device?.Model,
            id.SubVendorId, subVendorName,
            id.Revision, source);
    }
}
```

- [ ] **Step 5: Запустить тест — убедиться, что проходит**

Run: `dotnet test tests/SzDiag.Hardware.Tests --filter FullyQualifiedName~GpuResolverTests`
Expected: PASS (3 теста).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hardware/IGpuScraper.cs src/SzDiag.Hardware/GpuResolver.cs tests/SzDiag.Hardware.Tests/GpuResolverTests.cs
git commit -m "feat(hardware): GpuResolver с кэш-паттерном + заглушка IGpuScraper"
```

---

### Task 6: CLI — команда `hw` (import/update/resolve)

**Files:**
- Create: `src/SzDiag.Cli/HwCommand.cs`
- Modify: `src/SzDiag.Cli/CliOptions.cs`
- Modify: `src/SzDiag.Cli/appsettings.json`
- Modify: `src/SzDiag.Cli/Program.cs`
- Modify: `src/SzDiag.Cli/SzDiag.Cli.csproj`

- [ ] **Step 1: Ссылка на проект Hardware**

В `src/SzDiag.Cli/SzDiag.Cli.csproj`, в первый `ItemGroup` с `ProjectReference`, добавить:

```xml
    <ProjectReference Include="..\SzDiag.Hardware\SzDiag.Hardware.csproj" />
```

- [ ] **Step 2: Опции путей**

В `src/SzDiag.Cli/CliOptions.cs` добавить в класс:

```csharp
    public string GpuDbPath { get; set; } = "gpu.db";
    public string PciIdsPath { get; set; } = "pci.ids";
```

И в `src/SzDiag.Cli/appsettings.json` добавить пары (перед закрывающей `}`):

```json
  "GpuDbPath": "gpu.db",
  "PciIdsPath": "pci.ids"
```

(не забыть запятую после предыдущего значения `"KbRoot": "kb"` → `"KbRoot": "kb",`)

- [ ] **Step 3: Реализовать `HwCommand`**

Создать `src/SzDiag.Cli/HwCommand.cs`:

```csharp
using SzDiag.Hardware;

namespace SzDiag.Cli;

/// <summary>Подкоманды `hw import` / `hw update` / `hw resolve`. Тонкий слой над SzDiag.Hardware.</summary>
public static class HwCommand
{
    private const string PciIdsUrl = "https://pci-ids.ucw.cz/v2.2/pci.ids";

    public static async Task RunAsync(string[] args, string dbPath, string pciIdsPath)
    {
        var repo = new GpuRepository($"Data Source={dbPath}");
        await repo.InitializeAsync();
        var sub = args[0].ToLowerInvariant();

        if (sub == "import")
        {
            var path = args.Length >= 2 ? args[1] : pciIdsPath;
            if (!File.Exists(path))
            {
                Console.WriteLine($"Не найден pci.ids: {path}. Скачай через `szcli hw update`.");
                return;
            }
            await ImportFileAsync(repo, path);
            return;
        }

        if (sub == "update")
        {
            Console.WriteLine($"Качаю {PciIdsUrl} …");
            using var http = new HttpClient();
            var text = await http.GetStringAsync(PciIdsUrl);
            await File.WriteAllTextAsync(pciIdsPath, text);
            await ImportFileAsync(repo, pciIdsPath);
            return;
        }

        if (sub == "resolve" && args.Length >= 2)
        {
            PciId id;
            try { id = PciId.Parse(args[1]); }
            catch (FormatException ex) { Console.WriteLine(ex.Message); return; }

            var res = await new GpuResolver(repo, new NotImplementedGpuScraper()).ResolveAsync(id);
            Print(res);
            return;
        }

        Console.WriteLine("""
            Использование:
              szcli hw import [<путь к pci.ids>]   импорт локального файла в базу
              szcli hw update                      скачать свежий pci.ids и импортировать
              szcli hw resolve "<PCI ID>"          определить видяху по hardware id
            """);
    }

    private static async Task ImportFileAsync(GpuRepository repo, string path)
    {
        var data = PciIdsParser.Parse(await File.ReadAllTextAsync(path));
        await repo.ImportAsync(data);
        Console.WriteLine($"Импортировано: вендоров {data.Vendors.Count}, устройств {data.Devices.Count}.");
    }

    private static void Print(GpuResolution r)
    {
        var vendor = r.VendorName ?? "неизвестен";
        var model = r.Model ?? "не определена";
        var chip = r.Chip ?? "—";
        var partner = r.SubVendorName ?? (r.SubVendorId ?? "—");
        var source = r.Source switch
        {
            GpuSource.Cache => "локальная база (pci.ids)",
            GpuSource.Scraper => "скрапер",
            _ => "не определён (device нет в базе; попробуй `hw update`)"
        };
        Console.WriteLine($"""
              Вендор:   {vendor} ({r.VendorId})
              Модель:   {model}
              Чип:      {chip}
              Партнёр:  {partner}{(r.SubVendorId is null ? "" : $" ({r.SubVendorId})")}
              Ревизия:  {r.Revision ?? "—"}
              Источник: {source}
            """);
    }
}
```

- [ ] **Step 4: Диспатч в `Program.cs`**

В `src/SzDiag.Cli/Program.cs`, после блока `case "kb" …` (перед `case "test"`), добавить:

```csharp
    case "hw" when args.Length >= 2:
        await HwCommand.RunAsync(args[1..], ResolveLocal(options.GpuDbPath), ResolveLocal(options.PciIdsPath));
        break;
```

И добавить локальный резолвер путей рядом с `WatchAsync` (внизу файла, статическая функция):

```csharp
static string ResolveLocal(string path)
    => Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
```

А в блок usage (`default:`) добавить строку:

```csharp
              [yellow]szcli hw[/] …               видяха по PCI hardware id
```

- [ ] **Step 5: Собрать решение**

Run: `dotnet build`
Expected: Build succeeded, 0 Error(s).

- [ ] **Step 6: Прогнать весь тест-набор**

Run: `dotnet test`
Expected: PASS — все проекты зелёные, включая новый `SzDiag.Hardware.Tests` (11 тестов).

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Cli
git commit -m "feat(cli): команда hw import/update/resolve"
```

---

### Task 7: Живой smoke + документация

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Живой smoke на реальном pci.ids**

```bash
cd /c/Users/ENDI/RiderProjects/sz-diag
TMPDIR_HW="$TEMP/hw-smoke-$$"; mkdir -p "$TMPDIR_HW"
curl -sL "https://pci-ids.ucw.cz/v2.2/pci.ids" -o "$TMPDIR_HW/pci.ids"
export SZDIAG_GpuDbPath="$TMPDIR_HW/gpu.db"
export SZDIAG_PciIdsPath="$TMPDIR_HW/pci.ids"
dotnet run --project src/SzDiag.Cli --no-build -- hw import
dotnet run --project src/SzDiag.Cli --no-build -- hw resolve "PCI\\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1\\4&1FC990D7&0&0019"
rm -rf "$TMPDIR_HW"
```

Expected: импорт печатает тысячи вендоров/устройств; resolve выводит `Модель: GeForce RTX 5060 Ti`, `Чип: GB206`, `Партнёр: … (MSI)`, `Источник: локальная база`.

- [ ] **Step 2: Дополнить CLAUDE.md — новый проект**

В `CLAUDE.md`, в разделе «Архитектура», в перечислении проектов (после `SzDiag.Kb`), добавить:

```
- **SzDiag.Hardware** — определение видеокарты по Windows PCI hardware ID
  (`PCI\VEN_..&DEV_..&SUBSYS_..`). `PciId.Parse` разбирает id, `PciIdsParser` парсит базу
  pci.ids, `GpuRepository` (SQLite `gpu.db`) хранит вендоров/устройства, `GpuResolver`
  резолвит по кэш-паттерну БД→miss→`IGpuScraper`→запись. TPU-скрапер отложен (Cloudflare)
  за заглушкой `NotImplementedGpuScraper`. CLI: `szcli hw import/update/resolve`.
```

И поправить вводную «Пять проектов в `src/`» → «Шесть проектов в `src/`».

- [ ] **Step 3: Проверить кириллицу**

Run: `git diff CLAUDE.md`
Expected: изменения читаемы, кириллица корректна.

- [ ] **Step 4: Коммит**

```bash
git add CLAUDE.md
git commit -m "docs: проект SzDiag.Hardware в описании архитектуры"
```

---

## Итоговая проверка

- [ ] `dotnet build` — без ошибок.
- [ ] `dotnet test` — весь набор зелёный (прежние ~90 + 11 новых в Hardware.Tests).
- [ ] Живой smoke: `hw resolve` на `DEV_2D04` даёт `GeForce RTX 5060 Ti` из локальной базы.
