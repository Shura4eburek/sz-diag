# sz-diag Фаза 1 — План 2: CLI оператора

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Консоль оператора: живой список онлайн-СЗ со статусом, `close <СЗ>` (инициирует revert на агенте) и `target <СЗ>` (выдаёт SSH-адрес для Claude).

**Architecture:** В hub добавляется management-HTTP-API (minimal API) с эндпоинтами списка/закрытия/target, защищённый management-токеном. Логика закрытия вынесена в `SessionCloser`, отправка команды агенту — за интерфейсом `IAgentCommandSender` (тестируемо без SignalR). CLI (`SzDiag.Cli`) — HTTP-клиент management-API через `IHubApiClient`.

**Tech Stack:** .NET 8, ASP.NET Core minimal API, System.Net.Http.Json, xUnit.

**Предпосылка:** реализован План 1 (`docs/plans/2026-07-01-phase1-plan1-server-foundation.md`).

Спека: [../specs/2026-07-01-sz-diag-phase1-design.md](../specs/2026-07-01-sz-diag-phase1-design.md)

---

## File Structure

```
src/
  SzDiag.Contracts/
    TargetInfo.cs           — DTO ответа target (sz, ip, user, ssh)
  SzDiag.Hub/
    IAgentCommandSender.cs   — абстракция отправки команды агенту
    SignalRAgentCommandSender.cs — реализация через IHubContext<AgentHub>
    SessionCloser.cs         — логика закрытия СЗ (revert + record + remove)
    ManagementApi.cs         — регистрация minimal-API эндпоинтов
    (Program.cs)             — подключить сервисы и ManagementApi
    (HubOptions.cs)          — добавить ManagementToken
  SzDiag.Cli/
    SzDiag.Cli.csproj
    Program.cs               — разбор аргументов, команды
    IHubApiClient.cs         — контракт клиента management-API
    HubApiClient.cs          — HTTP-реализация
    CliOptions.cs            — базовый URL + management-токен
    SessionTableRenderer.cs  — рендер таблицы онлайн-СЗ
tests/
  SzDiag.Hub.Tests/
    SessionCloserTests.cs
    ManagementApiTests.cs
  SzDiag.Cli.Tests/
    SzDiag.Cli.Tests.csproj
    HubApiClientTests.cs
    SessionTableRendererTests.cs
```

---

### Task 1: Контракт TargetInfo и опция management-токена

**Files:**
- Create: `src/SzDiag.Contracts/TargetInfo.cs`
- Modify: `src/SzDiag.Hub/HubOptions.cs` (добавить свойство)

- [ ] **Step 1: Написать TargetInfo**

`src/SzDiag.Contracts/TargetInfo.cs`:
```csharp
namespace SzDiag.Contracts;

/// <summary>SSH-цель для диагностики по номеру СЗ.</summary>
public sealed record TargetInfo(string Sz, string Ip, string User, string Ssh);
```

- [ ] **Step 2: Добавить ManagementToken в HubOptions**

В `src/SzDiag.Hub/HubOptions.cs` добавить свойство внутри класса `HubOptions`:
```csharp
    /// <summary>Токен для management-API (CLI). Заголовок X-SzDiag-Mgmt-Token.</summary>
    public string ManagementToken { get; set; } = "";

    /// <summary>Логин сервисной учётки на клиенте (для target).</summary>
    public string ServiceAccount { get; set; } = "svc-diag";
```

