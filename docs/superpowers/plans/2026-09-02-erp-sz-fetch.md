# `szcli sz fetch` — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Команда `szcli sz fetch <СЗ>` забирает данные сервисной заявки из локального API учётной системы и раскладывает их в `kb/СЗ/<номер>/`, не затирая написанное руками.

**Architecture:** Новый проект-библиотека `SzDiag.Erp` (HTTP-клиент, сессия захвата, DTO, сборка markdown-блока, запись в kb), зависит от `SzDiag.Kb`. `SzDiag.Kb` не получает новых зависимостей. `SzDiag.Cli` остаётся тонким: разбор аргументов, печать, коды возврата.

**Tech Stack:** net8.0, `System.Text.Json`, `HttpClient`, xunit 2.5.3, Spectre.Console (только в CLI).

**Spec:** [`docs/superpowers/specs/2026-09-02-erp-sz-fetch-design.md`](../specs/2026-09-02-erp-sz-fetch-design.md)

## Global Constraints

- Целевой фреймворк — **net8.0**, `ImplicitUsings enable`, `Nullable enable` (как во всех проектах репо).
- **Репозиторий публичный.** Ни в коде, ни в тестах, ни в фикстурах, ни в сообщениях коммитов: имени вендора учётной системы, имени сопутствующего инструмента, адреса и порта API, имени заголовка токена, внутренних идентификаторов элементов интерфейса. Нейтральное имя везде — **ERP** / `Erp`.
- **Персональные данные клиентов в git не попадают.** Фикстура обезличивается до коммита: ФИО, телефоны, адреса, настоящие серийники — синтетические.
- Комментарии и вывод в консоль — **на русском**. Контент базы знаний (заголовки секций в заметках, текст блока) — **на украинском**.
- Файлы, которые читает PowerShell 5.1 (`tools/*.ps1`), сохранять **UTF-8 с BOM**. Файлы C# — как в репо.
- Пути к конфигу и файлам резолвятся от `AppContext.BaseDirectory`, не от текущего каталога.
- Правило репо: ad-hoc PowerShell не остаётся в чате — кладётся в `tools/recipes/host/` или `tools/recipes/client/`.
- Секретов в конфиг не пишем: в `appsettings.json` только **путь** к файлу токена, сам токен читается при каждом запуске.

---

### Task 1: Каркас проектов и обезличенная фикстура

Фикстура блокирует всё остальное: DTO, собранные по описанию API вместо реального ответа, разойдутся с реальностью. Задача заканчивается тем, что солюшен собирается, тесты гоняются, а в тестах лежит настоящий (обезличенный) ответ.

**Files:**
- Create: `src/SzDiag.Erp/SzDiag.Erp.csproj`
- Create: `tests/SzDiag.Erp.Tests/SzDiag.Erp.Tests.csproj`
- Create: `tests/SzDiag.Erp.Tests/Fixtures/sz-fetch.json`
- Create: `tests/SzDiag.Erp.Tests/FixtureTests.cs`
- Create: `tools/recipes/host/erp-fetch-raw.ps1`
- Modify: `SzDiag.sln`
- Modify: `tools/recipes/README.md`

**Interfaces:**
- Consumes: ничего.
- Produces: проекты `SzDiag.Erp` / `SzDiag.Erp.Tests`; файл фикстуры `Fixtures/sz-fetch.json`, копируемый в выходной каталог; вспомогательный метод `FixtureTests.Load()` не экспортируется — каждый следующий тест читает фикстуру своим хелпером `Fixture.Read()` (создаётся в Task 3).

- [ ] **Step 1: Создать проект библиотеки**

`src/SzDiag.Erp/SzDiag.Erp.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\SzDiag.Kb\SzDiag.Kb.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Создать тестовый проект**

`tests/SzDiag.Erp.Tests/SzDiag.Erp.Tests.csproj`:

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
    <ProjectReference Include="..\..\src\SzDiag.Erp\SzDiag.Erp.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="Fixtures\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 3: Добавить проекты в солюшен**

```powershell
dotnet sln SzDiag.sln add src/SzDiag.Erp/SzDiag.Erp.csproj tests/SzDiag.Erp.Tests/SzDiag.Erp.Tests.csproj
```

- [ ] **Step 4: Написать рецепт съёма сырого ответа**

`tools/recipes/host/erp-fetch-raw.ps1` (UTF-8 с BOM). Порт и путь к токену — обязательные параметры без дефолтов: репозиторий публичный, конкретика живёт в локальной доке `docs/erp-api.md`.

```powershell
<#
    Снять сырой ответ API учётной системы по одной заявке и положить в файл.
    Грабля, породившая рецепт: DTO нельзя строить по описанию API — нужен настоящий
    ответ, а руками собирать заголовок с токеном каждый раз муторно.

    Пример:
      .\erp-fetch-raw.ps1 -Sz 160800 -Port 8765 `
          -TokenFile C:\path\to\api_token -Out .\sz-fetch-raw.json
#>
param(
    [Parameter(Mandatory)] [string]$Sz,
    [Parameter(Mandatory)] [int]$Port,
    [Parameter(Mandatory)] [string]$TokenFile,
    [Parameter(Mandatory)] [string]$Out,
    [string]$TokenHeader = "X-Api-Token"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $TokenFile)) { throw "Файла токена нет: $TokenFile" }
$token = (Get-Content $TokenFile -Raw).Trim()
$base = "http://127.0.0.1:$Port"
$headers = @{ $TokenHeader = $token }

function Invoke-Tool([string]$Name, $Arguments) {
    $body = @{ name = $Name; arguments = $Arguments } | ConvertTo-Json -Depth 6
    Invoke-RestMethod -Uri "$base/call" -Method Post -Headers $headers `
        -ContentType "application/json; charset=utf-8" -Body $body -TimeoutSec 180
}

Write-Host "== захват клиента; НЕ ТРОГАЙ МЫШЬ, ~1 минута ==" -ForegroundColor Yellow
Invoke-Tool "session.begin" @{} | Out-Null
try {
    $r = Invoke-Tool "sz.fetch" @{ number = $Sz }
    $r.result | ConvertTo-Json -Depth 12 | Set-Content -Path $Out -Encoding utf8
    Write-Host "Ответ сохранён: $Out"
}
finally {
    Invoke-Tool "session.end" @{} | Out-Null
    Write-Host "Захват отпущен."
}
```

- [ ] **Step 5: Снять живой ответ**

Требует запущенных программ и живого логина. Токен читается из файла — если среда исполнения запрещает читать его или слать в заголовке, попроси человека выполнить рецепт самому и отдать файл.

```powershell
.\tools\recipes\host\erp-fetch-raw.ps1 -Sz <живой номер> -Port <порт> -TokenFile <путь> -Out .\raw.json
```

Ожидание: файл `raw.json` с полями `request`, `order`, `assembly`.

- [ ] **Step 6: Обезличить и положить фикстуру**

Заменить всё персональное синтетикой, сохранив **форму** данных (длину серийников, набор ключей, количество строк). Что чистить: ФИО, телефоны, адреса, e-mail, настоящие серийники, реальные номера заявок и заказов.

Взять номер заявки `160800` и заказа `1951256` как синтетические, серийники вида `SN000000000001`. Результат положить в `tests/SzDiag.Erp.Tests/Fixtures/sz-fetch.json`.

Проверить глазами перед коммитом:

```powershell
Select-String -Path tests\SzDiag.Erp.Tests\Fixtures\sz-fetch.json -Pattern '\+380|@|вул\.|ул\.'
```

Ожидание: пусто. Если что-то нашлось — вычистить и повторить.

- [ ] **Step 7: Написать тест, что фикстура на месте и разбирается**

`tests/SzDiag.Erp.Tests/FixtureTests.cs`:

```csharp
using System.Text.Json;

namespace SzDiag.Erp.Tests;

public class FixtureTests
{
    [Fact]
    public void Фикстура_разбирается_и_содержит_заявку()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sz-fetch.json");
        Assert.True(File.Exists(path), $"Нет фикстуры: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var request = doc.RootElement.GetProperty("request");

        Assert.False(string.IsNullOrWhiteSpace(request.GetProperty("number").GetString()));
        Assert.True(request.GetProperty("fields").EnumerateObject().Any());
    }

    [Fact]
    public void Фикстура_обезличена()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sz-fetch.json"));
        // Живые контакты в публичном репо — недопустимы (см. Global Constraints).
        Assert.DoesNotContain("+380", raw);
        Assert.DoesNotContain("@", raw);
    }
}
```

- [ ] **Step 8: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Erp.Tests`
Expected: PASS, 2 теста.

