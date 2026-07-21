# Апдейтер клиента (SzDiag.Updater) — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Тонкий `Updater.exe` — точка входа на клиенте, которая находит hub, сверяет версию, при необходимости качает свежий пакет агента и запускает `agent.exe`. Убирает ручной цикл раздачи через share.

**Architecture:** Новый проект `SzDiag.Updater` (console, net8). Hub раздаёт готовый пакет (`version.txt` + `package.zip` + `package.sha256`) из папки `agent-dist` через `/agent/*` эндпоинты под `X-SzDiag-Token`. `build-dist.ps1` собирает пакет и Updater. `HubDiscovery` выносится из `SzDiag.Agent` в `SzDiag.Contracts`, чтобы Updater и Agent делили одну реализацию.

**Tech Stack:** C# / net8.0, ASP.NET Core minimal API (hub), `System.Net.Http` (updater), `System.IO.Compression` (zip), xUnit, PowerShell (build-dist).

**Спека:** `docs/superpowers/specs/2026-07-21-agent-updater-design.md`

---

## File Structure

**Рефактор:**
- `src/SzDiag.Contracts/HubDiscovery.cs` — перемещён из `SzDiag.Agent` (namespace → `SzDiag.Contracts`).

**Hub (раздача пакета):**
- `src/SzDiag.Contracts/HubRoutes.cs` — +константы путей `/agent/*` (Modify).
- `src/SzDiag.Hub/HubOptions.cs` — +`AgentDistRoot` (Modify).
- `src/SzDiag.Hub/AgentPackageApi.cs` — новые эндпоинты (Create).
- `src/SzDiag.Hub/Program.cs` — регистрация `MapAgentPackageApi` (Modify).
- `tests/SzDiag.Hub.Tests/AgentPackageApiTests.cs` — тесты эндпоинтов (Create).

**Updater (новый проект):**
- `src/SzDiag.Updater/SzDiag.Updater.csproj` (Create)
- `src/SzDiag.Updater/UpdaterOptions.cs` (Create)
- `src/SzDiag.Updater/IUpdateClient.cs` + `HttpUpdateClient.cs` (Create)
- `src/SzDiag.Updater/Hashing.cs` — sha256 файла (Create)
- `src/SzDiag.Updater/PackageApplier.cs` (Create)
- `src/SzDiag.Updater/AgentLauncher.cs` (Create)
- `src/SzDiag.Updater/Program.cs` — оркестрация (Create)
- `src/SzDiag.Updater/appsettings.json` — шаблон (Create)
- `tests/SzDiag.Updater.Tests/SzDiag.Updater.Tests.csproj` (Create)
- `tests/SzDiag.Updater.Tests/HttpUpdateClientTests.cs` (Create)
- `tests/SzDiag.Updater.Tests/PackageApplierTests.cs` (Create)

**Сборка:**
- `tools/build-dist.ps1` — version.txt, package.zip, agent-dist, publish Updater (Modify).

---

## Task 1: Вынести HubDiscovery в SzDiag.Contracts

**Files:**
- Move: `src/SzDiag.Agent/HubDiscovery.cs` → `src/SzDiag.Contracts/HubDiscovery.cs`
- Modify: `src/SzDiag.Agent/Program.cs:90` (using)
- Modify: `tests/SzDiag.Agent.Tests/HubDiscoveryTests.cs` (using)

- [ ] **Step 1: Переместить файл и сменить namespace**

Скопировать содержимое `src/SzDiag.Agent/HubDiscovery.cs` в новый `src/SzDiag.Contracts/HubDiscovery.cs`, удалить старый. Сменить строку namespace (и убрать теперь лишний `using SzDiag.Contracts;` — файл уже в этом namespace):

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SzDiag.Contracts;

/// <summary>hub не откликнулся на автообнаружение за отведённое время.</summary>
public sealed class HubNotFoundException : Exception
{
    public HubNotFoundException(string message) : base(message) { }
}