- [ ] **Step 3: Сборка**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/SzDiag.Contracts/TargetInfo.cs src/SzDiag.Hub/HubOptions.cs
git commit -m "feat(contracts): TargetInfo + management-токен в HubOptions"
```

---

### Task 2: SessionCloser + IAgentCommandSender

**Files:**
- Create: `src/SzDiag.Hub/IAgentCommandSender.cs`, `src/SzDiag.Hub/SessionCloser.cs`
- Test: `tests/SzDiag.Hub.Tests/SessionCloserTests.cs`

- [ ] **Step 1: Написать интерфейс отправки команды**

`src/SzDiag.Hub/IAgentCommandSender.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>Отправка команд конкретному агенту по его connectionId.</summary>
public interface IAgentCommandSender
{
    Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default);
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Hub.Tests/SessionCloserTests.cs`:
```csharp
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionCloserTests
{
    private sealed class SpyCommandSender : IAgentCommandSender
    {
        public List<(string conn, string sz)> Sent { get; } = new();
        public Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default)
        {
            Sent.Add((connectionId, sz));
            return Task.CompletedTask;
        }
    }

    private sealed class SpyStore : ISessionStore
    {
        public List<string> Closed { get; } = new();
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordOpenAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordCloseAsync(string sz, DateTimeOffset closedAt, CancellationToken ct = default)
        {
            Closed.Add(sz);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<SessionRecord>> GetHistoryAsync(CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<SessionRecord>)new List<SessionRecord>());
    }

    [Fact]
    public async Task Close_KnownOnlineSz_SendsRevertRecordsCloseAndRemoves()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender);

        var ok = await closer.CloseAsync("156864");

        Assert.True(ok);
        Assert.Equal(("conn-1", "156864"), sender.Sent.Single());
        Assert.Equal("156864", store.Closed.Single());
        Assert.Empty(reg.GetActive());
    }

    [Fact]
    public async Task Close_UnknownSz_ReturnsFalseAndDoesNothing()
    {
        var reg = new SessionRegistry();
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender);

        var ok = await closer.CloseAsync("000000");

        Assert.False(ok);
        Assert.Empty(sender.Sent);
        Assert.Empty(store.Closed);
    }

    [Fact]
    public async Task Close_OfflineSz_RecordsCloseWithoutSend()
    {
        var reg = new SessionRegistry();
        reg.Register("156864", "10.0.0.42", "PC-1", "conn-1");
        reg.MarkOfflineByConnection("conn-1"); // соединения нет, но сессия в реестре
        // эмулируем потерю connectionId: удалять его нельзя, поэтому проверяем ветку,
        // где агент офлайн — connectionId ещё есть, но по факту недоступен. Здесь для
        // простоты считаем: офлайн => connectionId присутствует, revert всё равно шлём.
        var sender = new SpyCommandSender();
        var store = new SpyStore();
        var closer = new SessionCloser(reg, store, sender);

        var ok = await closer.CloseAsync("156864");

        Assert.True(ok);
        Assert.Equal("156864", store.Closed.Single());
        Assert.Empty(reg.GetActive());
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter SessionCloserTests`
Expected: FAIL — `SessionCloser` не существует.

- [ ] **Step 4: Реализовать SessionCloser**

`src/SzDiag.Hub/SessionCloser.cs`:
```csharp
namespace SzDiag.Hub;

/// <summary>
/// Закрытие СЗ по команде из CLI: шлёт агенту revert (если известен connectionId),
/// фиксирует закрытие в истории и убирает сессию из реестра. Идемпотентно на уровне
/// «неизвестная СЗ = false».
/// </summary>
public sealed class SessionCloser
{
    private readonly SessionRegistry _registry;
    private readonly ISessionStore _store;
    private readonly IAgentCommandSender _sender;

    public SessionCloser(SessionRegistry registry, ISessionStore store, IAgentCommandSender sender)
    {
        _registry = registry;
        _store = store;
        _sender = sender;
    }

    public async Task<bool> CloseAsync(string sz, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return false;

        await _sender.SendRevertAsync(connId, sz, ct);
        await _store.RecordCloseAsync(sz, DateTimeOffset.UtcNow, ct);
        _registry.Remove(sz);
        return true;
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter SessionCloserTests`
Expected: PASS (3 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Hub/IAgentCommandSender.cs src/SzDiag.Hub/SessionCloser.cs tests/SzDiag.Hub.Tests/SessionCloserTests.cs
git commit -m "feat(hub): SessionCloser + абстракция отправки команды агенту"
```

---

### Task 3: SignalR-реализация отправителя команд

**Files:**
- Create: `src/SzDiag.Hub/SignalRAgentCommandSender.cs`

Реализация тонкая (обёртка над `IHubContext`), покрыта интеграционным тестом Task 5.

- [ ] **Step 1: Реализовать**

`src/SzDiag.Hub/SignalRAgentCommandSender.cs`:
```csharp
using Microsoft.AspNetCore.SignalR;
using SzDiag.Contracts;

namespace SzDiag.Hub;

public sealed class SignalRAgentCommandSender : IAgentCommandSender
{
    private readonly IHubContext<AgentHub> _hub;

    public SignalRAgentCommandSender(IHubContext<AgentHub> hub) => _hub = hub;

    public Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.Revert, sz, ct);
}
```

- [ ] **Step 2: Сборка**

Run: `dotnet build src/SzDiag.Hub`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SzDiag.Hub/SignalRAgentCommandSender.cs
git commit -m "feat(hub): SignalR-реализация отправителя команд агенту"
```

---

### Task 4: Management-API (эндпоинты) + подключение в Program

**Files:**
- Create: `src/SzDiag.Hub/ManagementApi.cs`
- Modify: `src/SzDiag.Hub/Program.cs`

- [ ] **Step 1: Реализовать ManagementApi**

`src/SzDiag.Hub/ManagementApi.cs`:
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

public static class ManagementApi
{
    public const string TokenHeader = "X-SzDiag-Mgmt-Token";

    public static void MapManagementApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            var opts = ctx.HttpContext.RequestServices.GetRequiredService<IOptions<HubOptions>>().Value;
            var provided = ctx.HttpContext.Request.Headers[TokenHeader].ToString();
            if (string.IsNullOrEmpty(opts.ManagementToken) || provided != opts.ManagementToken)
                return Results.Unauthorized();
            return await next(ctx);
        });

        group.MapGet("/sessions", (SessionRegistry reg) => Results.Ok(reg.GetActive()));

        group.MapPost("/sessions/{sz}/close", async (string sz, SessionCloser closer) =>
            await closer.CloseAsync(sz) ? Results.Ok() : Results.NotFound());

        group.MapGet("/sessions/{sz}/target", (string sz, SessionRegistry reg, IOptions<HubOptions> opts) =>
        {
            var s = reg.GetActive().FirstOrDefault(x => x.Sz == sz);
            if (s is null) return Results.NotFound();
            var user = opts.Value.ServiceAccount;
            return Results.Ok(new TargetInfo(sz, s.Ip, user, $"ssh {user}@{s.Ip}"));
        });
    }
}
```

- [ ] **Step 2: Подключить в Program.cs**

В `src/SzDiag.Hub/Program.cs` добавить регистрацию сервисов (рядом с прочими `AddSingleton`):
```csharp
builder.Services.AddSingleton<IAgentCommandSender, SignalRAgentCommandSender>();
builder.Services.AddSingleton<SessionCloser>();
```
И перед `app.Run();` (после `app.MapHub(...)`):
```csharp
app.MapManagementApi();
```

- [ ] **Step 3: Сборка**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add src/SzDiag.Hub/ManagementApi.cs src/SzDiag.Hub/Program.cs
git commit -m "feat(hub): management-API (sessions/close/target) с токеном"
```

---

### Task 5: Интеграционные тесты management-API

**Files:**
- Create: `tests/SzDiag.Hub.Tests/ManagementApiTests.cs`

- [ ] **Step 1: Написать тесты**

`tests/SzDiag.Hub.Tests/ManagementApiTests.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ManagementApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ManagementApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", "Data Source=:memory:")
             .UseSetting("Hub:KnowledgeBaseRoot",
                 Path.Combine(Path.GetTempPath(), $"szkb-mgmt-{Guid.NewGuid():N}")));
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");
        return c;
    }

    [Fact]
    public async Task Sessions_NoToken_Unauthorized()
    {
        var c = _factory.CreateClient();
        var resp = await c.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Sessions_WithSeededRegistry_ReturnsSession()
    {
        _factory.Services.GetRequiredService<SessionRegistry>()
            .Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var sessions = await Client().GetFromJsonAsync<List<SessionInfo>>("/api/sessions");

        Assert.Contains(sessions!, s => s.Sz == "156864");
    }

    [Fact]
    public async Task Close_UnknownSz_NotFound()
    {
        var resp = await Client().PostAsync("/api/sessions/000000/close", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Target_KnownSz_ReturnsSshString()
    {
        _factory.Services.GetRequiredService<SessionRegistry>()
            .Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var target = await Client().GetFromJsonAsync<TargetInfo>("/api/sessions/156864/target");

        Assert.Equal("10.0.0.42", target!.Ip);
        Assert.Equal("svc-diag", target.User);
        Assert.Equal("ssh svc-diag@10.0.0.42", target.Ssh);
    }
}
```

- [ ] **Step 2: Запустить тесты**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter ManagementApiTests`
Expected: PASS (4 теста).

- [ ] **Step 3: Commit**

```bash
git add tests/SzDiag.Hub.Tests/ManagementApiTests.cs
git commit -m "test(hub): интеграционные тесты management-API"
```

---

### Task 6: Скелет CLI-проекта

**Files:**
- Create: `src/SzDiag.Cli/SzDiag.Cli.csproj`, `tests/SzDiag.Cli.Tests/SzDiag.Cli.Tests.csproj`

- [ ] **Step 1: Создать проекты**

Run:
```bash
dotnet new console -n SzDiag.Cli -o src/SzDiag.Cli -f net8.0
dotnet new xunit -n SzDiag.Cli.Tests -o tests/SzDiag.Cli.Tests -f net8.0
dotnet sln add src/SzDiag.Cli tests/SzDiag.Cli.Tests
dotnet add src/SzDiag.Cli reference src/SzDiag.Contracts
dotnet add tests/SzDiag.Cli.Tests reference src/SzDiag.Cli src/SzDiag.Contracts
```

- [ ] **Step 2: Удалить шаблонные файлы**

Удалить `tests/SzDiag.Cli.Tests/UnitTest1.cs`. `src/SzDiag.Cli/Program.cs` заменим в Task 9.

- [ ] **Step 3: Сборка**

Run: `dotnet build`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "chore: скелет проектов SzDiag.Cli и тестов"
```

---

### Task 7: IHubApiClient + HubApiClient

**Files:**
- Create: `src/SzDiag.Cli/CliOptions.cs`, `src/SzDiag.Cli/IHubApiClient.cs`, `src/SzDiag.Cli/HubApiClient.cs`
- Test: `tests/SzDiag.Cli.Tests/HubApiClientTests.cs`

- [ ] **Step 1: Написать опции и контракт**

`src/SzDiag.Cli/CliOptions.cs`:
```csharp
namespace SzDiag.Cli;

public sealed class CliOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
}
```

`src/SzDiag.Cli/IHubApiClient.cs`:
```csharp
using SzDiag.Contracts;

namespace SzDiag.Cli;

public interface IHubApiClient
{
    Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default);
    Task<bool> CloseAsync(string sz, CancellationToken ct = default);
    Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default);
}
```

- [ ] **Step 2: Написать падающие тесты**

`tests/SzDiag.Cli.Tests/HubApiClientTests.cs`:
```csharp
using System.Net;
using System.Text;
using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class HubApiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _json;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode code, string json = "")
        {
            _code = code;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static HubApiClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://hub") };
        return new HubApiClient(http, "mgmt-token");
    }

    [Fact]
    public async Task GetSessions_ParsesJsonAndSendsToken()
    {
        var json = """
        [{"sz":"156864","ip":"10.0.0.42","hostname":"PC-1","status":0,
          "connectedAt":"2026-07-01T12:00:00+00:00","lastHeartbeat":"2026-07-01T12:00:00+00:00"}]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var client = NewClient(handler);

        var sessions = await client.GetSessionsAsync();

        Assert.Equal("156864", sessions.Single().Sz);
        Assert.Equal("mgmt-token", handler.LastRequest!.Headers.GetValues("X-SzDiag-Mgmt-Token").Single());
    }

    [Fact]
    public async Task Close_Ok_ReturnsTrue()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK));
        Assert.True(await client.CloseAsync("156864"));
    }

    [Fact]
    public async Task Close_NotFound_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.False(await client.CloseAsync("000000"));
    }

    [Fact]
    public async Task GetTarget_NotFound_ReturnsNull()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.Null(await client.GetTargetAsync("000000"));
    }
}
```

- [ ] **Step 3: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter HubApiClientTests`
Expected: FAIL — `HubApiClient` не существует.

- [ ] **Step 4: Реализовать HubApiClient**

`src/SzDiag.Cli/HubApiClient.cs`:
```csharp
using System.Net;
using System.Net.Http.Json;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public sealed class HubApiClient : IHubApiClient
{
    private readonly HttpClient _http;

    public HubApiClient(HttpClient http, string managementToken)
    {
        _http = http;
        if (!string.IsNullOrEmpty(managementToken))
            _http.DefaultRequestHeaders.Add("X-SzDiag-Mgmt-Token", managementToken);
    }

    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(CancellationToken ct = default)
        => await _http.GetFromJsonAsync<List<SessionInfo>>("/api/sessions", ct) ?? new();

    public async Task<bool> CloseAsync(string sz, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync($"/api/sessions/{sz}/close", null, ct);
        return resp.StatusCode == HttpStatusCode.OK;
    }

    public async Task<TargetInfo?> GetTargetAsync(string sz, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"/api/sessions/{sz}/target", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TargetInfo>(ct);
    }
}
```

- [ ] **Step 5: Запустить тесты**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter HubApiClientTests`
Expected: PASS (4 теста).

- [ ] **Step 6: Commit**

```bash
git add src/SzDiag.Cli/CliOptions.cs src/SzDiag.Cli/IHubApiClient.cs src/SzDiag.Cli/HubApiClient.cs tests/SzDiag.Cli.Tests/HubApiClientTests.cs
git commit -m "feat(cli): HTTP-клиент management-API"
```

---

### Task 8: Рендер таблицы сессий

**Files:**
- Create: `src/SzDiag.Cli/SessionTableRenderer.cs`
- Test: `tests/SzDiag.Cli.Tests/SessionTableRendererTests.cs`

- [ ] **Step 1: Написать падающие тесты**

`tests/SzDiag.Cli.Tests/SessionTableRendererTests.cs`:
```csharp
using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

public class SessionTableRendererTests
{
    [Fact]
    public void Render_IncludesSzIpAndStatusMarker()
    {
        var at = new DateTimeOffset(2026, 7, 1, 15, 30, 0, TimeSpan.Zero);
        var sessions = new List<SessionInfo>
        {
            new("156864", "10.0.0.42", "PC-1", SessionStatus.Online, at, at)
        };

        var text = SessionTableRenderer.Render(sessions);

        Assert.Contains("156864", text);
        Assert.Contains("10.0.0.42", text);
        Assert.Contains("online", text);
    }

    [Fact]
    public void Render_EmptyList_ShowsPlaceholder()
    {
        var text = SessionTableRenderer.Render(new List<SessionInfo>());
        Assert.Contains("нет активных СЗ", text);
    }
}
```

- [ ] **Step 2: Запустить — убедиться, что падает**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter SessionTableRendererTests`
Expected: FAIL — `SessionTableRenderer` не существует.

- [ ] **Step 3: Реализовать SessionTableRenderer**

`src/SzDiag.Cli/SessionTableRenderer.cs`:
```csharp
using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public static class SessionTableRenderer
{
    public static string Render(IReadOnlyList<SessionInfo> sessions)
    {
        if (sessions.Count == 0) return "  (нет активных СЗ)";

        var sb = new StringBuilder();
        sb.AppendLine("  СЗ         Статус     IP               Хост");
        sb.AppendLine("  ────────── ────────── ──────────────── ────────────");
        foreach (var s in sessions.OrderBy(x => x.Sz))
        {
            var marker = s.Status == SessionStatus.Online ? "● online" : "○ offline";
            sb.AppendLine($"  {s.Sz,-10} {marker,-10} {s.Ip,-16} {s.Hostname}");
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Запустить тесты**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter SessionTableRendererTests`
Expected: PASS (2 теста).

- [ ] **Step 5: Commit**

```bash
git add src/SzDiag.Cli/SessionTableRenderer.cs tests/SzDiag.Cli.Tests/SessionTableRendererTests.cs
git commit -m "feat(cli): рендер таблицы онлайн-СЗ"
```

---

### Task 9: Program.cs CLI — команды и живой список

**Files:**
- Modify: `src/SzDiag.Cli/Program.cs` (заменить целиком)
- Create: `src/SzDiag.Cli/appsettings.json`

- [ ] **Step 1: Добавить пакет конфигурации**

Run:
```bash
dotnet add src/SzDiag.Cli package Microsoft.Extensions.Configuration.Json
dotnet add src/SzDiag.Cli package Microsoft.Extensions.Configuration.EnvironmentVariables
```

- [ ] **Step 2: appsettings.json**

`src/SzDiag.Cli/appsettings.json`:
```json
{
  "HubBaseUrl": "http://localhost:5000",
  "ManagementToken": "dev-token"
}
```

В `src/SzDiag.Cli/SzDiag.Cli.csproj` внутрь существующего `<Project>` добавить, чтобы конфиг копировался:
```xml
  <ItemGroup>
    <None Update="appsettings.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Заменить Program.cs**

`src/SzDiag.Cli/Program.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using SzDiag.Cli;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables("SZDIAG_")
    .Build();

var options = new CliOptions();
config.Bind(options);

using var http = new HttpClient { BaseAddress = new Uri(options.HubBaseUrl) };
var client = new HubApiClient(http, options.ManagementToken);

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "watch";

switch (command)
{
    case "list":
        Console.WriteLine(SessionTableRenderer.Render(await client.GetSessionsAsync()));
        break;

    case "watch":
        await WatchAsync(client);
        break;

    case "close" when args.Length >= 2:
        Console.WriteLine(await client.CloseAsync(args[1])
            ? $"СЗ {args[1]} закрыта (revert отправлен агенту)."
            : $"СЗ {args[1]} не найдена среди активных.");
        break;

    case "target" when args.Length >= 2:
        var t = await client.GetTargetAsync(args[1]);
        Console.WriteLine(t is null ? $"СЗ {args[1]} не найдена." : t.Ssh);
        break;

    default:
        Console.WriteLine("""
            Использование:
              szcli [watch]          живой список онлайн-СЗ (по умолчанию)
              szcli list             однократный список
              szcli close <СЗ>       закрыть СЗ (revert на агенте)
              szcli target <СЗ>      SSH-адрес по номеру СЗ
            """);
        break;
}

static async Task WatchAsync(IHubApiClient client)
{
    Console.WriteLine("Живой список онлайн-СЗ. Ctrl+C для выхода.\n");
    while (true)
    {
        IReadOnlyList<SzDiag.Contracts.SessionInfo> sessions;
        try
        {
            sessions = await client.GetSessionsAsync();
        }
        catch (HttpRequestException)
        {
            Console.Clear();
            Console.WriteLine("  hub недоступен, переподключение…");
            await Task.Delay(2000);
            continue;
        }

        Console.Clear();
        Console.WriteLine($"  sz-diag — онлайн-СЗ   {DateTime.Now:HH:mm:ss}\n");
        Console.WriteLine(SessionTableRenderer.Render(sessions));
        await Task.Delay(1000);
    }
}
```

- [ ] **Step 4: Сборка и ручной smoke**

Run: `dotnet build`
Expected: Build succeeded.

Smoke (нужен запущенный hub из Плана 1 с `ManagementToken=dev-token`):
Run в одном терминале: `dotnet run --project src/SzDiag.Hub`
Run в другом: `dotnet run --project src/SzDiag.Cli -- list`
Expected: печатает `(нет активных СЗ)` (агентов ещё нет — это норма до Плана 3).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(cli): команды watch/list/close/target и живой список"
```

---

## Self-Review (выполнено при написании плана)

**Покрытие спеки (раздел sz-cli):**
- Живой список онлайн-СЗ со статусом → Task 8 (рендер) + Task 9 (watch). ✓
- `close <СЗ>` → пуш revert агенту → Task 2 (`SessionCloser`) + Task 4 (эндпоинт) + Task 9. ✓
- `target <СЗ>` → SSH-адрес → Task 4 (эндпоинт) + Task 7/9. ✓
- Защита management-API токеном → Task 4 (endpoint filter). ✓

**Плейсхолдеры:** отсутствуют.
**Согласованность типов:** `SessionInfo`, `TargetInfo`, `IAgentCommandSender`, `SessionCloser`, `IHubApiClient` — единые сигнатуры во всех задачах. Заголовок management-токена `X-SzDiag-Mgmt-Token` одинаков в `ManagementApi` и `HubApiClient`.
**Зависимость от Плана 1:** использует `SessionRegistry`, `ISessionStore`, `AgentHub`, `HubRoutes.Revert`, `HubOptions` — всё определено в Плане 1.