- [ ] **Step 9: Дописать строку в таблицу рецептов**

В `tools/recipes/README.md` добавить строку про `erp-fetch-raw.ps1`: во что должен вырасти — в `szcli sz fetch`.

- [ ] **Step 10: Коммит**

```bash
git add SzDiag.sln src/SzDiag.Erp tests/SzDiag.Erp.Tests tools/recipes/host/erp-fetch-raw.ps1 tools/recipes/README.md
git commit -m "feat(erp): каркас проекта + обезличенная фикстура ответа API"
```

---

### Task 2: `MarkedBlock` — вставка блока без порчи ручного текста

**Files:**
- Create: `src/SzDiag.Erp/MarkedBlock.cs`
- Test: `tests/SzDiag.Erp.Tests/MarkedBlockTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `public static class MarkedBlock` с константами `Begin`/`End` и методом `public static string Upsert(string text, string block)`.

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Erp.Tests/MarkedBlockTests.cs`:

```csharp
namespace SzDiag.Erp.Tests;

public class MarkedBlockTests
{
    [Fact]
    public void В_пустой_текст_блок_добавляется_с_маркерами()
    {
        var result = MarkedBlock.Upsert("", "тіло");

        Assert.Contains(MarkedBlock.Begin, result);
        Assert.Contains("тіло", result);
        Assert.Contains(MarkedBlock.End, result);
    }

    [Fact]
    public void Ручной_текст_без_маркеров_сохраняется_целиком()
    {
        var manual = "# Дефект — СЗ 160800\n\nклієнт каже: гасне під грою\n";

        var result = MarkedBlock.Upsert(manual, "тіло");

        Assert.StartsWith(manual, result);
        Assert.Contains("тіло", result);
    }

    [Fact]
    public void Повторная_вставка_заменяет_блок_а_не_дублирует()
    {
        var once = MarkedBlock.Upsert("шапка\n", "перше");

        var twice = MarkedBlock.Upsert(once, "друге");

        Assert.Contains("друге", twice);
        Assert.DoesNotContain("перше", twice);
        Assert.Equal(1, CountOf(twice, MarkedBlock.Begin));
        Assert.Equal(1, CountOf(twice, MarkedBlock.End));
    }

    [Fact]
    public void Текст_до_и_после_блока_переживает_замену()
    {
        var text = MarkedBlock.Upsert("до\n", "перше") + "\nпісля\n";

        var result = MarkedBlock.Upsert(text, "друге");

        Assert.StartsWith("до\n", result);
        Assert.EndsWith("після\n", result);
        Assert.Contains("друге", result);
    }

    [Fact]
    public void Незакрытый_маркер_ничего_не_съедает()
    {
        // Человек снёс половину блока руками: закрывающего маркера нет.
        var broken = $"важливий текст\n{MarkedBlock.Begin}\nогризок\n";

        var result = MarkedBlock.Upsert(broken, "нове");

        Assert.Contains("важливий текст", result);
        Assert.Contains("огризок", result);
        Assert.Contains("нове", result);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~MarkedBlockTests`
Expected: FAIL — `MarkedBlock` не существует (ошибка компиляции).

- [ ] **Step 3: Реализовать**

`src/SzDiag.Erp/MarkedBlock.cs`:

```csharp
namespace SzDiag.Erp;

/// <summary>
/// Вкладывает генерируемый блок в заметку между маркерами, не трогая остальной текст.
/// Заметку по заявке человек дополняет руками по ходу ремонта, поэтому повторный
/// фетч обязан переписывать только свой блок.
/// </summary>
public static class MarkedBlock
{
    public const string Begin = "<!-- erp:початок -->";
    public const string End = "<!-- erp:кінець -->";

    /// <summary>
    /// Маркеры найдены — содержимое между ними заменяется. Не найдены (или найден только
    /// открывающий: человек снёс половину) — блок дописывается в конец, ничего не съедая.
    /// </summary>
    public static string Upsert(string text, string block)
    {
        var body = $"{Begin}\n{block.Trim()}\n{End}";

        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return text[..start] + body + text[(end + End.Length)..];

        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        return $"{text}{separator}\n{body}\n";
    }
}
```

- [ ] **Step 4: Прогнать — зелено**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~MarkedBlockTests`
Expected: PASS, 5 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Erp/MarkedBlock.cs tests/SzDiag.Erp.Tests/MarkedBlockTests.cs
git commit -m "feat(erp): MarkedBlock — вставка блока без порчи ручного текста"
```

---

### Task 3: DTO и разбор ответа

**Files:**
- Create: `src/SzDiag.Erp/SzFetchResult.cs`
- Create: `src/SzDiag.Erp/ErpJson.cs`
- Create: `tests/SzDiag.Erp.Tests/Fixture.cs`
- Test: `tests/SzDiag.Erp.Tests/ErpJsonTests.cs`

**Interfaces:**
- Consumes: фикстуру из Task 1.
- Produces:
  - `public sealed record ErpComponent(string Code, string Name, string Serial, string Quantity)`
  - `public sealed record ErpRequest(string Number, IReadOnlyDictionary<string,string> Fields, IReadOnlyList<ErpComponent> Components, string? OrderNumber)`
  - `public sealed record ErpOrder(string Number, IReadOnlyDictionary<string,string> Fields, IReadOnlyList<IReadOnlyDictionary<string,string>> Products)` со свойством `string? SingleProductName`
  - `public sealed record ErpAssembly(string Number, string Summary, IReadOnlyList<ErpComponent> Components)`
  - `public sealed record SzFetchResult(ErpRequest Request, ErpOrder? Order, ErpAssembly? Assembly)` со свойством `IReadOnlyList<ErpComponent> Configuration`
  - `public static class ErpJson` с `public static SzFetchResult ParseSzFetch(JsonElement root)`
  - `internal static class Fixture` (в тестах) с `public static JsonElement SzFetch()`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Erp.Tests/Fixture.cs`:

```csharp
using System.Text.Json;

namespace SzDiag.Erp.Tests;

internal static class Fixture
{
    private static readonly JsonDocument Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sz-fetch.json")));

    public static JsonElement SzFetch() => Doc.RootElement;

    /// <summary>Ответ, собранный на лету: для случаев, которых в фикстуре нет.</summary>
    public static JsonElement Of(string json) => JsonDocument.Parse(json).RootElement;
}
```

`tests/SzDiag.Erp.Tests/ErpJsonTests.cs`:

```csharp
namespace SzDiag.Erp.Tests;

public class ErpJsonTests
{
    [Fact]
    public void Заявка_разбирается_из_фикстуры()
    {
        var result = ErpJson.ParseSzFetch(Fixture.SzFetch());

        Assert.False(string.IsNullOrWhiteSpace(result.Request.Number));
        Assert.NotEmpty(result.Request.Fields);
    }

    [Fact]
    public void Заказа_может_не_быть()
    {
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":null},
             "order":null,"assembly":null}
            """);

        var result = ErpJson.ParseSzFetch(json);

        Assert.Null(result.Order);
        Assert.Null(result.Assembly);
        Assert.Null(result.Request.OrderNumber);
    }

    [Fact]
    public void Пустая_комплектация_это_пустой_список_а_не_ошибка()
    {
        // Вкладки комплектации может не быть — она появляется не на всех статусах заявки.
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[],"service_requests":[]},"assembly":null}
            """);

        var result = ErpJson.ParseSzFetch(json);

        Assert.Empty(result.Request.Components);
    }

    [Fact]
    public void Компоненты_читаются_с_серийниками()
    {
        var json = Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"Відеокарта X","serial":"SN000000000001","quantity":"1"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """);

        var component = Assert.Single(ErpJson.ParseSzFetch(json).Request.Components);

        Assert.Equal("Відеокарта X", component.Name);
        Assert.Equal("SN000000000001", component.Serial);
    }

    [Fact]
    public void Устройство_берётся_только_из_единственной_товарной_строки()
    {
        var one = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[{"Код":"1","Товар":"ПК Ігровий","Кіл-ть":"1"}],
             "service_requests":[]},"assembly":null}
            """);
        var many = Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Материнська плата","Кіл-ть":"1"},
               {"Код":"2","Товар":"Процесор","Кіл-ть":"1"}],
             "service_requests":[]},"assembly":null}
            """);

        Assert.Equal("ПК Ігровий", ErpJson.ParseSzFetch(one).Order!.SingleProductName);
        Assert.Null(ErpJson.ParseSzFetch(many).Order!.SingleProductName);
    }

    [Fact]
    public void Состав_берётся_из_сборки_если_она_есть_иначе_из_комплектации()
    {
        var withAssembly = Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"З заявки","serial":"SN1","quantity":"1"}],
             "discussion":[],"order_number":"1951256"},"order":null,
             "assembly":{"number":"46323","summary":"зведення",
             "components":[{"code":"A1","name":"Зі збірки","serial":"SN2","quantity":"1"}]}}
            """);

        Assert.Equal("Зі збірки", Assert.Single(ErpJson.ParseSzFetch(withAssembly).Configuration).Name);
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpJsonTests`
Expected: FAIL — `ErpJson` не существует.

- [ ] **Step 3: Реализовать DTO**

`src/SzDiag.Erp/SzFetchResult.cs`:

```csharp
namespace SzDiag.Erp;