/// <summary>Находит hub в локальной сети через UDP-broadcast (см. DiscoveryProtocol).</summary>
public static class HubDiscovery
{
    // ... тело без изменений (как в исходном файле) ...
}
```

Удалить `src/SzDiag.Agent/HubDiscovery.cs`.

- [ ] **Step 2: Добавить using в агенте**

`src/SzDiag.Agent/Program.cs` — вверху файла (после существующих using) добавить `using SzDiag.Contracts;`, если его там ещё нет. `HubDiscovery.FindHubAsync` на строке ~90 теперь резолвится из Contracts.

- [ ] **Step 3: Обновить тест discovery**

`tests/SzDiag.Agent.Tests/HubDiscoveryTests.cs` — заменить `using SzDiag.Agent;` на (или добавить) `using SzDiag.Contracts;`. Тело тестов не меняется.

- [ ] **Step 4: Собрать и прогнать тесты**

Run: `dotnet build && dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HubDiscovery`
Expected: PASS (те же тесты, новый namespace).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Contracts/HubDiscovery.cs src/SzDiag.Agent/Program.cs tests/SzDiag.Agent.Tests/HubDiscoveryTests.cs
git rm src/SzDiag.Agent/HubDiscovery.cs
git commit -m "refactor: HubDiscovery в Contracts (общая для Agent и Updater)"
```

---

## Task 2: Константы путей и AgentDistRoot

**Files:**
- Modify: `src/SzDiag.Contracts/HubRoutes.cs`
- Modify: `src/SzDiag.Hub/HubOptions.cs`

- [ ] **Step 1: Добавить константы путей апдейта в HubRoutes**

`src/SzDiag.Contracts/HubRoutes.cs` — после строки `public const string RunDiag = nameof(RunDiag);` добавить:

```csharp
    // Апдейтер клиента: раздача пакета агента (HTTP, под TokenHeader).
    public const string AgentApiPrefix = "/agent";
    public const string AgentVersionRoute = "/agent/version";
    public const string AgentPackageRoute = "/agent/package";
    public const string AgentPackageSha256Route = "/agent/package.sha256";
```

- [ ] **Step 2: Добавить AgentDistRoot в HubOptions**

`src/SzDiag.Hub/HubOptions.cs` — после свойства `Port` добавить:

```csharp
    /// <summary>Папка, из которой hub раздаёт пакет агента апдейтеру
    /// (version.txt, package.zip, package.sha256). Кладётся build-dist.</summary>
    public string AgentDistRoot { get; set; } = "agent-dist";
```

- [ ] **Step 3: Собрать**

Run: `dotnet build src/SzDiag.Hub`
Expected: SUCCESS.

- [ ] **Step 4: Commit**

```bash
git add src/SzDiag.Contracts/HubRoutes.cs src/SzDiag.Hub/HubOptions.cs
git commit -m "feat(hub): константы /agent/* и AgentDistRoot для раздачи пакета"
```

---

## Task 3: Hub — эндпоинты раздачи пакета

**Files:**
- Create: `src/SzDiag.Hub/AgentPackageApi.cs`
- Modify: `src/SzDiag.Hub/Program.cs`
- Create: `tests/SzDiag.Hub.Tests/AgentPackageApiTests.cs`

- [ ] **Step 1: Написать эндпоинты**

Create `src/SzDiag.Hub/AgentPackageApi.cs`:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Раздача пакета агента апдейтеру (SzDiag.Updater). Файлы берутся из
/// HubOptions.AgentDistRoot (кладёт build-dist). Аутентификация — тот же AgentToken,
/// что у агента (заголовок HubRoutes.TokenHeader).</summary>
public static class AgentPackageApi
{
    public static void MapAgentPackageApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(HubRoutes.AgentApiPrefix).AddEndpointFilter(async (ctx, next) =>
        {
            var opts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<HubOptions>>().Value;
            var provided = ctx.HttpContext.Request.Headers[HubRoutes.TokenHeader].ToString();
            if (string.IsNullOrEmpty(opts.AgentToken) || provided != opts.AgentToken)
                return Results.Unauthorized();
            return await next(ctx);
        });

        group.MapGet("/version", (IOptions<HubOptions> opts) =>
        {
            var path = Path.Combine(opts.Value.AgentDistRoot, "version.txt");
            return File.Exists(path)
                ? Results.Text(File.ReadAllText(path).Trim(), "text/plain")
                : Results.NotFound();
        });

        group.MapGet("/package", (IOptions<HubOptions> opts) =>
        {
            var path = Path.Combine(opts.Value.AgentDistRoot, "package.zip");
            return File.Exists(path)
                ? Results.File(path, "application/zip", "package.zip")
                : Results.NotFound();
        });