/// <summary>Строка комплектации либо состава сборки.</summary>
public sealed record ErpComponent(string Code, string Name, string Serial, string Quantity);

/// <summary>
/// Поля заявки — словарь «имя → значение» как пришло. Имена заданы чужим интерфейсом и
/// украинские; типизировать их значит ломаться при каждом переименовании на той стороне.
/// Типизировано только то, на чём стоит логика.
/// </summary>
public sealed record ErpRequest(
    string Number,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<ErpComponent> Components,
    string? OrderNumber);

public sealed record ErpOrder(
    string Number,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Products)
{
    /// <summary>
    /// Название устройства выводимо, только если товарная строка одна. Кастомная сборка —
    /// это россыпь комплектующих, из которой «устройство» однозначно не следует.
    /// </summary>
    public string? SingleProductName =>
        Products.Count == 1
        && Products[0].TryGetValue("Товар", out var name)
        && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
}

public sealed record ErpAssembly(
    string Number,
    string Summary,
    IReadOnlyList<ErpComponent> Components);

public sealed record SzFetchResult(ErpRequest Request, ErpOrder? Order, ErpAssembly? Assembly)
{
    /// <summary>
    /// Что стоит в машине: состав сборки, если это готовое решение, иначе комплектация
    /// заявки. Именно этот список потом сверяется с тем, что видит `diag run`.
    /// </summary>
    public IReadOnlyList<ErpComponent> Configuration =>
        Assembly is { Components.Count: > 0 } ? Assembly.Components : Request.Components;
}
```

- [ ] **Step 4: Реализовать разбор**

`src/SzDiag.Erp/ErpJson.cs`:

```csharp
using System.Text.Json;

namespace SzDiag.Erp;

/// <summary>Разбор ответа API в DTO. Отсутствующие узлы — не ошибка, а пустота.</summary>
public static class ErpJson
{
    public static SzFetchResult ParseSzFetch(JsonElement root) => new(
        ParseRequest(Node(root, "request")),
        ParseOrder(Node(root, "order")),
        ParseAssembly(Node(root, "assembly")));

    private static ErpRequest ParseRequest(JsonElement? node) => new(
        Str(node, "number"),
        Map(Node(node, "fields")),
        Components(Node(node, "components")),
        NullIfBlank(Str(node, "order_number")));

    private static ErpOrder? ParseOrder(JsonElement? node) => node is null ? null : new ErpOrder(
        Str(node, "number"),
        Map(Node(node, "fields")),
        Rows(Node(node, "products")));

    private static ErpAssembly? ParseAssembly(JsonElement? node) => node is null ? null : new ErpAssembly(
        Str(node, "number"),
        Str(node, "summary"),
        Components(Node(node, "components")));

    private static JsonElement? Node(JsonElement? parent, string name)
    {
        if (parent is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!obj.TryGetProperty(name, out var child)) return null;
        return child.ValueKind == JsonValueKind.Null ? null : child;
    }

    private static string Str(JsonElement? parent, string name)
        => Node(parent, name) is { } n && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";

    private static string? NullIfBlank(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Значения приводим к строке: на той стороне числа и строки перемешаны.</summary>
    private static IReadOnlyDictionary<string, string> Map(JsonElement? node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is not { ValueKind: JsonValueKind.Object } obj) return map;
        foreach (var property in obj.EnumerateObject())
            map[property.Name] = Scalar(property.Value);
        return map;
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(JsonElement? node)
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        if (node is not { ValueKind: JsonValueKind.Array } array) return rows;
        foreach (var row in array.EnumerateArray())
            rows.Add(Map(row));
        return rows;
    }

    private static IReadOnlyList<ErpComponent> Components(JsonElement? node)
    {
        var items = new List<ErpComponent>();
        if (node is not { ValueKind: JsonValueKind.Array } array) return items;
        foreach (var item in array.EnumerateArray())
            items.Add(new ErpComponent(
                Str(item, "code"), Str(item, "name"), Str(item, "serial"), Str(item, "quantity")));
        return items;
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
        _ => value.ToString(),
    };
}
```

- [ ] **Step 5: Прогнать — зелено**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpJsonTests`
Expected: PASS, 6 тестов.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Erp/SzFetchResult.cs src/SzDiag.Erp/ErpJson.cs tests/SzDiag.Erp.Tests/Fixture.cs tests/SzDiag.Erp.Tests/ErpJsonTests.cs
git commit -m "feat(erp): DTO ответа и разбор JSON"
```

---

### Task 4: `ErpBlockBuilder` — markdown-блок для `запит.md`

**Files:**
- Create: `src/SzDiag.Erp/ErpBlockBuilder.cs`
- Test: `tests/SzDiag.Erp.Tests/ErpBlockBuilderTests.cs`

**Interfaces:**
- Consumes: `SzFetchResult`, `ErpComponent`, `ErpOrder` из Task 3.
- Produces: `public static class ErpBlockBuilder` с `public static string Build(SzFetchResult data, DateTimeOffset now)`.

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Erp.Tests/ErpBlockBuilderTests.cs`:

```csharp
namespace SzDiag.Erp.Tests;

public class ErpBlockBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 14, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Блок_несёт_дату_обновления()
    {
        var block = ErpBlockBuilder.Build(ErpJson.ParseSzFetch(Fixture.SzFetch()), Now);

        Assert.Contains("2026-09-02 14:30", block);
    }

    [Fact]
    public void Поля_заявки_печатаются_как_есть()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{"Дефект":"гасне під грою","Вимога":"діагностика"},
             "components":[],"discussion":[],"order_number":null},"order":null,"assembly":null}
            """));

        var block = ErpBlockBuilder.Build(data, Now);

        Assert.Contains("**Дефект:** гасне під грою", block);
        Assert.Contains("**Вимога:** діагностика", block);
    }

    [Fact]
    public void Комплектация_печатается_таблицей_с_серийниками()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"Відеокарта X","serial":"SN000000000001","quantity":"1"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """));

        var block = ErpBlockBuilder.Build(data, Now);

        Assert.Contains("### Комплектація", block);
        Assert.Contains("| Відеокарта X | SN000000000001 | 1 |", block);
    }

    [Fact]
    public void Без_заказа_секция_состава_не_печатается()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":null},
             "order":null,"assembly":null}
            """));

        var block = ErpBlockBuilder.Build(data, Now);

        Assert.DoesNotContain("### Склад замовлення", block);
    }

    [Fact]
    public void Пустая_комплектация_даёт_пояснение_а_не_пустую_таблицу()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":null},
             "order":null,"assembly":null}
            """));

        var block = ErpBlockBuilder.Build(data, Now);

        Assert.Contains("комплектація недоступна", block);
    }

    [Fact]
    public void Переписка_в_блок_не_попадает()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],
             "discussion":[{"date":"2026-09-01","author":"оператор","text":"СЕКРЕТНЕ ЛИСТУВАННЯ"}],
             "order_number":null},"order":null,"assembly":null}
            """));

        var block = ErpBlockBuilder.Build(data, Now);

        Assert.DoesNotContain("СЕКРЕТНЕ ЛИСТУВАННЯ", block);
    }

    [Fact]
    public void Труба_в_значении_не_ломает_таблицу()
    {
        var data = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},
             "components":[{"code":"K1","name":"ОЗП 2x16 | DDR5","serial":"SN1","quantity":"2"}],
             "discussion":[],"order_number":null},"order":null,"assembly":null}
            """));

        var block = ErpBlockBuilder.Build(data, Now);

        Assert.Contains(@"ОЗП 2x16 \| DDR5", block);
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpBlockBuilderTests`
Expected: FAIL — `ErpBlockBuilder` не существует.

- [ ] **Step 3: Реализовать**

`src/SzDiag.Erp/ErpBlockBuilder.cs`:

```csharp
using System.Text;

namespace SzDiag.Erp;

/// <summary>
/// Собирает тело блока для `запит.md`. Чистая функция без файлов и сети — чтобы формат
/// заметки проверялся тестом, а не глазами в vault.
/// Контент базы знаний украинский, поэтому и заголовки секций тоже.
/// </summary>
public static class ErpBlockBuilder
{
    public static string Build(SzFetchResult data, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine($"## Дані з обліку — оновлено {now:yyyy-MM-dd HH:mm}");
        text.AppendLine();

        foreach (var (key, value) in data.Request.Fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            text.AppendLine($"**{key}:** {Inline(value)}");
        }

        AppendConfiguration(text, data);
        AppendOrder(text, data.Order);

        return text.ToString().TrimEnd();
    }

    private static void AppendConfiguration(StringBuilder text, SzFetchResult data)
    {
        text.AppendLine();
        text.AppendLine("### Комплектація");
        text.AppendLine();

        var components = data.Configuration;
        if (components.Count == 0)
        {
            // Вкладки комплектации может не быть — это состояние заявки, а не сбой.
            text.AppendLine("_комплектація недоступна для цього стану заявки_");
            return;
        }

        if (data.Assembly is { } assembly)
            text.AppendLine($"_склад збірки {Inline(assembly.Number)}_");
        text.AppendLine();
        text.AppendLine("| Компонент | Серійний номер | К-ть |");
        text.AppendLine("|---|---|---|");
        foreach (var component in components)
            text.AppendLine($"| {Cell(component.Name)} | {Cell(component.Serial)} | {Cell(component.Quantity)} |");
    }

    private static void AppendOrder(StringBuilder text, ErpOrder? order)
    {
        if (order is null || order.Products.Count == 0) return;

        text.AppendLine();
        text.AppendLine("### Склад замовлення");
        text.AppendLine();

        var columns = order.Products[0].Keys.ToList();
        text.AppendLine($"| {string.Join(" | ", columns.Select(Cell))} |");
        text.AppendLine($"|{string.Concat(columns.Select(_ => "---|"))}");
        foreach (var row in order.Products)
            text.AppendLine($"| {string.Join(" | ", columns.Select(c => Cell(row.TryGetValue(c, out var v) ? v : "")))} |");
    }

    /// <summary>Схлопывает переносы: значение поля должно остаться одной строкой.</summary>
    private static string Inline(string value)
        => value.Replace("\r", "").Replace("\n", " ").Trim();

    /// <summary>Труба в значении разъезжает markdown-таблицу — экранируем.</summary>
    private static string Cell(string value)
        => Inline(value).Replace("|", @"\|");
}
```

- [ ] **Step 4: Прогнать — зелено**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpBlockBuilderTests`
Expected: PASS, 7 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Erp/ErpBlockBuilder.cs tests/SzDiag.Erp.Tests/ErpBlockBuilderTests.cs
git commit -m "feat(erp): сборка markdown-блока для запит.md"
```

---

### Task 5: `ErpApiClient` — транспорт и разбор ошибок

**Files:**
- Create: `src/SzDiag.Erp/ErpOptions.cs`
- Create: `src/SzDiag.Erp/ErpApiException.cs`
- Create: `src/SzDiag.Erp/ErpApiClient.cs`
- Test: `tests/SzDiag.Erp.Tests/ErpApiClientTests.cs`
- Test: `tests/SzDiag.Erp.Tests/StubHandler.cs`

**Interfaces:**
- Consumes: ничего из предыдущих задач.
- Produces:
  - `public sealed class ErpOptions { string BaseUrl; string TokenFile; int TimeoutSeconds; }`
  - `public sealed class ErpApiException : Exception { public string Code { get; } }`
  - `public sealed class ErpApiClient : IDisposable` с `Task<bool> IsAliveAsync(CancellationToken)`, `Task<JsonElement> CallAsync(string name, object? args, CancellationToken)`, статикой `public static ErpApiClient Create(ErpOptions options)`
  - коды ошибок-синтетики: `"unavailable"` (сеть недоступна), `"no_token"` (нет файла токена)

- [ ] **Step 1: Написать заглушку транспорта и падающие тесты**

`tests/SzDiag.Erp.Tests/StubHandler.cs`:

```csharp
using System.Net;

namespace SzDiag.Erp.Tests;

/// <summary>Отвечает заранее заданным телом; помнит, что и в каком порядке спрашивали.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _reply;

    public List<string> Calls { get; } = new();

    public StubHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> reply) => _reply = reply;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(body);
        var (status, response) = _reply(request, body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
        };
    }
}
```

`tests/SzDiag.Erp.Tests/ErpApiClientTests.cs`:

```csharp
using System.Net;

namespace SzDiag.Erp.Tests;

public class ErpApiClientTests
{
    private static ErpApiClient ClientOver(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, "тест-токен");

    [Fact]
    public async Task Успешный_вызов_возвращает_поле_result()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.OK, """{"result":{"ok":true}}"""));
        using var client = ClientOver(handler);

        var result = await client.CallAsync("sz.fetch", new { number = "160800" });

        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Имя_и_аргументы_уезжают_в_теле_запроса()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.OK, """{"result":{}}"""));
        using var client = ClientOver(handler);

        await client.CallAsync("sz.fetch", new { number = "160800" });

        Assert.Contains("sz.fetch", handler.Calls[0]);
        Assert.Contains("160800", handler.Calls[0]);
    }

    [Fact]
    public async Task Ошибка_превращается_в_исключение_с_кодом()
    {
        using var handler = new StubHandler((_, _) =>
            (HttpStatusCode.Conflict, """{"code":"client_not_logged_in","message":"видно вікно логіну"}"""));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("client_not_logged_in", error.Code);
        Assert.Contains("вікно логіну", error.Message);
    }

    [Fact]
    public async Task Нечитаемое_тело_ошибки_не_валит_клиент()
    {
        using var handler = new StubHandler((_, _) => (HttpStatusCode.InternalServerError, "<html>500</html>"));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("internal", error.Code);
    }

    [Fact]
    public async Task Недоступный_сервис_это_не_живой_а_не_исключение()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("нет соединения"));
        using var client = ClientOver(handler);

        Assert.False(await client.IsAliveAsync());
    }

    [Fact]
    public async Task Недоступный_сервис_в_вызове_даёт_код_unavailable()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("нет соединения"));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => client.CallAsync("sz.fetch"));

        Assert.Equal("unavailable", error.Code);
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpApiClientTests`
Expected: FAIL — `ErpApiClient` не существует.

- [ ] **Step 3: Реализовать опции и исключение**

`src/SzDiag.Erp/ErpOptions.cs`:

```csharp
namespace SzDiag.Erp;

/// <summary>
/// Настройки доступа к локальному API учётной системы. Токен здесь НЕ хранится:
/// он генерируется заново при каждом запуске сервиса и читается из файла.
/// </summary>
public sealed class ErpOptions
{
    /// <summary>Слушает только localhost; порт задаётся конфигом, дефолта в коде нет.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Путь к файлу токена. Пусто — искать файл рядом с исполняемым.</summary>
    public string TokenFile { get; set; } = "";

    /// <summary>Один вызов идёт около минуты — таймаут с запасом.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public string ResolveTokenFile() => string.IsNullOrWhiteSpace(TokenFile)
        ? Path.Combine(AppContext.BaseDirectory, "api_token")
        : TokenFile;
}
```

`src/SzDiag.Erp/ErpApiException.cs`:

```csharp
namespace SzDiag.Erp;

/// <summary>
/// Сбой обращения к API. `Code` — код с той стороны либо синтетический:
/// `unavailable` (сервис не отвечает), `no_token` (нет файла токена).
/// </summary>
public sealed class ErpApiException : Exception
{
    public string Code { get; }

    public ErpApiException(string code, string message) : base(message) => Code = code;
}
```

- [ ] **Step 4: Реализовать клиент**