        group.MapGet("/package.sha256", (IOptions<HubOptions> opts) =>
        {
            var path = Path.Combine(opts.Value.AgentDistRoot, "package.sha256");
            return File.Exists(path)
                ? Results.Text(File.ReadAllText(path).Trim(), "text/plain")
                : Results.NotFound();
        });
    }
}
```

- [ ] **Step 2: Зарегистрировать в Program.cs**

`src/SzDiag.Hub/Program.cs` — после строки `app.MapManagementApi();` добавить:

```csharp
app.MapAgentPackageApi();
```

- [ ] **Step 3: Написать тесты**

Create `tests/SzDiag.Hub.Tests/AgentPackageApiTests.cs`. Использует ту же фабрику, что и другие Hub-тесты (WebApplicationFactory на `public partial class Program`). Смотри существующий `tests/SzDiag.Hub.Tests/*` на предмет базового класса/хелпера конфигурации `AgentToken` и `AgentDistRoot` — переиспользуй его паттерн. Тело тестов:

```csharp
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AgentPackageApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AgentPackageApiTests(WebApplicationFactory<Program> factory)
    {
        var dist = Path.Combine(Path.GetTempPath(), $"szdist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, "version.txt"), "abc123");
        File.WriteAllText(Path.Combine(dist, "package.zip"), "ZIPBYTES");
        File.WriteAllText(Path.Combine(dist, "package.sha256"), "deadbeef");

        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:AgentDistRoot", dist));
    }

    private HttpClient WithToken()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, "test-token");
        return c;
    }

    [Fact]
    public async Task Version_WithToken_ReturnsVersionString()
    {
        var r = await WithToken().GetAsync(HubRoutes.AgentVersionRoute);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("abc123", (await r.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Version_NoToken_Unauthorized()
    {
        var r = await _factory.CreateClient().GetAsync(HubRoutes.AgentVersionRoute);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Package_WithToken_ReturnsZip()
    {
        var r = await WithToken().GetAsync(HubRoutes.AgentPackageRoute);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
    }
}
```

Примечание: если существующие Hub-тесты конфигурируют `AgentToken` иначе (не через `UseSetting`), повтори их способ. Проверь один существующий тест-файл в `tests/SzDiag.Hub.Tests/` перед написанием.

- [ ] **Step 4: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AgentPackageApi`
Expected: 3 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Hub/AgentPackageApi.cs src/SzDiag.Hub/Program.cs tests/SzDiag.Hub.Tests/AgentPackageApiTests.cs
git commit -m "feat(hub): эндпоинты /agent/version|package|package.sha256"
```

---

## Task 4: Создать проект SzDiag.Updater и тест-проект

**Files:**
- Create: `src/SzDiag.Updater/SzDiag.Updater.csproj`
- Create: `src/SzDiag.Updater/UpdaterOptions.cs`
- Create: `src/SzDiag.Updater/appsettings.json`
- Create: `src/SzDiag.Updater/Program.cs` (заглушка)
- Create: `tests/SzDiag.Updater.Tests/SzDiag.Updater.Tests.csproj`

- [ ] **Step 1: csproj апдейтера**

Create `src/SzDiag.Updater/SzDiag.Updater.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <ProjectReference Include="..\SzDiag.Contracts\SzDiag.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration.Binder" Version="8.0.2" />
    <PackageReference Include="Microsoft.Extensions.Configuration.EnvironmentVariables" Version="8.0.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.1" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RuntimeIdentifiers>win-x64</RuntimeIdentifiers>
    <AssemblyName>SzDiag.Updater</AssemblyName>
  </PropertyGroup>

</Project>
```

- [ ] **Step 2: UpdaterOptions**

Create `src/SzDiag.Updater/UpdaterOptions.cs`:

```csharp
namespace SzDiag.Updater;

/// <summary>Конфиг апдейтера. Читается из appsettings.json рядом с exe — те же поля,
/// что у агента (общий файл на клиенте).</summary>
public sealed class UpdaterOptions
{
    /// <summary>Адрес hub. Пусто — автообнаружение по UDP (HubDiscovery).</summary>
    public string HubUrl { get; set; } = "";
    public string AgentToken { get; set; } = "";
}
```

- [ ] **Step 3: appsettings-шаблон**

Create `src/SzDiag.Updater/appsettings.json`:

```json
{
  "HubUrl": "",
  "AgentToken": "dev-token"
}
```

- [ ] **Step 4: Program-заглушка (соберётся)**

Create `src/SzDiag.Updater/Program.cs`:

```csharp
using SzDiag.Updater;

Console.WriteLine("SzDiag.Updater");
return 0;
```

- [ ] **Step 5: Тест-проект**

Create `tests/SzDiag.Updater.Tests/SzDiag.Updater.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\SzDiag.Updater\SzDiag.Updater.csproj" />
  </ItemGroup>

</Project>
```

Примечание: сверь версии пакетов xunit/Test.Sdk с любым существующим `tests/*/*.Tests.csproj` и используй их, чтобы не расходились.

- [ ] **Step 6: Добавить проекты в солюшен**

Run:
```bash
dotnet sln add src/SzDiag.Updater/SzDiag.Updater.csproj
dotnet sln add tests/SzDiag.Updater.Tests/SzDiag.Updater.Tests.csproj
```

- [ ] **Step 7: Собрать**

Run: `dotnet build`
Expected: SUCCESS (все проекты, включая новые).

- [ ] **Step 8: Commit**

```bash
git add src/SzDiag.Updater tests/SzDiag.Updater.Tests SzDiag.sln
git commit -m "feat(updater): скелет проекта SzDiag.Updater + тест-проект"
```

---

## Task 5: Hashing + UpdateClient (HTTP)

**Files:**
- Create: `src/SzDiag.Updater/Hashing.cs`
- Create: `src/SzDiag.Updater/IUpdateClient.cs`
- Create: `src/SzDiag.Updater/HttpUpdateClient.cs`
- Create: `tests/SzDiag.Updater.Tests/HttpUpdateClientTests.cs`

- [ ] **Step 1: Hashing helper**

Create `src/SzDiag.Updater/Hashing.cs`:

```csharp
using System.Security.Cryptography;

namespace SzDiag.Updater;

public static class Hashing
{
    /// <summary>sha256 файла в нижнем регистре hex.</summary>
    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Интерфейс клиента**

Create `src/SzDiag.Updater/IUpdateClient.cs`:

```csharp
namespace SzDiag.Updater;

/// <summary>Доступ к раздаче пакета на hub. Отдельный интерфейс — чтобы Program
/// тестировать с фейком, а сеть жила только в HttpUpdateClient.</summary>
public interface IUpdateClient
{
    /// <summary>Версия пакета на хосте (GET /agent/version).</summary>
    Task<string> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Ожидаемый sha256 пакета (GET /agent/package.sha256).</summary>
    Task<string> GetPackageSha256Async(CancellationToken ct = default);

    /// <summary>Скачать package.zip в destZipPath (GET /agent/package).</summary>
    Task DownloadPackageAsync(string destZipPath, CancellationToken ct = default);
}
```

- [ ] **Step 3: HTTP-реализация**

Create `src/SzDiag.Updater/HttpUpdateClient.cs`:

```csharp
using SzDiag.Contracts;

namespace SzDiag.Updater;

/// <summary>HTTP-клиент раздачи пакета. Токен шлётся в HubRoutes.TokenHeader.</summary>
public sealed class HttpUpdateClient : IUpdateClient
{
    private readonly HttpClient _http;

    public HttpUpdateClient(string hubBaseUrl, string token, HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(hubBaseUrl);
        _http.DefaultRequestHeaders.Remove(HubRoutes.TokenHeader);
        _http.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, token);
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync(HubRoutes.AgentVersionRoute, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadAsStringAsync(ct)).Trim();
    }

    public async Task<string> GetPackageSha256Async(CancellationToken ct = default)
    {
        var r = await _http.GetAsync(HubRoutes.AgentPackageSha256Route, ct);
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadAsStringAsync(ct)).Trim().ToLowerInvariant();
    }

    public async Task DownloadPackageAsync(string destZipPath, CancellationToken ct = default)
    {
        var r = await _http.GetAsync(HubRoutes.AgentPackageRoute, HttpCompletionOption.ResponseHeadersRead, ct);
        r.EnsureSuccessStatusCode();
        await using var fs = File.Create(destZipPath);
        await r.Content.CopyToAsync(fs, ct);
    }
}
```

- [ ] **Step 4: Написать тест на клиента (fake handler)**

Create `tests/SzDiag.Updater.Tests/HttpUpdateClientTests.cs`:

```csharp
using System.Net;
using System.Text;
using SzDiag.Contracts;
using SzDiag.Updater;
using Xunit;

namespace SzDiag.Updater.Tests;

public class HttpUpdateClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_fn(req));
    }

    [Fact]
    public async Task GetVersion_SendsTokenHeader_ReturnsTrimmedBody()
    {
        string? sentToken = null;
        var http = new HttpClient(new StubHandler(req =>
        {
            sentToken = req.Headers.GetValues(HubRoutes.TokenHeader).FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1\n") };
        }));
        var client = new HttpUpdateClient("http://hub", "tok", http);

        var v = await client.GetVersionAsync();

        Assert.Equal("v1", v);
        Assert.Equal("tok", sentToken);
    }

    [Fact]
    public async Task DownloadPackage_WritesBodyToFile()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ZIPDATA")) }));
        var client = new HttpUpdateClient("http://hub", "tok", http);
        var dest = Path.Combine(Path.GetTempPath(), $"pkg-{Guid.NewGuid():N}.zip");

        try
        {
            await client.DownloadPackageAsync(dest);
            Assert.Equal("ZIPDATA", File.ReadAllText(dest));
        }
        finally { File.Delete(dest); }
    }
}
```

- [ ] **Step 5: Прогнать тесты**

Run: `dotnet test tests/SzDiag.Updater.Tests --filter FullyQualifiedName~HttpUpdateClient`
Expected: 2 PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Updater/Hashing.cs src/SzDiag.Updater/IUpdateClient.cs src/SzDiag.Updater/HttpUpdateClient.cs tests/SzDiag.Updater.Tests/HttpUpdateClientTests.cs
git commit -m "feat(updater): HttpUpdateClient (version/sha256/download) + sha256 helper"
```

---

## Task 6: PackageApplier (атомарная распаковка, исключая appsettings/tools)

**Files:**
- Create: `src/SzDiag.Updater/PackageApplier.cs`
- Create: `tests/SzDiag.Updater.Tests/PackageApplierTests.cs`

- [ ] **Step 1: Написать тест (red)**

Create `tests/SzDiag.Updater.Tests/PackageApplierTests.cs`:

```csharp
using System.IO.Compression;
using SzDiag.Updater;
using Xunit;

namespace SzDiag.Updater.Tests;

public class PackageApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pa-{Guid.NewGuid():N}");
    private string Target => Path.Combine(_root, "target");
    private string ZipPath => Path.Combine(_root, "package.zip");