`src/SzDiag.Erp/ErpApiClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace SzDiag.Erp;

/// <summary>Тонкий транспорт к локальному API: один POST на вызов инструмента.</summary>
public sealed class ErpApiClient : IDisposable
{
    private const string TokenHeader = "X-Api-Token";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ErpApiClient(HttpClient http, string token, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _http.DefaultRequestHeaders.Remove(TokenHeader);
        _http.DefaultRequestHeaders.Add(TokenHeader, token);
    }

    /// <summary>Собирает клиента по конфигу, читая токен из файла.</summary>
    public static ErpApiClient Create(ErpOptions options)
    {
        if (!options.IsConfigured)
            throw new ErpApiException("unavailable", "адрес API не задан в конфиге (секция Erp).");

        var tokenFile = options.ResolveTokenFile();
        if (!File.Exists(tokenFile))
            throw new ErpApiException("no_token", $"нет файла токена: {tokenFile}");

        var http = new HttpClient
        {
            BaseAddress = new Uri(options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds),
        };
        return new ErpApiClient(http, File.ReadAllText(tokenFile).Trim(), ownsHttp: true);
    }

    /// <summary>Живость проверяется без токена: сервис может быть поднят, а токен протух.</summary>
    public async Task<bool> IsAliveAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync("/healthz", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task<JsonElement> CallAsync(string name, object? args = null, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync("/call", new { name, arguments = args ?? new { } }, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ErpApiException("unavailable", $"сервис не отвечает: {e.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ErpApiException("timeout", "вызов не уложился в таймаут.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ToException(body);

        // Тело успеха всегда объект с полем result; иначе на той стороне что-то сломалось.
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("result", out var result))
            throw new ErpApiException("internal", "в ответе нет поля result.");
        return result.Clone();
    }

    /// <summary>Тело ошибки может оказаться не-JSON (упавший сервер отдаёт html) — не падаем на разборе.</summary>
    private static ErpApiException ToException(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "internal" : "internal";
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            return new ErpApiException(code, message);
        }
        catch (JsonException)
        {
            return new ErpApiException("internal", "нечитаемый ответ сервиса.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
```

- [ ] **Step 5: Прогнать — зелено**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpApiClientTests`
Expected: PASS, 6 тестов.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Erp/ErpOptions.cs src/SzDiag.Erp/ErpApiException.cs src/SzDiag.Erp/ErpApiClient.cs tests/SzDiag.Erp.Tests/StubHandler.cs tests/SzDiag.Erp.Tests/ErpApiClientTests.cs
git commit -m "feat(erp): HTTP-клиент API и разбор ошибок"
```

---

### Task 6: `ErpSession` — захват, который всегда отпускается

**Files:**
- Create: `src/SzDiag.Erp/ErpSession.cs`
- Test: `tests/SzDiag.Erp.Tests/ErpSessionTests.cs`

**Interfaces:**
- Consumes: `ErpApiClient`, `ErpApiException` из Task 5.
- Produces: `public sealed class ErpSession : IAsyncDisposable` со статикой `public static Task<ErpSession> BeginAsync(ErpApiClient client, CancellationToken ct = default)`.

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Erp.Tests/ErpSessionTests.cs`:

```csharp
using System.Net;

namespace SzDiag.Erp.Tests;

public class ErpSessionTests
{
    private static StubHandler Ok() =>
        new((_, _) => (HttpStatusCode.OK, """{"result":{}}"""));

    private static ErpApiClient ClientOver(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost") }, "тест-токен");

    [Fact]
    public async Task Захват_берётся_и_отпускается()
    {
        using var handler = Ok();
        using var client = ClientOver(handler);

        await using (await ErpSession.BeginAsync(client)) { }

        Assert.Contains("session.begin", handler.Calls[0]);
        Assert.Contains("session.end", handler.Calls[^1]);
    }

    [Fact]
    public async Task Захват_отпускается_при_исключении_внутри()
    {
        using var handler = Ok();
        using var client = ClientOver(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var session = await ErpSession.BeginAsync(client);
            throw new InvalidOperationException("что-то пошло не так");
        });

        Assert.Contains("session.end", handler.Calls[^1]);
    }

    [Fact]
    public async Task Повторный_Dispose_не_шлёт_второй_end()
    {
        using var handler = Ok();
        using var client = ClientOver(handler);

        var session = await ErpSession.BeginAsync(client);
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, handler.Calls.Count(c => c.Contains("session.end")));
    }

    [Fact]
    public async Task Занятый_захват_пробрасывается_наружу()
    {
        using var handler = new StubHandler((_, _) =>
            (HttpStatusCode.Conflict, """{"code":"busy","message":"вже йде сесія"}"""));
        using var client = ClientOver(handler);

        var error = await Assert.ThrowsAsync<ErpApiException>(() => ErpSession.BeginAsync(client));

        Assert.Equal("busy", error.Code);
    }

    [Fact]
    public async Task Сбой_освобождения_не_валит_вызывающий_код()
    {
        // Сервис умер посреди работы: end не пройдёт, но исключение из Dispose затмило бы
        // настоящую причину сбоя.
        var calls = 0;
        using var handler = new StubHandler((_, body) =>
        {
            calls++;
            return body.Contains("session.end")
                ? (HttpStatusCode.InternalServerError, """{"code":"internal","message":"впав"}""")
                : (HttpStatusCode.OK, """{"result":{}}""");
        });
        using var client = ClientOver(handler);

        var session = await ErpSession.BeginAsync(client);
        await session.DisposeAsync();

        Assert.Equal(2, calls);
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpSessionTests`
Expected: FAIL — `ErpSession` не существует.

- [ ] **Step 3: Реализовать**

`src/SzDiag.Erp/ErpSession.cs`:

```csharp
namespace SzDiag.Erp;

/// <summary>
/// Монопольный захват учётной программы на время работы. Захват обязан отпускаться:
/// иначе следующий вызов упрётся в занятость, а у человека останутся свёрнутые окна
/// и чужой фильтр в панели поиска. Поэтому освобождение висит и на Dispose, и на Ctrl+C.
/// </summary>
public sealed class ErpSession : IAsyncDisposable
{
    public const string BeginTool = "session.begin";
    public const string EndTool = "session.end";

    private readonly ErpApiClient _client;
    private readonly ConsoleCancelEventHandler _onCancel;
    private int _ended;

    private ErpSession(ErpApiClient client)
    {
        _client = client;
        // Ctrl+C посреди минутного вызова не должен оставить программу захваченной.
        // Процесс завершится штатно после обработчика — e.Cancel не трогаем.
        _onCancel = (_, _) => Release();
        Console.CancelKeyPress += _onCancel;
    }

    public static async Task<ErpSession> BeginAsync(ErpApiClient client, CancellationToken ct = default)
    {
        await client.CallAsync(BeginTool, ct: ct);
        return new ErpSession(client);
    }

    public async ValueTask DisposeAsync()
    {
        Console.CancelKeyPress -= _onCancel;
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;

        try { await _client.CallAsync(EndTool); }
        catch (ErpApiException) { /* сервис уже мёртв: настоящую причину сбоя не затмеваем */ }
    }

    /// <summary>Синхронное освобождение для обработчика Ctrl+C — ждать там нечем.</summary>
    private void Release()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try { _client.CallAsync(EndTool).GetAwaiter().GetResult(); }
        catch (ErpApiException) { /* см. выше */ }
    }
}
```

- [ ] **Step 4: Прогнать — зелено**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~ErpSessionTests`
Expected: PASS, 5 тестов.

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Erp/ErpSession.cs tests/SzDiag.Erp.Tests/ErpSessionTests.cs
git commit -m "feat(erp): сессия захвата с гарантированным освобождением"
```

---

### Task 7: `SzFetchWriter` — запись в базу знаний

**Files:**
- Create: `src/SzDiag.Erp/SzFetchWriter.cs`
- Test: `tests/SzDiag.Erp.Tests/SzFetchWriterTests.cs`

**Interfaces:**
- Consumes: `SzFetchResult` (Task 3), `ErpBlockBuilder` (Task 4), `MarkedBlock` (Task 2); из `SzDiag.Kb`: `KbPaths`, `KnowledgeBaseScaffolder`, `FrontmatterEditor`, `EntityNoteWriter`, `SzJournal`, `JournalEntry`, `JournalSource`.
- Produces:
  - `public sealed record SzFetchWriteResult(string JsonPath, string RequestPath, bool DeviceSet, string? DeviceSkipReason)`
  - `public sealed class SzFetchWriter` с конструктором `(string kbRoot, Func<DateTimeOffset>? now = null)` и методом `public SzFetchWriteResult Write(string sz, SzFetchResult data, string rawJson, bool force)`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Erp.Tests/SzFetchWriterTests.cs`:

```csharp
using SzDiag.Kb;

namespace SzDiag.Erp.Tests;

public class SzFetchWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "erp-tests-" + Guid.NewGuid().ToString("N"));
    private readonly KbPaths _paths;

    public SzFetchWriterTests() => _paths = new KbPaths(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* временная папка */ }
    }

    private SzFetchWriter Writer() => new(_root, () => new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.FromHours(3)));

    private static SzFetchResult Data(string orderNumber = "1951256", string product = "ПК Ігровий") =>
        ErpJson.ParseSzFetch(Fixture.Of($$"""
            {"request":{"number":"160800","fields":{"Дефект":"гасне під грою"},
             "components":[{"code":"K1","name":"Відеокарта X","serial":"SN000000000001","quantity":"1"}],
             "discussion":[],"order_number":"{{orderNumber}}"},
             "order":{"number":"{{orderNumber}}","fields":{},
             "products":[{"Код":"1","Товар":"{{product}}","Кіл-ть":"1"}],"service_requests":[]},
             "assembly":null}
            """));

    [Fact]
    public void Сырой_ответ_ложится_в_папку_СЗ()
    {
        var result = Writer().Write("160800", Data(), """{"сирий":"json"}""", force: false);

        Assert.True(File.Exists(result.JsonPath));
        Assert.Contains("сирий", File.ReadAllText(result.JsonPath));
    }

    [Fact]
    public void Блок_попадает_в_запит()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        var request = File.ReadAllText(_paths.Request("160800"));
        Assert.Contains(MarkedBlock.Begin, request);
        Assert.Contains("SN000000000001", request);
    }

    [Fact]
    public void Ручной_текст_переживает_повторный_фетч()
    {
        Writer().Write("160800", Data(), "{}", force: false);
        File.AppendAllText(_paths.Request("160800"), "\nруками: перевірив БЖ, тримає\n");

        Writer().Write("160800", Data(), "{}", force: false);

        Assert.Contains("руками: перевірив БЖ, тримає", File.ReadAllText(_paths.Request("160800")));
    }

    [Fact]
    public void Пустой_frontmatter_заполняется()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        var home = FrontmatterEditor.Load(File.ReadAllText(_paths.HomeNote("160800")));
        Assert.Equal("1951256", home.GetScalar("замовлення")?.Trim('"'));
        Assert.Equal("ПК Ігровий", home.GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Заполненный_frontmatter_не_перебивается_без_force()
    {
        Writer().Write("160800", Data(), "{}", force: false);
        var edited = FrontmatterEditor.Load(File.ReadAllText(_paths.HomeNote("160800")));
        edited.SetScalar("пристрій", "\"Ноутбук вручну\"");
        File.WriteAllText(_paths.HomeNote("160800"), edited.Serialize());

        Writer().Write("160800", Data(product: "ПК Інший"), "{}", force: false);

        var home = FrontmatterEditor.Load(File.ReadAllText(_paths.HomeNote("160800")));
        Assert.Equal("Ноутбук вручну", home.GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Force_перебивает_заполненное_поле()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Writer().Write("160800", Data(product: "ПК Інший"), "{}", force: true);

        var home = FrontmatterEditor.Load(File.ReadAllText(_paths.HomeNote("160800")));
        Assert.Equal("ПК Інший", home.GetScalar("пристрій")?.Trim('"'));
    }

    [Fact]
    public void Устройство_не_ставится_если_в_заказе_несколько_позиций()
    {
        var custom = ErpJson.ParseSzFetch(Fixture.Of("""
            {"request":{"number":"160800","fields":{},"components":[],"discussion":[],"order_number":"1951256"},
             "order":{"number":"1951256","fields":{},"products":[
               {"Код":"1","Товар":"Материнська плата","Кіл-ть":"1"},
               {"Код":"2","Товар":"Процесор","Кіл-ть":"1"}],"service_requests":[]},"assembly":null}
            """));

        var result = Writer().Write("160800", custom, "{}", force: false);

        Assert.False(result.DeviceSet);
        Assert.Contains("2", result.DeviceSkipReason);
    }

    [Fact]
    public void Заводятся_заметки_заказа_и_устройства()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Assert.True(File.Exists(_paths.OrderNote("1951256")));
        Assert.True(File.Exists(_paths.DeviceNote("ПК Ігровий")));
    }

    [Fact]
    public void Компоненты_заметками_не_плодятся()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Assert.False(File.Exists(_paths.ComponentNote("Відеокарта X")));
    }

    [Fact]
    public void В_журнал_ложится_строка()
    {
        Writer().Write("160800", Data(), "{}", force: false);

        Assert.Contains("обліку", File.ReadAllText(_paths.Journal("160800")));
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~SzFetchWriterTests`
Expected: FAIL — `SzFetchWriter` не существует.

- [ ] **Step 3: Реализовать**

`src/SzDiag.Erp/SzFetchWriter.cs`:

```csharp
using SzDiag.Kb;

namespace SzDiag.Erp;

/// <param name="DeviceSet">Поле `пристрій` заполнено.</param>
/// <param name="DeviceSkipReason">Почему не заполнено — печатается человеку.</param>
public sealed record SzFetchWriteResult(
    string JsonPath,
    string RequestPath,
    bool DeviceSet,
    string? DeviceSkipReason);

/// <summary>
/// Раскладывает ответ API по базе знаний. Всё, что пишется, либо лежит в своём файле
/// (сырой json), либо в своём блоке (запит.md), либо заполняет пустое место
/// (frontmatter) — руками написанное не трогается.
/// </summary>
public sealed class SzFetchWriter
{
    private readonly string _kbRoot;
    private readonly KbPaths _paths;
    private readonly Func<DateTimeOffset> _now;

    public SzFetchWriter(string kbRoot, Func<DateTimeOffset>? now = null)
    {
        _kbRoot = kbRoot;
        _paths = new KbPaths(kbRoot);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public SzFetchWriteResult Write(string sz, SzFetchResult data, string rawJson, bool force)
    {
        new KnowledgeBaseScaffolder(_kbRoot, _now).EnsureSkeleton(sz);

        var jsonPath = Path.Combine(_paths.SzDir(sz), "erp.json");
        File.WriteAllText(jsonPath, rawJson);

        var requestPath = _paths.Request(sz);
        var existing = File.Exists(requestPath) ? File.ReadAllText(requestPath) : "";
        File.WriteAllText(requestPath, MarkedBlock.Upsert(existing, ErpBlockBuilder.Build(data, _now())));

        var (deviceSet, skipReason) = UpdateFrontmatter(sz, data, force);
        WriteEntities(data, deviceSet);

        new SzJournal(_paths).Append(sz, new JournalEntry(
            _now(), JournalSource.Command, "дані підтягнуто з обліку (sz fetch)"));

        return new SzFetchWriteResult(jsonPath, requestPath, deviceSet, skipReason);
    }

    private (bool DeviceSet, string? SkipReason) UpdateFrontmatter(string sz, SzFetchResult data, bool force)
    {
        var homePath = _paths.HomeNote(sz);
        var home = FrontmatterEditor.Load(File.ReadAllText(homePath));

        var order = data.Request.OrderNumber ?? data.Order?.Number;
        if (!string.IsNullOrWhiteSpace(order) && (force || IsBlank(home.GetScalar("замовлення"))))
            home.SetScalar("замовлення", Quote(order));

        var deviceSet = false;
        string? skipReason = null;
        var device = data.Order?.SingleProductName;
        if (device is null)
        {
            var count = data.Order?.Products.Count ?? 0;
            skipReason = count == 0
                ? "у заявці немає замовлення"
                : $"у замовленні {count} позицій — складання під замовлення";
        }
        else if (force || IsBlank(home.GetScalar("пристрій")))
        {
            home.SetScalar("пристрій", Quote(device));
            deviceSet = true;
        }
        else
        {
            skipReason = "поле вже заповнене (перебити — --force)";
        }

        File.WriteAllText(homePath, home.Serialize());
        return (deviceSet, skipReason);
    }

    private void WriteEntities(SzFetchResult data, bool deviceSet)
    {
        var entities = new EntityNoteWriter(_paths);

        var order = data.Request.OrderNumber ?? data.Order?.Number;
        if (!string.IsNullOrWhiteSpace(order)) entities.EnsureOrder(order);

        // Компоненты заметками не заводим: каждая сборка дала бы 8-10 однодневок,
        // и поиск по vault утонул бы в них.
        if (deviceSet && data.Order?.SingleProductName is { } device) entities.EnsureDevice(device);
    }

    /// <summary>Скаффолдер пишет пустые значения как `""` — это тоже «пусто».</summary>
    private static bool IsBlank(string? raw)
        => string.IsNullOrWhiteSpace(raw) || raw.Trim() is "\"\"" or "''";

    private static string Quote(string value) => $"\"{value.Replace("\"", "'")}\"";
}
```