    public PackageApplierTests()
    {
        Directory.CreateDirectory(Target);
        // Локальные файлы клиента, которые нельзя перетирать:
        File.WriteAllText(Path.Combine(Target, "appsettings.json"), "LOCAL-CONFIG");
        Directory.CreateDirectory(Path.Combine(Target, "tools"));
        File.WriteAllText(Path.Combine(Target, "tools", "big.exe"), "LOCAL-TOOL");

        // Пакет: свежий agent.exe + version.txt + попытка перетереть appsettings/tools.
        using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
        AddEntry(zip, "SzDiag.Agent.exe", "NEW-AGENT");
        AddEntry(zip, "version.txt", "v2");
        AddEntry(zip, "appsettings.json", "SHOULD-NOT-OVERWRITE");
        AddEntry(zip, "tools/big.exe", "SHOULD-NOT-OVERWRITE");
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open());
        w.Write(content);
    }

    [Fact]
    public void Apply_WritesPackageFiles_ButKeepsLocalConfigAndTools()
    {
        PackageApplier.Apply(ZipPath, Target);

        Assert.Equal("NEW-AGENT", File.ReadAllText(Path.Combine(Target, "SzDiag.Agent.exe")));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(Target, "version.txt")));
        // Локальные не тронуты:
        Assert.Equal("LOCAL-CONFIG", File.ReadAllText(Path.Combine(Target, "appsettings.json")));
        Assert.Equal("LOCAL-TOOL", File.ReadAllText(Path.Combine(Target, "tools", "big.exe")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Прогнать — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Updater.Tests --filter FullyQualifiedName~PackageApplier`
Expected: FAIL (нет `PackageApplier`).

- [ ] **Step 3: Реализовать PackageApplier**

Create `src/SzDiag.Updater/PackageApplier.cs`:

```csharp
using System.IO.Compression;

namespace SzDiag.Updater;

/// <summary>Распаковка пакета агента поверх рабочей папки. Не перетирает локальные
/// файлы клиента: appsettings.json (конфиг) и всё в tools/ (стресс-проги).
/// Атомарность: сперва распаковка во временную папку, потом копирование поверх —
/// битый zip не оставит папку полу-обновлённой.</summary>
public static class PackageApplier
{
    private static readonly string[] SkipTopLevel = { "appsettings.json" };
    private static readonly string[] SkipDirs = { "tools" };

    public static void Apply(string zipPath, string targetDir)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"szupd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            foreach (var src in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, src);
                if (IsSkipped(rel)) continue;

                var dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* temp — не критично */ }
        }
    }

    private static bool IsSkipped(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/');
        if (SkipTopLevel.Contains(norm, StringComparer.OrdinalIgnoreCase)) return true;
        var firstSegment = norm.Split('/')[0];
        return SkipDirs.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Прогнать — зелёный**

Run: `dotnet test tests/SzDiag.Updater.Tests --filter FullyQualifiedName~PackageApplier`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Updater/PackageApplier.cs tests/SzDiag.Updater.Tests/PackageApplierTests.cs
git commit -m "feat(updater): PackageApplier — атомарная распаковка, кроме appsettings/tools"
```

---

## Task 7: AgentLauncher + Program (оркестрация)

**Files:**
- Create: `src/SzDiag.Updater/AgentLauncher.cs`
- Modify: `src/SzDiag.Updater/Program.cs`

- [ ] **Step 1: AgentLauncher**

Create `src/SzDiag.Updater/AgentLauncher.cs`:

```csharp
using System.Diagnostics;

namespace SzDiag.Updater;

/// <summary>Запуск agent.exe в той же консоли (без redirect stdio — агент интерактивно
/// спрашивает номер СЗ). Updater ждёт выхода агента и возвращает его код.</summary>
public static class AgentLauncher
{
    public static int LaunchAndWait(string agentExePath, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = agentExePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false, // наследуем консоль родителя
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode;
    }
}
```

- [ ] **Step 2: Program — оркестрация**

Replace `src/SzDiag.Updater/Program.cs` целиком:

```csharp
using Microsoft.Extensions.Configuration;
using SzDiag.Contracts;
using SzDiag.Updater;

var baseDir = AppContext.BaseDirectory;

var config = new ConfigurationBuilder()
    .SetBasePath(baseDir)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZUPDATER_")
    .Build();
var opts = new UpdaterOptions();
config.Bind(opts);

var agentExe = Path.Combine(baseDir, "SzDiag.Agent.exe");
var localVersionPath = Path.Combine(baseDir, "version.txt");

try
{
    // 1. Найти hub (требуем hub — без него агент всё равно бесполезен).
    var hubUrl = !string.IsNullOrWhiteSpace(opts.HubUrl)
        ? opts.HubUrl
        : await HubDiscovery.FindHubAsync(opts.AgentToken);
    Console.WriteLine($"Hub: {hubUrl}");

    var client = new HttpUpdateClient(hubUrl, opts.AgentToken);

    // 2. Версия на хосте. Старый hub без /agent/* → деградация на локальный агент.
    string hostVersion;
    try { hostVersion = await client.GetVersionAsync(); }
    catch (HttpRequestException)
    {
        Console.WriteLine("Hub не поддерживает апдейт (нет /agent/version).");
        return LaunchLocalOrFail(agentExe, baseDir, "hub без апдейт-эндпоинта");
    }

    var localVersion = File.Exists(localVersionPath) ? File.ReadAllText(localVersionPath).Trim() : null;

    // 3. Обновление, если версии разошлись.
    if (localVersion != hostVersion)
    {
        Console.WriteLine($"Обновление: {localVersion ?? "(нет)"} -> {hostVersion}");
        var tmpZip = Path.Combine(Path.GetTempPath(), $"szpkg-{Guid.NewGuid():N}.zip");
        try
        {
            await client.DownloadPackageAsync(tmpZip);
            var expected = await client.GetPackageSha256Async();
            var actual = Hashing.Sha256File(tmpZip);
            if (actual != expected)
            {
                Console.WriteLine($"sha256 не сошёлся (ожидали {expected}, получили {actual}).");
                return LaunchLocalOrFail(agentExe, baseDir, "битый пакет");
            }
            PackageApplier.Apply(tmpZip, baseDir);
            Console.WriteLine("Пакет применён.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Напр. agent.exe залочен (уже запущен) — не заменяем, идём на локальный агент.
            Console.WriteLine($"Не удалось применить обновление: {ex.Message}");
            return LaunchLocalOrFail(agentExe, baseDir, "ошибка применения пакета");
        }
        finally { try { File.Delete(tmpZip); } catch { } }
    }
    else
    {
        Console.WriteLine("Версия актуальна.");
    }

    // 4. Запустить агента.
    if (!File.Exists(agentExe))
    {
        Console.Error.WriteLine("Агент не найден после апдейта: " + agentExe);
        return 1;
    }
    return AgentLauncher.LaunchAndWait(agentExe, baseDir);
}
catch (HubNotFoundException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

// Деградация: если локальный агент есть — запустить его, иначе фейл.
static int LaunchLocalOrFail(string agentExe, string baseDir, string reason)
{
    if (File.Exists(agentExe))
    {
        Console.WriteLine($"Запускаю локального агента ({reason}).");
        return AgentLauncher.LaunchAndWait(agentExe, baseDir);
    }
    Console.Error.WriteLine($"Обновление невозможно ({reason}) и локального агента нет.");
    return 3;
}
```

- [ ] **Step 3: Собрать**

Run: `dotnet build src/SzDiag.Updater`
Expected: SUCCESS.

- [ ] **Step 4: Прогнать все тесты апдейтера**

Run: `dotnet test tests/SzDiag.Updater.Tests`
Expected: PASS (HttpUpdateClient + PackageApplier).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Updater/AgentLauncher.cs src/SzDiag.Updater/Program.cs
git commit -m "feat(updater): оркестрация — discovery, сверка версии, апдейт, запуск агента"
```

---

## Task 8: build-dist — version.txt, package.zip, agent-dist, publish Updater

**Files:**
- Modify: `tools/build-dist.ps1`

- [ ] **Step 1: Отдельная публикация Updater в dist/client (без сноса папки)**

Updater кладётся рядом с агентом в ту же `dist/client`. НЕ добавляй его в массив компонентов
(`@{ Project=...; Out="dist/client" }`) — функция `Publish` меняет папку целиком через staging,
и вторая публикация в ту же папку снесла бы агента. Вместо этого — прямой `dotnet publish -o`
после того, как агент уже в `dist/client`.

`tools/build-dist.ps1` — после блока, который публикует агента и копирует ssh/tools (после строки `Copy-Item "$sshCache\*.dll" dist\client\ssh\ ...`), добавить:

```powershell
# Апдейтер кладём рядом с агентом в dist\client (та же папка, БЕЗ сноса — агент уже там).
if (Test-Path dist\client\SzDiag.Agent.exe) {
    Write-Host "-- публикую SzDiag.Updater -> dist\client"
    dotnet publish src/SzDiag.Updater @common -o dist\client | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish SzDiag.Updater упал (код $LASTEXITCODE)." }
}
```

Примечание: `@common` уже определён выше (`-c Release -r win-x64 --self-contained -p:PublishSingleFile=true ...`).

- [ ] **Step 2: Генерация версии + пакета + agent-dist**

`tools/build-dist.ps1` — после публикации Updater (Step 2), перед секцией «3. Конфиги», добавить:

```powershell
# --- Версия и пакет для апдейтера ---
# Версия = git short sha (+ -dirty, если есть незакоммиченные правки); вне git — timestamp.
$version = ""
try {
    $sha = (git -C $root rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -eq 0 -and $sha) {
        $dirty = (git -C $root status --porcelain 2>$null)
        $version = if ($dirty) { "$sha-dirty" } else { "$sha" }
    }
} catch { }
if ([string]::IsNullOrWhiteSpace($version)) { $version = Get-Date -Format "yyyyMMdd-HHmmss" }

Set-Content -Path dist\client\version.txt -Value $version -Encoding ascii -NoNewline

# Пакет: agent.exe + ssh + service_key.pub + testsuite.json + version.txt.
# БЕЗ appsettings.json (локальный конфиг клиента), tools\ (тяжёлые тулы) и Updater.exe.
$pkgStage = Join-Path $env:TEMP "szdiag-pkg"
Remove-Item $pkgStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $pkgStage -Force | Out-Null
Copy-Item dist\client\SzDiag.Agent.exe    $pkgStage\ -Force
Copy-Item dist\client\service_key.pub     $pkgStage\ -Force -ErrorAction SilentlyContinue
Copy-Item dist\client\testsuite.json      $pkgStage\ -Force -ErrorAction SilentlyContinue
Copy-Item dist\client\version.txt         $pkgStage\ -Force
if (Test-Path dist\client\ssh) { Copy-Item dist\client\ssh $pkgStage\ssh -Recurse -Force }

$distRoot = "dist\host\hub\agent-dist"
New-Item -ItemType Directory $distRoot -Force | Out-Null
$zipPath = Join-Path $distRoot "package.zip"
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$pkgStage\*" -DestinationPath $zipPath -Force

$sha256 = (Get-FileHash $zipPath -Algorithm SHA256).Hash.ToLower()
Set-Content -Path (Join-Path $distRoot "package.sha256") -Value $sha256 -Encoding ascii -NoNewline
Set-Content -Path (Join-Path $distRoot "version.txt")    -Value $version -Encoding ascii -NoNewline
Remove-Item $pkgStage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "-- пакет апдейтера: $zipPath (version=$version)"
```

- [ ] **Step 3: Прописать AgentDistRoot в конфиг hub**

`tools/build-dist.ps1` — в here-string `$hubCfg` (объект `"Hub"`) добавить строку с абсолютным путём к agent-dist. Рядом с `"KnowledgeBaseRoot": "$kb",` добавить:

```powershell
    "AgentDistRoot": "$(("$root\dist\host\hub\agent-dist").Replace('\','\\'))",
```

(Путь абсолютный — hub резолвит файлы независимо от CWD, как и `AgentPackageApi` читает `Path.Combine(AgentDistRoot, ...)`.)

- [ ] **Step 4: Прогнать сборку dist**

Run: `pwsh -File tools/build-dist.ps1` (или из PowerShell: `.\tools\build-dist.ps1`)
Expected:
- `dist/client/SzDiag.Updater.exe` существует;
- `dist/client/version.txt` содержит версию;
- `dist/host/hub/agent-dist/{package.zip, package.sha256, version.txt}` существуют;
- в `package.zip` НЕТ `appsettings.json` и папки `tools`.

Проверка содержимого zip:
```bash
powershell -Command "Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead('dist/host/hub/agent-dist/package.zip').Entries.FullName"
```
Expected: `SzDiag.Agent.exe`, `version.txt`, `service_key.pub`, `testsuite.json`, `ssh/...` — без `appsettings.json`/`tools`.

- [ ] **Step 5: Commit**

```bash
git add tools/build-dist.ps1
git commit -m "build: пакет апдейтера (version/package/sha256) + публикация Updater"
```

---

## Task 9: E2E — ручной прогон боевого пути

**Files:** нет (ручной чеклист).

- [ ] **Step 1: Собрать и поднять hub**

Run:
```
.\tools\build-dist.ps1
dist\host\hub\SzDiag.Hub.exe   # в отдельном окне
```

- [ ] **Step 2: Первый заезд — только Updater + конфиг**

На чистой клиентской папке оставить только `SzDiag.Updater.exe` + `appsettings.json` (с `AgentToken`, опц. `HubUrl`). Запустить от админа:
```
SzDiag.Updater.exe
```
Expected: находит hub → «Обновление: (нет) -> <version>» → качает пакет → «Пакет применён» → запускается агент (спрашивает номер СЗ). Появились `SzDiag.Agent.exe`, `ssh\`, `service_key.pub`, `testsuite.json`, `version.txt`.

- [ ] **Step 3: Повторный запуск без правок**

Закрыть агента, снова запустить `SzDiag.Updater.exe`.
Expected: «Версия актуальна.» → сразу запуск агента, без скачивания.

- [ ] **Step 4: Проверить апдейт после правки агента**

Внести правку в агента (напр. изменить строку баннера), `.\tools\build-dist.ps1`, на клиенте снова `SzDiag.Updater.exe`.
Expected: версии разошлись → скачивание → «Пакет применён» → агент с новой правкой. `appsettings.json` клиента не перезаписан.

- [ ] **Step 5: Обновить документацию**

Отметить в `CLAUDE.md` (раздел про build-dist/клиент) и `docs/TESTING.md`, что точка входа на клиенте теперь `SzDiag.Updater.exe` (агент подтягивается сам). Обновить `docs/dev-knowledge-base.md`, если там описан запуск клиента.

```bash
git add CLAUDE.md docs/TESTING.md docs/dev-knowledge-base.md
git commit -m "docs: клиент запускается через SzDiag.Updater (самообновление агента)"
```

---

## Self-Review заметки

- **HttpRequestException в деградации:** `GetVersionAsync` кидает `HttpRequestException` и на 404 (через `EnsureSuccessStatusCode`), и на «hub недоступен». Discovery уже гарантирует, что hub найден (иначе `HubNotFoundException` раньше), поэтому `HttpRequestException` на этом шаге = именно «эндпоинт не отвечает/404» → деградация корректна.
- **appsettings апдейтера vs агента на клиенте:** оба читают один `dist/client/appsettings.json`. Пакет его не перетирает (исключён и в `PackageApplier`, и в упаковке build-dist) — локальный `HubUrl`/токен клиента сохраняются.
- **Updater не входит в пакет** — не перезаписывает сам себя во время работы (self-update вне MVP, спека §5).
- **Порядок публикации в build-dist:** Updater публикуется в `dist/client` ПОСЛЕ агента отдельной командой (функция `Publish` меняет папку целиком через staging, поэтому вторую публикацию в ту же папку делаем прямым `dotnet publish -o`, без сноса).