- [ ] **Step 4: Прогнать — зелено**

Run: `dotnet test tests/SzDiag.Erp.Tests --filter FullyQualifiedName~SzFetchWriterTests`
Expected: PASS, 10 тестов.

- [ ] **Step 5: Прогнать весь проект**

Run: `dotnet test tests/SzDiag.Erp.Tests`
Expected: PASS, все тесты.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Erp/SzFetchWriter.cs tests/SzDiag.Erp.Tests/SzFetchWriterTests.cs
git commit -m "feat(erp): запись ответа в базу знаний"
```

---

### Task 8: Команды `szcli sz fetch` и `szcli sz release`

**Files:**
- Create: `src/SzDiag.Cli/ErpCommand.cs`
- Test: `tests/SzDiag.Cli.Tests/ErpExitCodeTests.cs`
- Modify: `src/SzDiag.Cli/SzDiag.Cli.csproj` (ссылка на `SzDiag.Erp`)
- Modify: `src/SzDiag.Cli/CliOptions.cs` (секция `Erp`)
- Modify: `src/SzDiag.Cli/CliCommands.cs` (команда `sz` в `Known`)
- Modify: `src/SzDiag.Cli/Program.cs` (диспетчеризация, валидация номера, usage)
- Modify: `src/SzDiag.Cli/appsettings.json`

**Interfaces:**
- Consumes: `ErpOptions`, `ErpApiClient`, `ErpApiException`, `ErpSession`, `ErpJson`, `SzFetchWriter`, `SzFetchWriteResult` из Task 3–7.
- Produces: `public static class ErpCommand` с `public static Task<int> RunAsync(string[] args, CliOptions options)` и `public static int ExitCodeFor(string apiCode)`.

- [ ] **Step 1: Написать падающий тест на карту кодов возврата**

`tests/SzDiag.Cli.Tests/ErpExitCodeTests.cs`:

```csharp
namespace SzDiag.Cli.Tests;

public class ErpExitCodeTests
{
    [Theory]
    [InlineData("unavailable", 3)]
    [InlineData("no_token", 3)]
    [InlineData("client_not_running", 4)]
    [InlineData("client_not_logged_in", 4)]
    [InlineData("busy", 5)]
    [InlineData("not_found", 6)]
    [InlineData("ambiguous", 6)]
    [InlineData("anchor_missing", 7)]
    [InlineData("timeout", 1)]
    [InlineData("internal", 1)]
    [InlineData("что-то новое", 1)]
    public void Код_апи_превращается_в_код_возврата(string apiCode, int expected)
        => Assert.Equal(expected, ErpCommand.ExitCodeFor(apiCode));
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~ErpExitCodeTests`
Expected: FAIL — `ErpCommand` не существует.

- [ ] **Step 3: Подключить проект и конфиг**

В `src/SzDiag.Cli/SzDiag.Cli.csproj` — в тот же `ItemGroup`, где остальные `ProjectReference`:

```xml
    <ProjectReference Include="..\SzDiag.Erp\SzDiag.Erp.csproj" />
```

В `src/SzDiag.Cli/CliOptions.cs` — добавить свойство (и `using SzDiag.Erp;` вверху файла):

```csharp
    /// <summary>Доступ к локальному API учётной системы (`szcli sz fetch`).</summary>
    public ErpOptions Erp { get; set; } = new();
```

В `src/SzDiag.Cli/appsettings.json` — добавить секцию:

```json
  "Erp": {
    "BaseUrl": "",
    "TokenFile": "",
    "TimeoutSeconds": 180
  }
```

В `src/SzDiag.Cli/CliCommands.cs` — дописать `"sz"` в массив `Known`:

```csharp
        "client", "maintenance", "agent", "sz",
```

- [ ] **Step 4: Реализовать команду**

`src/SzDiag.Cli/ErpCommand.cs`:

```csharp
using System.Text.Json;
using Spectre.Console;
using SzDiag.Erp;

namespace SzDiag.Cli;

/// <summary>
/// `szcli sz fetch <СЗ>` — подтянуть данные заявки из учётной системы в базу знаний.
/// `szcli sz release` — отпустить залипший захват.
/// </summary>
public static class ErpCommand
{
    /// <summary>
    /// Код API → код возврата. Разные причины сбоя различаются кодом, потому что
    /// «не запущено» и «занято» лечатся по-разному.
    /// </summary>
    public static int ExitCodeFor(string apiCode) => apiCode switch
    {
        "unavailable" or "no_token" => 3,
        "client_not_running" or "client_not_logged_in" => 4,
        "busy" => 5,
        "not_found" or "ambiguous" => 6,
        // Ловили на первом же живом съёме: захват берётся, логин на месте, а навигация по
        // разделам не находится. Сбой не наш и лечится не так, как «не запущено» — свой код.
        "anchor_missing" => 7,
        _ => 1,
    };

    public static async Task<int> RunAsync(string[] args, CliOptions options)
    {
        var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (sub is "--help" or "-h" or "help" or "")
        {
            PrintUsage();
            return sub == "" ? 2 : 0;
        }

        return sub switch
        {
            "fetch" when args.Length >= 2 => await FetchAsync(args[1], args.Contains("--force"), options),
            "release" => await ReleaseAsync(options),
            _ => Unknown(),
        };

        static int Unknown()
        {
            PrintUsage();
            return 2;
        }
    }

    private static async Task<int> FetchAsync(string sz, bool force, CliOptions options)
    {
        ErpApiClient client;
        try { client = ErpApiClient.Create(options.Erp); }
        catch (ErpApiException e) { return Fail(e); }

        using (client)
        {
            if (!await client.IsAliveAsync())
            {
                AnsiConsole.MarkupLine("[red]API учётной системы не отвечает.[/] "
                    + "Проверь, что сервис поднят, а адрес в секции [grey]Erp[/] конфига верный.");
                return 3;
            }

            // Автоматизация кликает физически: увёл мышь — увёл клик.
            AnsiConsole.MarkupLine("[yellow]Идёт обращение к учётной системе (~1 минута). "
                + "Не трогай мышь и клавиатуру.[/]");

            JsonElement result;
            try
            {
                await using var session = await ErpSession.BeginAsync(client);
                result = await client.CallAsync("sz.fetch", new { number = sz });
            }
            catch (ErpApiException e) { return Fail(e); }

            var raw = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });

            var data = ErpJson.ParseSzFetch(result);
            var written = new SzFetchWriter(options.KbRoot).Write(sz, data, raw, force);
            Print(sz, data, written);
            return 0;
        }
    }

    /// <summary>Захват снаружи иначе не снять: прибитый процесс оставляет его висеть.</summary>
    private static async Task<int> ReleaseAsync(CliOptions options)
    {
        ErpApiClient client;
        try { client = ErpApiClient.Create(options.Erp); }
        catch (ErpApiException e) { return Fail(e); }

        using (client)
        {
            try { await client.CallAsync(ErpSession.EndTool); }
            catch (ErpApiException e) { return Fail(e); }
            AnsiConsole.MarkupLine("Захват отпущен.");
            return 0;
        }
    }

    private static void Print(string sz, SzFetchResult data, SzFetchWriteResult written)
    {
        AnsiConsole.MarkupLineInterpolated($"СЗ {sz}: записано в базу знаний.");
        AnsiConsole.MarkupLineInterpolated($"  сырой ответ: {written.JsonPath}");

        if (data.Request.Fields.TryGetValue("Дефект", out var defect) && !string.IsNullOrWhiteSpace(defect))
            AnsiConsole.MarkupLineInterpolated($"  дефект: {defect}");
        if (data.Request.OrderNumber is { } order)
            AnsiConsole.MarkupLineInterpolated($"  заказ: {order}");
        if (written.DeviceSkipReason is { } reason)
            AnsiConsole.MarkupLineInterpolated($"  пристрій не визначено: {reason}");

        if (data.Configuration.Count == 0) return;

        var table = new Table().AddColumns("Компонент", "Серийник", "К-во");
        foreach (var component in data.Configuration)
            table.AddRow(
                Markup.Escape(component.Name),
                Markup.Escape(component.Serial),
                Markup.Escape(component.Quantity));
        AnsiConsole.Write(table);
    }

    private static int Fail(ErpApiException e)
    {
        var code = ExitCodeFor(e.Code);
        var hint = code switch
        {
            3 => "Сервис не поднят либо нет файла токена.",
            4 => "Запусти учётную программу и войди в неё, потом повтори.",
            5 => "Захват занят — отпусти его: szcli sz release",
            6 => "Заявка не найдена либо совпадений больше одного.",
            7 => "Интерфейс учётной программы не распознан: разверни её главное окно "
                 + "на главном экране навигации и повтори. Если не помогло — правка на "
                 + "стороне сервиса API, не здесь.",
            _ => "",
        };
        AnsiConsole.MarkupLineInterpolated($"[red]Сбой обращения к учётной системе[/] ({e.Code}): {e.Message}");
        if (hint.Length > 0) AnsiConsole.MarkupLineInterpolated($"  {hint}");
        return code;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Использование:
          szcli sz fetch <СЗ> [--force]   подтянуть данные заявки из учётной системы в kb
          szcli sz release                отпустить залипший захват учётной программы

        --force перебивает уже заполненные поля frontmatter (по умолчанию не трогаются).
        """);
}
```

- [ ] **Step 5: Подключить команду в `Program.cs`**

В блоке `var szArgIndex = command switch` — добавить ветку перед `_ => -1`:

```csharp
    // szcli sz fetch <СЗ>: номер третий. У `sz release` номера нет — ветка не сработает.
    "sz" when args.Length >= 3 && args[1].Equals("fetch", StringComparison.OrdinalIgnoreCase) => 2,
```

Рядом с `case "kb"` в основном `switch` — добавить:

```csharp
    case "sz" when args.Length >= 2:
        return await ErpCommand.RunAsync(args[1..], options);
```

В `PrintUsage()` — добавить строки в список команд:

```
  sz fetch <СЗ> [--force]      подтянуть данные заявки из учётной системы в kb
  sz release                   отпустить залипший захват учётной программы
```

- [ ] **Step 6: Прогнать тесты и сборку**

Run: `dotnet build`
Expected: без ошибок.

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~ErpExitCodeTests`
Expected: PASS, 10 кейсов.

- [ ] **Step 7: Проверить справку и неизвестные аргументы вручную**

```powershell
dotnet run --project src/SzDiag.Cli -- sz --help
dotnet run --project src/SzDiag.Cli -- sz fetch 12
dotnet run --project src/SzDiag.Cli -- sz крякозябра
```

Ожидание: справка и код 0; «Неверный номер СЗ» и код 2; справка и код 2.

- [ ] **Step 8: Коммит**

```bash
git add src/SzDiag.Cli tests/SzDiag.Cli.Tests/ErpExitCodeTests.cs
git commit -m "feat(cli): команды sz fetch и sz release"
```

---

### Task 9: Сборка dist, документация, живой чек-лист

**Files:**
- Modify: `tools/build-dist.ps1`
- Modify: `CLAUDE.md`
- Modify: `docs/dev-knowledge-base.md`
- Create: `docs/live-checklist-2026-09-02.md`

**Interfaces:**
- Consumes: команды из Task 8, секцию конфига `Erp`.
- Produces: параметры `-ErpPort` и `-ErpTokenFile` у `build-dist.ps1`.

- [ ] **Step 1: Добавить параметры в `build-dist.ps1`**

В блок `param(...)` (строка 34) дописать:

```powershell
    [int]$ErpPort = 0,
    [string]$ErpTokenFile = "",
```

- [ ] **Step 2: Прописать секцию `Erp` в конфиг CLI**

Заменить формирование `$cliCfg` (около строки 314) на вариант с секцией. Пустой `BaseUrl` = команда `sz fetch` скажет, что адрес не задан, вместо попытки стучаться в никуда:

```powershell
$erpBaseUrl = if ($ErpPort -gt 0) { "http://127.0.0.1:$ErpPort" } else { "" }
$erpToken = $ErpTokenFile -replace '\\', '\\'
$cliCfg = @"
{
  "HubBaseUrl": "http://localhost:$Port",
  "ManagementToken": "$Token",
  "KbRoot": "$kb",
  "SshKeyPath": "$sshKeyAbs",
  "Erp": {
    "BaseUrl": "$erpBaseUrl",
    "TokenFile": "$erpToken",
    "TimeoutSeconds": 180
  }
}
"@
```

- [ ] **Step 3: Проверить, что dist собирается и конфиг валиден**

```powershell
.\tools\build-dist.ps1 -ErpPort 8765 -ErpTokenFile C:\path\to\api_token
Get-Content dist\host\cli\appsettings.json | ConvertFrom-Json | Select-Object -ExpandProperty Erp
```

Ожидание: объект с `BaseUrl`, `TokenFile`, `TimeoutSeconds`.

- [ ] **Step 4: Написать живой чек-лист**

`docs/live-checklist-2026-09-02.md` — что проверить на реальной заявке:

```markdown
# Живой чек-лист: `szcli sz fetch` (2026-09-02)

Требует запущенных учётной программы (с логином) и сервиса API на том же боксе.

- [ ] `szcli sz fetch <живая СЗ>` — код 0, в `kb/СЗ/<номер>/` появились `erp.json`
      и блок в `запит.md`.
- [ ] Состояние учётной программы вернулось: активная вкладка та же, фильтры те же,
      чужие окна документов не закрыты.
- [ ] Дописать в `запит.md` строку руками → повторить фетч → строка на месте,
      блок обновился, маркеры не задвоились.
- [ ] frontmatter в `<sz>.md`: `замовлення` и `пристрій` заполнены; на кастомной сборке
      `пристрій` пуст, а в консоли причина.
- [ ] `szcli sz fetch <СЗ>` при закрытой учётной программе → код 4 и внятный текст.
- [ ] Ctrl+C посреди вызова → следующий `szcli sz fetch` не упирается в занятость.
- [ ] `szcli sz release` на свободном захвате не роняет команду.
- [ ] Серийники из блока сверить глазами с `szcli diag run <СЗ> storage` — расхождения
      записать в `діагностика.md`.
```

- [ ] **Step 5: Обновить документацию проекта**

В `CLAUDE.md`, в описание `SzDiag.Cli`, добавить абзац:

```
  `szcli sz fetch <СЗ> [--force]` / `szcli sz release` — подтянуть данные заявки из
  локального API учётной системы в kb: сырой ответ в `kb/СЗ/<номер>/erp.json`, блок под
  маркерами `erp:початок`/`erp:кінець` в `запит.md`, пустые поля frontmatter, заметки
  заказа и устройства. Повторный вызов переписывает только свой блок — руками написанное
  не трогается. Вызов идёт около минуты и кликает по чужому интерфейсу физически, поэтому
  запускается только руками, никогда фоном. Спека —
  [docs/superpowers/specs/2026-09-02-erp-sz-fetch-design.md](docs/superpowers/specs/2026-09-02-erp-sz-fetch-design.md).
```

В списке проектов добавить:

```
- **SzDiag.Erp** — доступ к локальному API учётной системы (клиент, сессия захвата, DTO,
  запись в kb). Зависит от `SzDiag.Kb`; конкретика API — в локальной доке `docs/erp-api.md`,
  вне git (репозиторий публичный).
```

В `docs/dev-knowledge-base.md` — добавить раздел про команду `sz` рядом с описанием
остальных команд CLI: подкоманды, коды возврата 0/1/2/3/4/5/6/7, где лежат артефакты.

- [ ] **Step 6: Прогнать всё**

Run: `dotnet build`
Expected: без ошибок.

Run: `dotnet test`
Expected: PASS, все тесты (было ~481, стало примерно на 40 больше).

- [ ] **Step 7: Коммит**

```bash
git add tools/build-dist.ps1 CLAUDE.md docs/dev-knowledge-base.md docs/live-checklist-2026-09-02.md
git commit -m "feat(erp): сборка dist, документация, живой чек-лист"
```

- [ ] **Step 8: Живой прогон**

Пройти `docs/live-checklist-2026-09-02.md` на реальной заявке. Каждую грабку, из-за которой пришлось лезть руками, — в `docs/dev-backlog.md` в тот же заход, с цифрами и кодами ошибок.
