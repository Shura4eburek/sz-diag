# Доступ к клиенту через Cloudflare Tunnel — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Клиентская машина доступна из любой сети без входящих портов: агент публикует свой
portable sshd через Cloudflare quick tunnel и сообщает выданное имя хабу.

**Architecture:** Два исходящих канала. Управляющий — SignalR агент → `hub.<домен>` через
именованный туннель с Access service token. Канал доступа — quick tunnel на sshd клиента, имя
приезжает хабу в `Register`/`ReportAccess` и живёт в `SessionInfo.AccessHost`. Адресация
перестаёт опираться на `RemoteIpAddress`; IP-сверка в `RevertStatusApi` заменяется секретом
сессии; host-ключ пинится на время заявки.

**Tech Stack:** .NET 8 (`net8.0`), ASP.NET Core SignalR, xunit 2.5.3, cloudflared (внешний exe),
Windows scheduled tasks под SYSTEM.

**Spec:** `docs/superpowers/specs/2026-09-14-cloudflare-tunnel-access-design.md`

## Global Constraints

- Целевой фреймворк — **net8.0**. Файлы с кириллицей сохранять в **UTF-8 с BOM**.
- Комментарии и пользовательский вывод — **на русском**; записи в журнал СЗ (`JournalWriter`) —
  **на украинском**, как весь kb.
- Реальный домен в git **не попадает**: в коде, тестах и фикстурах только `hub.example.com`,
  `<домен>` и подобные placeholder'ы. Репозиторий публичный.
- Живой Cloudflare в `dotnet test` не вызывается **никогда** — только фейк за `IAccessTunnel`.
- Пути к ключам/конфигам резолвятся от `AppContext.BaseDirectory`, не от CWD.
- Любой новый шаг в `Open` обязан иметь парную ветку в `Revert` под своим флагом в `RevertState`.
- Каждый шаг `Revert` — в собственном `try/catch`; падение одного не прекращает остальные.
- Агенты старых сборок не шлют новых полей: всё новое в DTO — nullable с дефолтом.
- Проверка после каждой задачи: `dotnet build` и `dotnet test` зелёные (~481 тест на входе).

---

### Task 1: Контракты адресации

**Files:**
- Create: `src/SzDiag.Contracts/AccessMode.cs`
- Create: `src/SzDiag.Contracts/AccessReportRequest.cs`
- Modify: `src/SzDiag.Contracts/SessionInfo.cs`
- Modify: `src/SzDiag.Contracts/RegisterRequest.cs`
- Modify: `src/SzDiag.Contracts/HubRoutes.cs`
- Test: `tests/SzDiag.Hub.Tests/AccessAddressingContractsTests.cs`

**Interfaces:**
- Consumes: ничего (первая задача).
- Produces: `AccessMode.Direct` / `AccessMode.Tunnel` (строковые константы);
  `AccessReportRequest(string Sz, string? AccessHost, string? AccessMode, string? SshHostKeyFingerprint)`;
  `SessionInfo` с новыми параметрами `string? LanIp = null, string? AccessHost = null, string? AccessMode = null`;
  `RegisterRequest` с `string? LanIp = null, string? AccessHost = null, string? AccessMode = null, string? SshHostKeyFingerprint = null`;
  `HubRoutes.ReportAccess`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/AccessAddressingContractsTests.cs`:

```csharp
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AccessAddressingContractsTests
{
    [Fact]
    public void Агент_старой_сборки_не_шлёт_новых_полей()
    {
        var request = new RegisterRequest("162003", "TEST-PC");

        Assert.Null(request.LanIp);
        Assert.Null(request.AccessHost);
        Assert.Null(request.AccessMode);
        Assert.Null(request.SshHostKeyFingerprint);
    }

    [Fact]
    public void SessionInfo_по_умолчанию_без_туннеля()
    {
        var now = DateTimeOffset.UtcNow;
        var info = new SessionInfo("162003", "192.168.1.5", "TEST-PC",
            SessionStatus.Online, now, now);

        Assert.Null(info.AccessHost);
        Assert.Null(info.AccessMode);
        Assert.Null(info.LanIp);
    }

    [Fact]
    public void Режимы_доступа_имеют_стабильные_имена()
    {
        // Значения ездят по SignalR и лежат в SQLite — менять нельзя.
        Assert.Equal("direct", AccessMode.Direct);
        Assert.Equal("tunnel", AccessMode.Tunnel);
    }

    [Fact]
    public void ReportAccess_переносит_имя_туннеля_и_отпечаток()
    {
        var report = new AccessReportRequest("162003", "abc-def.trycloudflare.com",
            AccessMode.Tunnel, "SHA256:abc123");

        Assert.Equal("162003", report.Sz);
        Assert.Equal("abc-def.trycloudflare.com", report.AccessHost);
        Assert.Equal(AccessMode.Tunnel, report.AccessMode);
        Assert.Equal("SHA256:abc123", report.SshHostKeyFingerprint);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AccessAddressingContractsTests`
Expected: FAIL — не собирается, `AccessMode` и `AccessReportRequest` не существуют.

- [ ] **Step 3: Написать минимальную реализацию**

Создать `src/SzDiag.Contracts/AccessMode.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>Как хост добирается до клиента. Строки, а не enum: значение ездит по SignalR,
/// лежит в SQLite и приходит от агентов старых сборок как null.</summary>
public static class AccessMode
{
    /// <summary>Прямой SSH по адресу в общей сети — быстрее и не зависит от Cloudflare.
    /// Агент выбирает этот режим, когда нашёл hub broadcast'ом по локалке.</summary>
    public const string Direct = "direct";

    /// <summary>SSH через quick tunnel: у машины нет входящего порта, доступного хосту.</summary>
    public const string Tunnel = "tunnel";
}
```

Создать `src/SzDiag.Contracts/AccessReportRequest.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>Агент → hub: чем сейчас доступна машина. Отдельный метод, а не поле heartbeat,
/// потому что SignalR не поддерживает перегрузки hub-методов, а ломать сигнатуру
/// <c>Heartbeat(string)</c> нельзя — агенты старых сборок зовут её как есть.
///
/// Шлётся при открытии доступа и заново после ребута: quick tunnel НЕ сохраняет hostname
/// между запусками, поэтому имя обязано обновляться в течение сессии.</summary>
/// <param name="AccessHost">Имя, по которому хост подключается: hostname quick tunnel'а либо
/// null, если туннеля нет (тогда адресом остаётся IP).</param>
/// <param name="SshHostKeyFingerprint">Отпечаток host-ключа нашего portable sshd. Якорь
/// доверия — этот канал (аутентифицирован, по TLS); по нему хост пинит ключ и перестаёт
/// ходить со StrictHostKeyChecking=no.</param>
public sealed record AccessReportRequest(string Sz, string? AccessHost = null,
    string? AccessMode = null, string? SshHostKeyFingerprint = null);
```

В `src/SzDiag.Contracts/HubRoutes.cs` после строки с `Heartbeat` добавить:

```csharp
    // Агент -> hub: чем сейчас доступна машина (имя туннеля, режим, отпечаток host-ключа).
    // Отдельный метод: SignalR не умеет перегрузки, а Heartbeat(string) ломать нельзя.
    public const string ReportAccess = nameof(ReportAccess);
```

В `src/SzDiag.Contracts/RegisterRequest.cs` расширить record (сохранив порядок существующих
параметров — они позиционные):

```csharp
public sealed record RegisterRequest(string Sz, string Hostname, DateTimeOffset? BootTime = null,
    string? LastShutdown = null, string? AgentUser = null, int? AgentSessionId = null,
    string? LanIp = null, string? AccessHost = null, string? AccessMode = null,
    string? SshHostKeyFingerprint = null);
```

И добавить в XML-док над ним:

```csharp
/// <param name="LanIp">Адрес машины в её собственной сети, как его видит сам агент. Носит
/// справочный смысл (какая у клиента сеть) и адресом не является: за туннелем и за NAT
/// подключаться по нему нельзя.</param>
/// <param name="AccessHost">Имя quick tunnel'а, если поднят. Именно из него строится строка
/// подключения — hub больше не угадывает адрес из RemoteIpAddress.</param>
/// <param name="AccessMode">См. <see cref="SzDiag.Contracts.AccessMode"/>.</param>
/// <param name="SshHostKeyFingerprint">Отпечаток host-ключа portable sshd для пиннинга.</param>
```

В `src/SzDiag.Contracts/SessionInfo.cs` добавить параметры в конец record'а:

```csharp
    /// <summary>Адрес машины в её собственной сети (со слов агента) — справочный.</summary>
    string? LanIp = null,
    /// <summary>Имя, по которому хост подключается к клиенту (quick tunnel). null — туннеля
    /// нет, адресом остаётся <see cref="Ip"/>.</summary>
    string? AccessHost = null,
    /// <summary>См. <see cref="SzDiag.Contracts.AccessMode"/>. null у агентов старых сборок —
    /// считать Direct.</summary>
    string? AccessMode = null,
    /// <summary>Отпечаток host-ключа sshd клиента для пиннинга (см. Task 9).</summary>
    string? SshHostKeyFingerprint = null)
```

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AccessAddressingContractsTests`
Expected: PASS (4 теста)

- [ ] **Step 5: Прогнать весь набор — ничего не сломано**

Run: `dotnet test`
Expected: PASS, регрессий нет (новые поля опциональны).

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Contracts tests/SzDiag.Hub.Tests/AccessAddressingContractsTests.cs
git commit -m "feat(contracts): адресация клиента через AccessHost/AccessMode вместо IP"
```

---

### Task 2: Реестр сессий принимает и обновляет адрес доступа

**Files:**
- Modify: `src/SzDiag.Hub/SessionRegistry.cs`
- Test: `tests/SzDiag.Hub.Tests/SessionRegistryAccessTests.cs`

**Interfaces:**
- Consumes: `AccessMode`, `SessionInfo` из Task 1.
- Produces: `SessionRegistry.Register(..., string? lanIp = null, string? accessHost = null,
  string? accessMode = null, string? sshHostKeyFingerprint = null)`;
  `bool SessionRegistry.SetAccess(string sz, string? accessHost, string? accessMode, string? fingerprint)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/SessionRegistryAccessTests.cs`:

```csharp
using Microsoft.Extensions.Time.Testing;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionRegistryAccessTests
{
    private static SessionRegistry NewRegistry(out FakeTimeProvider time)
    {
        time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-14T10:00:00Z"));
        return new SessionRegistry(time);
    }

    [Fact]
    public void Register_кладёт_имя_туннеля_в_сессию()
    {
        var reg = NewRegistry(out _);

        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            lanIp: "192.168.1.5", accessHost: "aaa-bbb.trycloudflare.com",
            accessMode: AccessMode.Tunnel);

        var info = reg.TryGetInfo("162003")!;
        Assert.Equal("aaa-bbb.trycloudflare.com", info.AccessHost);
        Assert.Equal(AccessMode.Tunnel, info.AccessMode);
        Assert.Equal("192.168.1.5", info.LanIp);
    }

    [Fact]
    public void SetAccess_обновляет_имя_после_ребута()
    {
        // Quick tunnel не сохраняет hostname между запусками: после ребута клиента имя ДРУГОЕ.
        // Если бы hub держал только имя из Register, target вёл бы на мёртвый туннель.
        var reg = NewRegistry(out _);
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            accessHost: "старое-имя.trycloudflare.com", accessMode: AccessMode.Tunnel);

        var ok = reg.SetAccess("162003", "новое-имя.trycloudflare.com", AccessMode.Tunnel, null);

        Assert.True(ok);
        Assert.Equal("новое-имя.trycloudflare.com", reg.TryGetInfo("162003")!.AccessHost);
    }

    [Fact]
    public void SetAccess_освежает_heartbeat()
    {
        var reg = NewRegistry(out var time);
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1");
        time.Advance(TimeSpan.FromMinutes(5));

        reg.SetAccess("162003", "имя.trycloudflare.com", AccessMode.Tunnel, null);

        Assert.Equal(time.GetUtcNow(), reg.TryGetInfo("162003")!.LastHeartbeat);
    }

    [Fact]
    public void SetAccess_по_неизвестной_СЗ_возвращает_false()
    {
        var reg = NewRegistry(out _);

        Assert.False(reg.SetAccess("999999", "имя.trycloudflare.com", AccessMode.Tunnel, null));
    }

    [Fact]
    public void Туннель_отвалился_имя_обнуляется_но_сессия_жива()
    {
        var reg = NewRegistry(out _);
        reg.Register("162003", "127.0.0.1", "TEST-PC", "conn-1",
            accessHost: "имя.trycloudflare.com", accessMode: AccessMode.Tunnel);

        reg.SetAccess("162003", null, AccessMode.Direct, null);

        var info = reg.TryGetInfo("162003")!;
        Assert.Null(info.AccessHost);
        Assert.Equal(SessionStatus.Online, info.Status);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~SessionRegistryAccessTests`
Expected: FAIL — у `Register` нет таких параметров, `SetAccess` не существует.

> Если `FakeTimeProvider` не подключён в `SzDiag.Hub.Tests`, посмотреть, чем измеряют время
> соседние тесты реестра (`tests/SzDiag.Hub.Tests/SessionRegistry*Tests.cs`), и взять тот же
> способ — не тянуть новый пакет ради одного файла.

- [ ] **Step 3: Написать минимальную реализацию**

В `src/SzDiag.Hub/SessionRegistry.cs` расширить сигнатуру `Register` (новые параметры **в конец**,
чтобы не ломать существующие вызовы):

```csharp
    public RegisterOutcome Register(string sz, string ip, string hostname, string connectionId,
        DateTimeOffset? bootTime = null, string? lastShutdown = null,
        string? agentUser = null, int? agentSessionId = null,
        string? lanIp = null, string? accessHost = null, string? accessMode = null,
        string? sshHostKeyFingerprint = null)
```

В обоих местах, где строится `new SessionInfo(...)` внутри `Register`, дописать новые аргументы:

```csharp
        var info = new SessionInfo(sz, ip, hostname, SessionStatus.Online, now, now,
            BootTime: bootTime, LastRebootAt: lastReboot, RebootCount: rebootCount,
            AgentUser: agentUser, AgentSessionId: agentSessionId,
            LanIp: lanIp, AccessHost: accessHost, AccessMode: accessMode,
            SshHostKeyFingerprint: sshHostKeyFingerprint);
```

Рядом с `Heartbeat` добавить:

```csharp
    /// <summary>Агент сообщил, чем машина доступна сейчас. Отдельно от Register, потому что
    /// имя quick tunnel'а меняется в течение сессии: туннель не сохраняет hostname между
    /// запусками, а агент переподнимает его после ребута и при обрыве.
    ///
    /// Пустое <paramref name="accessHost"/> — не ошибка: туннель отвалился, сессия при этом
    /// жива (управляющий канал отдельный). Освежает heartbeat, раз агент на связи.</summary>
    public bool SetAccess(string sz, string? accessHost, string? accessMode, string? fingerprint)
    {
        if (!_bySz.TryGetValue(sz, out var e)) return false;
        var now = _time.GetUtcNow();
        _bySz[sz] = e with
        {
            Info = e.Info with
            {
                AccessHost = string.IsNullOrWhiteSpace(accessHost) ? null : accessHost,
                AccessMode = string.IsNullOrWhiteSpace(accessMode) ? null : accessMode,
                // Отпечаток не затираем пустым: агент может отчитаться о смене имени, не
                // трогая host-ключ (ключи переживают перезапуск туннеля).
                SshHostKeyFingerprint = string.IsNullOrWhiteSpace(fingerprint)
                    ? e.Info.SshHostKeyFingerprint
                    : fingerprint,
                Status = SessionStatus.Online,
                LastHeartbeat = now,
            }
        };
        return true;
    }
```

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~SessionRegistryAccessTests`
Expected: PASS (5 тестов)

- [ ] **Step 5: Прогнать весь набор**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hub/SessionRegistry.cs tests/SzDiag.Hub.Tests/SessionRegistryAccessTests.cs
git commit -m "feat(hub): реестр хранит адрес доступа и обновляет его в течение сессии"
```

---

### Task 3: Hub-метод `ReportAccess`

**Files:**
- Modify: `src/SzDiag.Hub/AgentHub.cs`
- Test: `tests/SzDiag.Hub.Tests/AgentHubReportAccessTests.cs`

**Interfaces:**
- Consumes: `SessionRegistry.SetAccess` (Task 2), `AccessReportRequest` (Task 1).
- Produces: hub-метод `AgentHub.ReportAccess(AccessReportRequest request)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/AgentHubReportAccessTests.cs`. За образец взять, как соседние
тесты (`AgentHubRevertResultTests.cs`) конструируют `AgentHub` — набор зависимостей там уже
собран, повторить его, а не выдумывать свой:

```csharp
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AgentHubReportAccessTests
{
    [Fact]
    public async Task ReportAccess_кладёт_имя_туннеля_в_реестр()
    {
        var fixture = AgentHubTestFixture.Create();          // см. AgentHubRevertResultTests
        await fixture.Hub.Register(new RegisterRequest("162003", "TEST-PC"));

        await fixture.Hub.ReportAccess(new AccessReportRequest("162003",
            "aaa-bbb.trycloudflare.com", AccessMode.Tunnel, "SHA256:xyz"));

        var info = fixture.Registry.TryGetInfo("162003")!;
        Assert.Equal("aaa-bbb.trycloudflare.com", info.AccessHost);
        Assert.Equal(AccessMode.Tunnel, info.AccessMode);
        Assert.Equal("SHA256:xyz", info.SshHostKeyFingerprint);
    }

    [Fact]
    public async Task ReportAccess_по_мусорному_номеру_ничего_не_пишет()
    {
        // Токен агента общий на всех (модель угроз: клиент — наименее доверенное устройство),
        // поэтому Sz из тела нельзя принимать как есть — соседние пути это уже проверяют.
        var fixture = AgentHubTestFixture.Create();

        await fixture.Hub.ReportAccess(new AccessReportRequest(@"..\..\Users\Public\x",
            "имя.trycloudflare.com", AccessMode.Tunnel, null));

        Assert.Null(fixture.Registry.TryGetInfo(@"..\..\Users\Public\x"));
    }

    [Fact]
    public async Task ReportAccess_по_незарегистрированной_СЗ_не_падает()
    {
        var fixture = AgentHubTestFixture.Create();

        var ex = await Record.ExceptionAsync(() => fixture.Hub.ReportAccess(
            new AccessReportRequest("999999", "имя.trycloudflare.com", AccessMode.Tunnel, null)));

        Assert.Null(ex);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AgentHubReportAccessTests`
Expected: FAIL — метода `ReportAccess` нет.

- [ ] **Step 3: Написать минимальную реализацию**

В `src/SzDiag.Hub/AgentHub.cs` рядом с `Heartbeat` добавить:

```csharp
    /// <summary>Агент сообщает, чем машина доступна сейчас. Зовётся при открытии доступа и
    /// заново при каждой смене имени туннеля (ребут, переподнятие после обрыва).</summary>
    public Task ReportAccess(AccessReportRequest request)
    {
        // Токен /agents общий на всех агентов, поэтому Sz из тела проверяем, как и на
        // соседних путях: без этого произвольная строка уезжает дальше по коду как номер СЗ.
        if (!SzNumber.IsValid(request.Sz)) return Task.CompletedTask;

        _registry.SetAccess(request.Sz, request.AccessHost, request.AccessMode,
            request.SshHostKeyFingerprint);
        return Task.CompletedTask;
    }
```

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~AgentHubReportAccessTests`
Expected: PASS (3 теста)

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hub/AgentHub.cs tests/SzDiag.Hub.Tests/AgentHubReportAccessTests.cs
git commit -m "feat(hub): метод ReportAccess — агент сообщает имя туннеля и отпечаток ключа"
```

---

### Task 4: `szcli target` в двух режимах

**Files:**
- Modify: `src/SzDiag.Cli/TargetSsh.cs`
- Modify: `src/SzDiag.Contracts/TargetInfo.cs`
- Modify: `src/SzDiag.Hub/ManagementApi.cs:264-270`
- Test: `tests/SzDiag.Cli.Tests/TargetSshTunnelTests.cs`

**Interfaces:**
- Consumes: `SessionInfo.AccessHost` / `AccessMode` (Task 1-2).
- Produces: `TargetInfo(string Sz, string Ip, string User, string Ssh, string? AccessHost = null,
  string? AccessMode = null, string? Unavailable = null)`;
  `TargetSsh.Build(string user, string host, string? keyPath, bool viaTunnel, string? knownHostsPath = null)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Cli.Tests/TargetSshTunnelTests.cs`:

```csharp
using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class TargetSshTunnelTests
{
    [Fact]
    public void Прямой_режим_строит_обычную_строку()
    {
        var line = TargetSsh.Build("svc-diag", "192.168.1.5", @"C:\keys\svc_diag_key",
            viaTunnel: false);

        Assert.Contains("svc-diag@192.168.1.5", line);
        Assert.DoesNotContain("ProxyCommand", line);
    }

    [Fact]
    public void Туннельный_режим_добавляет_ProxyCommand()
    {
        var line = TargetSsh.Build("svc-diag", "aaa-bbb.trycloudflare.com",
            @"C:\keys\svc_diag_key", viaTunnel: true);

        Assert.Contains("ProxyCommand", line);
        Assert.Contains("cloudflared access ssh --hostname %h", line);
        Assert.Contains("svc-diag@aaa-bbb.trycloudflare.com", line);
    }

    [Fact]
    public void ProxyCommand_взят_в_кавычки_целиком()
    {
        // Без кавычек ssh разбирает только первое слово как команду, а остальное считает
        // своими аргументами — строку нельзя было бы просто скопировать и вставить.
        var line = TargetSsh.Build("svc-diag", "aaa-bbb.trycloudflare.com", null, viaTunnel: true);

        Assert.Contains("-o \"ProxyCommand cloudflared access ssh --hostname %h\"", line);
    }

    [Fact]
    public void Известный_отпечаток_включает_строгую_проверку_ключа()
    {
        var line = TargetSsh.Build("svc-diag", "aaa-bbb.trycloudflare.com", null,
            viaTunnel: true, knownHostsPath: @"C:\hub\known_hosts\162003");

        Assert.Contains("StrictHostKeyChecking=yes", line);
        Assert.Contains(@"UserKnownHostsFile=""C:\hub\known_hosts\162003""", line);
        Assert.DoesNotContain("StrictHostKeyChecking=no", line);
    }

    [Fact]
    public void Без_отпечатка_остаётся_прежнее_поведение()
    {
        // Пока отпечаток не приехал (агент старой сборки), ломать рабочий путь нельзя.
        var line = TargetSsh.Build("svc-diag", "192.168.1.5", null, viaTunnel: false);

        Assert.Contains("StrictHostKeyChecking=no", line);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~TargetSshTunnelTests`
Expected: FAIL — у `TargetSsh` нет метода `Build` с такой сигнатурой.

- [ ] **Step 3: Написать минимальную реализацию**

Заменить содержимое `src/SzDiag.Cli/TargetSsh.cs`:

```csharp
namespace SzDiag.Cli;

/// <summary>Сборка рабочей SSH-строки для `szcli target`. Голый `ssh user@host` не
/// подключается: нужен наш ключ и опции host-ключа (бэклог п.118).
///
/// Два режима. Прямой — как было, по адресу в общей сети. Туннельный — через
/// `cloudflared access ssh`: у машины нет входящего порта, доступного хосту.</summary>
public static class TargetSsh
{
    /// <summary>Команда-прокси для туннельного режима. `%h` подставляет сам ssh — имя
    /// туннеля не хардкодится в строку дважды.</summary>
    private const string ProxyCommand = "ProxyCommand cloudflared access ssh --hostname %h";

    /// <param name="host">Адрес в прямом режиме, имя quick tunnel'а — в туннельном.</param>
    /// <param name="knownHostsPath">Файл known_hosts этой СЗ. Задан — ходим со строгой
    /// проверкой host-ключа: имя туннеля публично и не аутентифицировано, без пиннинга
    /// подмену не отличить. Не задан (агент старой сборки) — прежнее поведение.</param>
    public static string Build(string user, string host, string? keyPath, bool viaTunnel,
        string? knownHostsPath = null)
    {
        var parts = new List<string> { "ssh" };

        if (!string.IsNullOrWhiteSpace(keyPath)) parts.Add($"-i \"{keyPath}\"");

        if (!string.IsNullOrWhiteSpace(knownHostsPath))
        {
            parts.Add("-o StrictHostKeyChecking=yes");
            parts.Add($"-o UserKnownHostsFile=\"{knownHostsPath}\"");
        }
        else
        {
            // Исторически: IP переиспользуются между заявками, и ssh ругался на смену ключа.
            parts.Add("-o StrictHostKeyChecking=no");
            parts.Add("-o UserKnownHostsFile=NUL");
        }

        if (viaTunnel) parts.Add($"-o \"{ProxyCommand}\"");

        parts.Add($"{user}@{host}");
        return string.Join(' ', parts);
    }
}
```

Расширить `src/SzDiag.Contracts/TargetInfo.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>Как подключиться к клиенту. <paramref name="Unavailable"/> заполнено, когда
/// подключиться нельзя: печатать в этом случае неработающую строку — врать пользователю
/// (правило честности отчётов).</summary>
public sealed record TargetInfo(string Sz, string Ip, string User, string Ssh,
    string? AccessHost = null, string? AccessMode = null, string? Unavailable = null);
```

Заменить эндпоинт в `src/SzDiag.Hub/ManagementApi.cs`:

```csharp
        group.MapGet("/sessions/{sz}/target", (string sz, SessionRegistry reg, IOptions<HubOptions> opts) =>
        {
            var s = reg.GetActive().FirstOrDefault(x => x.Sz == sz);
            if (s is null) return Results.NotFound();
            var user = opts.Value.ServiceAccount;

            // Туннельный режим без имени — машина сейчас недостижима по SSH. Сессия при этом
            // может быть жива: управляющий канал отдельный. Отдаём причину, а не строку,
            // которая гарантированно не сработает.
            if (s.AccessMode == AccessMode.Tunnel && string.IsNullOrWhiteSpace(s.AccessHost))
                return Results.Ok(new TargetInfo(sz, s.Ip, user, "",
                    AccessMode: s.AccessMode,
                    Unavailable: "туннель не поднят — SSH недоступен; exec-канал работает"));

            var viaTunnel = s.AccessMode == AccessMode.Tunnel;
            var host = viaTunnel ? s.AccessHost! : s.Ip;
            var line = TargetSsh.Build(user, host, null, viaTunnel);
            return Results.Ok(new TargetInfo(sz, s.Ip, user, line, s.AccessHost, s.AccessMode));
        });
```

> `ManagementApi` лежит в `SzDiag.Hub`, а `TargetSsh` — в `SzDiag.Cli`. Если ссылки нет,
> перенести `TargetSsh.cs` в `SzDiag.Contracts` (строка нужна обоим концам) и поправить
> `using` в `SzDiag.Cli`. Проверить `dotnet build` до того, как править тесты.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Cli.Tests --filter FullyQualifiedName~TargetSshTunnelTests`
Expected: PASS (5 тестов)

- [ ] **Step 5: Прогнать весь набор — тут выше всего риск регрессии**

Run: `dotnet test`
Expected: PASS. Если падают существующие тесты `target` — они ждут старый формат строки;
привести их к новому, не ослабляя проверок.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Cli/TargetSsh.cs src/SzDiag.Contracts/TargetInfo.cs src/SzDiag.Hub/ManagementApi.cs tests/SzDiag.Cli.Tests/TargetSshTunnelTests.cs
git commit -m "feat(cli): target строит ProxyCommand для туннеля и честно молчит без него"
```

---

### Task 5: `IAccessTunnel` и разбор вывода cloudflared

**Files:**
- Create: `src/SzDiag.Agent/IAccessTunnel.cs`
- Create: `src/SzDiag.Agent/CloudflaredOutputParser.cs`
- Create: `src/SzDiag.Agent/CloudflaredTunnel.cs`
- Test: `tests/SzDiag.Agent.Tests/CloudflaredOutputParserTests.cs`

**Interfaces:**
- Consumes: ничего.
- Produces: `IAccessTunnel` с `string? Start(int sshPort, string taskName, TimeSpan timeout)`,
  `void Stop(string taskName)`; `CloudflaredOutputParser.TryFindHostname(string log, out string? hostname)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Agent.Tests/CloudflaredOutputParserTests.cs`. Строки лога — **настоящие**,
снятые с бокса 2026-09-14:

```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class CloudflaredOutputParserTests
{
    // Реальный вывод cloudflared 2026.8.2, снят при проверке схемы на боксе.
    private const string РеальныйЛог = """
2026-09-14T13:07:36Z INF Requesting new quick Tunnel on trycloudflare.com...
2026-09-14T13:07:41Z INF +--------------------------------------------------------------+
2026-09-14T13:07:41Z INF |  Your quick Tunnel has been created! Visit it at              |
2026-09-14T13:07:41Z INF |  https://pending-values-phil-alike.trycloudflare.com          |
2026-09-14T13:07:41Z INF +--------------------------------------------------------------+
2026-09-14T13:07:41Z INF Settings: map[ha-connections:1 url:ssh://localhost:22]
""";

    [Fact]
    public void Достаёт_имя_из_рамки()
    {
        var ok = CloudflaredOutputParser.TryFindHostname(РеальныйЛог, out var host);

        Assert.True(ok);
        Assert.Equal("pending-values-phil-alike.trycloudflare.com", host);
    }

    [Fact]
    public void Схема_и_рамка_в_имя_не_попадают()
    {
        CloudflaredOutputParser.TryFindHostname(РеальныйЛог, out var host);

        Assert.DoesNotContain("https", host);
        Assert.DoesNotContain("|", host);
        Assert.DoesNotContain(" ", host);
    }

    [Fact]
    public void Пока_имени_нет_возвращает_false()
    {
        var частичный = "2026-09-14T13:07:36Z INF Requesting new quick Tunnel on trycloudflare.com...";

        Assert.False(CloudflaredOutputParser.TryFindHostname(частичный, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void Пустой_лог_не_валит_разбор()
    {
        Assert.False(CloudflaredOutputParser.TryFindHostname("", out _));
    }

    [Fact]
    public void Берёт_последнее_имя_если_туннель_переподнимали()
    {
        var дважды = РеальныйЛог + "\n" +
            "2026-09-14T14:00:00Z INF |  https://second-name-here.trycloudflare.com  |";

        CloudflaredOutputParser.TryFindHostname(дважды, out var host);

        Assert.Equal("second-name-here.trycloudflare.com", host);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~CloudflaredOutputParserTests`
Expected: FAIL — `CloudflaredOutputParser` не существует.

- [ ] **Step 3: Написать минимальную реализацию**

Создать `src/SzDiag.Agent/CloudflaredOutputParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace SzDiag.Agent;

/// <summary>Разбор вывода `cloudflared tunnel --url ...`. Имя quick tunnel'а печатается
/// только в лог, отдельного способа его узнать нет — приходится вылавливать.</summary>
public static partial class CloudflaredOutputParser
{
    /// <summary>Имя выдаётся внутри ASCII-рамки: `|  https://<имя>.trycloudflare.com  |`.</summary>
    [GeneratedRegex(@"https://([a-z0-9-]+\.trycloudflare\.com)", RegexOptions.IgnoreCase)]
    private static partial Regex HostnameRegex();

    /// <summary>Последнее встреченное имя: при переподнятии туннеля в том же логе окажется
    /// несколько, актуально последнее.</summary>
    public static bool TryFindHostname(string log, out string? hostname)
    {
        hostname = null;
        if (string.IsNullOrEmpty(log)) return false;

        var matches = HostnameRegex().Matches(log);
        if (matches.Count == 0) return false;

        hostname = matches[^1].Groups[1].Value;
        return true;
    }
}
```

Создать `src/SzDiag.Agent/IAccessTunnel.cs`:

```csharp
namespace SzDiag.Agent;

/// <summary>Жизненный цикл quick tunnel'а к нашему sshd. Абстракция ради подмены в тестах:
/// реальная реализация запускает внешний процесс и ходит в сеть, в `dotnet test` живой
/// Cloudflare не вызывается никогда.</summary>
public interface IAccessTunnel
{
    /// <summary>Поднять туннель на порт sshd. Возвращает выданное имя либо null, если имя не
    /// появилось за <paramref name="timeout"/> — это не отказ сессии: управляющий канал
    /// отдельный, и без SSH заявка продолжает работать через exec.</summary>
    string? Start(int sshPort, string taskName, TimeSpan timeout);

    /// <summary>Снять туннель (идемпотентно): задача и процесс.</summary>
    void Stop(string taskName);
}
```

Создать `src/SzDiag.Agent/CloudflaredTunnel.cs` — боевую реализацию. Запуск **транзиентной
scheduled task под SYSTEM**, тем же способом, каким `PortableSshServer` поднимает sshd:
посмотреть, как это сделано там, и повторить, а не изобретать. Ключевые моменты:

```csharp
namespace SzDiag.Agent;

/// <summary>Quick tunnel через внешний cloudflared.exe.
///
/// Запуск — транзиентной scheduled task под SYSTEM, как sshd: обычный дочерний процесс
/// умирает вместе с SSH-сессией, а под нагрузкой (OCCT) сессия рвётся — это уже проходили
/// с `lhmmon`. Вывод пишем в файл рядом с exe: имя туннеля печатается только в лог.</summary>
public sealed class CloudflaredTunnel : IAccessTunnel
{
    private readonly string _exePath;
    private readonly string _logPath;

    public CloudflaredTunnel(string exePath, string logPath)
    {
        _exePath = exePath;
        _logPath = logPath;
    }

    public string? Start(int sshPort, string taskName, TimeSpan timeout)
    {
        // 1. Снести прежний лог: иначе TryFindHostname подберёт имя от прошлого запуска.
        // 2. schtasks /create /ru SYSTEM ... с командой
        //    cmd /c "<exe>" tunnel --url ssh://127.0.0.1:<sshPort> --no-autoupdate > "<log>" 2>&1
        // 3. schtasks /run
        // 4. Опрашивать лог до timeout, пока CloudflaredOutputParser не отдаст имя.
        // Не нашли — вернуть null (см. док IAccessTunnel), задачу при этом снять.
        throw new NotImplementedException("заполнить по образцу PortableSshServer");
    }

    public void Stop(string taskName)
    {
        // schtasks /delete /f + добить процесс по пути exe (идемпотентно, ошибки глушим).
        throw new NotImplementedException("заполнить по образцу PortableSshServer");
    }
}
```

> `CloudflaredTunnel` юнит-тестами не покрывается (ходит в систему) — ровно как
> `PortableSshServer`. Его проверяет живой прогон из чек-листа.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~CloudflaredOutputParserTests`
Expected: PASS (5 тестов)

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Agent/IAccessTunnel.cs src/SzDiag.Agent/CloudflaredOutputParser.cs src/SzDiag.Agent/CloudflaredTunnel.cs tests/SzDiag.Agent.Tests/CloudflaredOutputParserTests.cs
git commit -m "feat(agent): интерфейс туннеля и разбор имени из вывода cloudflared"
```

---

### Task 6: Туннель в `Open` и `Revert`

**Files:**
- Modify: `src/SzDiag.Agent/RevertState.cs`
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`
- Test: `tests/SzDiag.Agent.Tests/AccessTunnelLifecycleTests.cs`

**Interfaces:**
- Consumes: `IAccessTunnel` (Task 5).
- Produces: поля `RevertState.StartedQuickTunnel` (bool), `TunnelTaskName` (string),
  `QuickTunnelHost` (string), `DeployedCloudflared` (bool);
  `WindowsSystemAccessManager` принимает `IAccessTunnel?` через конструктор.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Agent.Tests/AccessTunnelLifecycleTests.cs`:

```csharp
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class AccessTunnelLifecycleTests
{
    private sealed class FakeTunnel : IAccessTunnel
    {
        public string? ИмяКВыдаче = "aaa-bbb.trycloudflare.com";
        public bool Поднят;
        public bool Снят;
        public int ПорядокСнятия;

        public string? Start(int sshPort, string taskName, TimeSpan timeout)
        {
            Поднят = true;
            return ИмяКВыдаче;
        }

        public void Stop(string taskName)
        {
            Снят = true;
            ПорядокСнятия = ++Счётчик.Значение;
        }
    }

    private sealed class ПадающийSsh : ISshServer
    {
        public int ПорядокСнятия;
        public void Start(int port, string authorizedKeyLine, string taskName) { }
        public void Stop(string taskName)
        {
            ПорядокСнятия = ++Счётчик.Значение;
            throw new InvalidOperationException("sshd не снялся");
        }
        public string WorkDir => @"C:\temp\sshd";
    }

    private static class Счётчик { public static int Значение; }

    [Fact]
    public void Open_записывает_имя_туннеля_в_состояние()
    {
        var tunnel = new FakeTunnel();
        var manager = AccessManagerFixture.Create(tunnel);   // см. существующие тесты Open/Revert

        var state = manager.Open(AccessManagerFixture.Spec("162003"));

        Assert.True(state.StartedQuickTunnel);
        Assert.Equal("aaa-bbb.trycloudflare.com", state.QuickTunnelHost);
        Assert.NotEmpty(state.TunnelTaskName);
    }

    [Fact]
    public void Туннель_не_поднялся_доступ_всё_равно_открыт()
    {
        // Без SSH заявка продолжает работать через exec-канал: валить Open нельзя.
        var tunnel = new FakeTunnel { ИмяКВыдаче = null };
        var manager = AccessManagerFixture.Create(tunnel);

        var state = manager.Open(AccessManagerFixture.Spec("162003"));

        Assert.False(state.StartedQuickTunnel);
        Assert.Null(state.QuickTunnelHost);
        Assert.True(state.CreatedSshdTask);
    }

    [Fact]
    public void Revert_снимает_туннель_раньше_sshd()
    {
        // Иначе между снятием sshd и снятием туннеля наружу торчит дверь в разваливающийся
        // доступ.
        Счётчик.Значение = 0;
        var tunnel = new FakeTunnel();
        var ssh = new ПадающийSsh();
        var manager = AccessManagerFixture.Create(tunnel, ssh);
        var state = manager.Open(AccessManagerFixture.Spec("162003"));

        manager.Revert(state);

        Assert.True(tunnel.Снят);
        Assert.True(tunnel.ПорядокСнятия < ssh.ПорядокСнятия);
    }

    [Fact]
    public void Туннель_снимается_даже_когда_падает_шаг_sshd()
    {
        // Инвариант «каждый шаг в своём try/catch»: на 160705 одно исключение оставило
        // доступ на машине навсегда.
        Счётчик.Значение = 0;
        var tunnel = new FakeTunnel();
        var manager = AccessManagerFixture.Create(tunnel, new ПадающийSsh());
        var state = manager.Open(AccessManagerFixture.Spec("162003"));

        var outcome = manager.Revert(state);

        Assert.True(tunnel.Снят);
        Assert.NotEmpty(outcome.Failed);      // падение sshd зафиксировано, но не проглочено
    }

    [Fact]
    public void Revert_идемпотентен_по_туннелю()
    {
        var tunnel = new FakeTunnel();
        var manager = AccessManagerFixture.Create(tunnel);
        var state = manager.Open(AccessManagerFixture.Spec("162003"));

        manager.Revert(state);
        var ex = Record.Exception(() => manager.Revert(state));

        Assert.Null(ex);
    }
}
```

> `AccessManagerFixture` — вспомогательный конструктор менеджера с фейковыми зависимостями.
> Если такого в `tests/SzDiag.Agent.Tests` ещё нет, посмотреть, как существующие тесты
> `Open`/`Revert` собирают `WindowsSystemAccessManager`, и вынести это в фикстуру, а не
> дублировать в каждом тесте.

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AccessTunnelLifecycleTests`
Expected: FAIL — полей в `RevertState` нет, менеджер не принимает `IAccessTunnel`.

- [ ] **Step 3: Написать минимальную реализацию**

В `src/SzDiag.Agent/RevertState.cs` добавить:

```csharp
    /// <summary>Имя quick tunnel'а этой сессии. Меняется при каждом переподнятии (туннель не
    /// сохраняет hostname), поэтому хранится ради отката и диагностики, а не как адрес.</summary>
    public string QuickTunnelHost { get; set; } = "";

    /// <summary>Задача, под которой крутится cloudflared (транзиентная, под SYSTEM).</summary>
    public string TunnelTaskName { get; set; } = "";

    public bool StartedQuickTunnel { get; set; }

    /// <summary>Клали ли cloudflared.exe мы. Если бинарь был на машине до нас — при откате
    /// не трогаем: чужое не наше дело.</summary>
    public bool DeployedCloudflared { get; set; }
```

В `WindowsSystemAccessManager`: принять `IAccessTunnel? tunnel` в конструктор (null — туннель
не используется, прямой режим). В `Open` **после** успешного подъёма sshd добавить шаг:

```csharp
        // Шаг 9: публикация sshd через quick tunnel. Делается последним — туннель бессмысленен
        // без живого sshd. Отсутствие туннеля доступ не отменяет: exec-канал работает и без него.
        if (_tunnel is not null)
        {
            try
            {
                var taskName = $"szdiag-cfd-{spec.Sz}";
                var host = _tunnel.Start(spec.SshPort, taskName, TunnelStartTimeout);
                if (!string.IsNullOrWhiteSpace(host))
                {
                    state.StartedQuickTunnel = true;
                    state.TunnelTaskName = taskName;
                    state.QuickTunnelHost = host;
                    SaveState(state);       // прогрессивно, как остальные шаги
                }
                else
                {
                    // Имя не появилось — снять задачу, чтобы не висела пустышка.
                    _tunnel.Stop(taskName);
                }
            }
            catch (Exception ex)
            {
                Log($"туннель не поднялся: {ex.Message}");
            }
        }
```

В `Revert` — **первым шагом**, до снятия sshd:

```csharp
        // Туннель снимается раньше sshd: иначе на время отката наружу остаётся опубликованная
        // дверь в уже разваливающийся доступ.
        if (state.StartedQuickTunnel && _tunnel is not null)
            Step(outcome, "quick tunnel", () =>
            {
                _tunnel.Stop(state.TunnelTaskName);
                state.StartedQuickTunnel = false;
                state.QuickTunnelHost = "";
            });
```

где `Step` — существующий помощник «шаг в своём try/catch с записью в `RevertOutcome`».
Если его нет, обернуть тем же способом, каким обёрнуты соседние шаги.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AccessTunnelLifecycleTests`
Expected: PASS (5 тестов)

- [ ] **Step 5: Прогнать весь набор**

Run: `dotnet test`
Expected: PASS

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Agent/RevertState.cs src/SzDiag.Agent/WindowsSystemAccessManager.cs tests/SzDiag.Agent.Tests/AccessTunnelLifecycleTests.cs
git commit -m "feat(agent): туннель как шаг Open с парным откатом, снимается раньше sshd"
```

---

### Task 7: Агент сообщает адрес хабу

**Files:**
- Modify: `src/SzDiag.Agent/Program.cs`
- Modify: `src/SzDiag.Agent/AgentOptions.cs`
- Create: `src/SzDiag.Agent/AccessReporter.cs`
- Test: `tests/SzDiag.Agent.Tests/AccessReporterTests.cs`

**Interfaces:**
- Consumes: `AccessReportRequest`, `HubRoutes.ReportAccess` (Task 1), `RevertState` (Task 6).
- Produces: `AccessReporter.BuildReport(RevertState state, string sz, bool foundHubByBroadcast)` →
  `AccessReportRequest`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Agent.Tests/AccessReporterTests.cs`:

```csharp
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class AccessReporterTests
{
    [Fact]
    public void Туннель_поднят_режим_туннельный()
    {
        var state = new RevertState
        {
            Sz = "162003",
            StartedQuickTunnel = true,
            QuickTunnelHost = "aaa-bbb.trycloudflare.com",
        };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: false);

        Assert.Equal(AccessMode.Tunnel, report.AccessMode);
        Assert.Equal("aaa-bbb.trycloudflare.com", report.AccessHost);
    }

    [Fact]
    public void Hub_найден_broadcast_ом_режим_прямой()
    {
        // Машина в одной сети с боксом: прямой SSH быстрее и не зависит от Cloudflare.
        var state = new RevertState { Sz = "162003" };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: true);

        Assert.Equal(AccessMode.Direct, report.AccessMode);
        Assert.Null(report.AccessHost);
    }

    [Fact]
    public void Туннель_не_поднялся_но_hub_по_домену_режим_всё_равно_прямой()
    {
        // Честность: раз имени нет, туннельным режим называть нельзя — иначе target
        // напечатает ProxyCommand в никуда.
        var state = new RevertState { Sz = "162003", StartedQuickTunnel = false };

        var report = AccessReporter.BuildReport(state, "162003", foundHubByBroadcast: false);

        Assert.Equal(AccessMode.Direct, report.AccessMode);
        Assert.Null(report.AccessHost);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AccessReporterTests`
Expected: FAIL — `AccessReporter` не существует.

- [ ] **Step 3: Написать минимальную реализацию**

Создать `src/SzDiag.Agent/AccessReporter.cs`:

```csharp
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Сборка отчёта «чем машина доступна» для hub. Вынесено из Program.cs отдельно,
/// чтобы правило выбора режима было проверяемым: ошибка здесь означает, что `szcli target`
/// печатает строку, которая не сработает.</summary>
public static class AccessReporter
{
    /// <param name="foundHubByBroadcast">Hub найден UDP-broadcast'ом, то есть машина в одной
    /// сети с боксом — идём напрямую.</param>
    public static AccessReportRequest BuildReport(RevertState state, string sz,
        bool foundHubByBroadcast)
    {
        var туннельЖив = state.StartedQuickTunnel
                         && !string.IsNullOrWhiteSpace(state.QuickTunnelHost);

        // Туннельным режим называем только когда имя реально есть: иначе target напечатает
        // ProxyCommand в никуда.
        if (foundHubByBroadcast || !туннельЖив)
            return new AccessReportRequest(sz, null, AccessMode.Direct);

        return new AccessReportRequest(sz, state.QuickTunnelHost, AccessMode.Tunnel);
    }
}
```

В `src/SzDiag.Agent/Program.cs`: после открытия доступа и после каждого переподнятия туннеля
(включая путь `--resume`) звать

```csharp
await connection.InvokeAsync(HubRoutes.ReportAccess,
    AccessReporter.BuildReport(state, sz, foundHubByBroadcast));
```

`foundHubByBroadcast` — признак, вернувшийся из резолва адреса хаба: `HubUrl` из конфига → false,
`HubDiscovery.FindHubAsync` → true.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~AccessReporterTests`
Expected: PASS (3 теста)

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Agent/AccessReporter.cs src/SzDiag.Agent/Program.cs src/SzDiag.Agent/AgentOptions.cs tests/SzDiag.Agent.Tests/AccessReporterTests.cs
git commit -m "feat(agent): агент сообщает hub режим доступа и имя туннеля"
```

---

### Task 8: Секрет сессии вместо IP-сверки

**Files:**
- Modify: `src/SzDiag.Hub/SessionRegistry.cs`
- Modify: `src/SzDiag.Hub/AgentHub.cs`
- Modify: `src/SzDiag.Hub/RevertStatusApi.cs`
- Modify: `src/SzDiag.Contracts/HubRoutes.cs`
- Test: `tests/SzDiag.Hub.Tests/SessionSecretAuthTests.cs`

**Interfaces:**
- Consumes: `SessionRegistry` (Task 2).
- Produces: `HubRoutes.SessionSecretHeader = "X-SzDiag-Session"`;
  `string SessionRegistry.IssueSecret(string sz)`; `bool SessionRegistry.SecretMatches(string sz, string? secret)`;
  `RevertStatusApi.IsAuthorizedForSz(SessionRegistry registry, string sz, string? remoteIp, string? sessionSecret)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/SessionSecretAuthTests.cs`:

```csharp
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class SessionSecretAuthTests
{
    [Fact]
    public void Свой_секрет_пропускается()
    {
        var reg = new SessionRegistry(TimeProvider.System);
        reg.Register("162003", "10.0.0.1", "PC", "conn-1");
        var secret = reg.IssueSecret("162003");

        Assert.True(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.9", secret));
    }

    [Fact]
    public void Чужой_секрет_отклоняется_даже_с_того_же_IP()
    {
        // За туннелем RemoteIpAddress у ВСЕХ агентов один (адрес cloudflared), поэтому
        // IP-сверка перестаёт различать машины — вернулась бы дыра Critical-5: заражённый
        // клиент отчитывается за чужую активную СЗ и выкидывает её из реестра.
        var reg = new SessionRegistry(TimeProvider.System);
        reg.Register("162003", "127.0.0.1", "PC", "conn-1");
        reg.IssueSecret("162003");

        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "127.0.0.1", "чужой-секрет"));
    }

    [Fact]
    public void Секрет_не_предъявлен_при_живой_сессии_с_секретом_отклоняется()
    {
        // Fail closed: прежняя проверка фейлилась ОТКРЫТО (info is null → true).
        var reg = new SessionRegistry(TimeProvider.System);
        reg.Register("162003", "127.0.0.1", "PC", "conn-1");
        reg.IssueSecret("162003");

        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "127.0.0.1", null));
    }

    [Fact]
    public void Агент_старой_сборки_откатывается_на_сверку_по_IP()
    {
        // Переходная ветка: у сессии секрета нет, потому что агент его не запрашивал.
        // Убрать вместе с веткой, когда апдейтер выведет флот.
        var reg = new SessionRegistry(TimeProvider.System);
        reg.Register("162003", "10.0.0.1", "PC", "conn-1");

        Assert.True(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.1", null));
        Assert.False(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.2", null));
    }

    [Fact]
    public void Сессии_нет_в_реестре_пропускаем()
    {
        // Обычный случай: watchdog шлёт отчёт как раз потому, что живого коннекта больше нет.
        var reg = new SessionRegistry(TimeProvider.System);

        Assert.True(RevertStatusApi.IsAuthorizedForSz(reg, "162003", "10.0.0.1", null));
    }

    [Fact]
    public void Секрет_у_каждой_СЗ_свой()
    {
        var reg = new SessionRegistry(TimeProvider.System);
        reg.Register("162003", "10.0.0.1", "PC1", "conn-1");
        reg.Register("162004", "10.0.0.2", "PC2", "conn-2");

        Assert.NotEqual(reg.IssueSecret("162003"), reg.IssueSecret("162004"));
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~SessionSecretAuthTests`
Expected: FAIL — `IssueSecret` нет, у `IsAuthorizedForSz` три параметра.

- [ ] **Step 3: Написать минимальную реализацию**

В `HubRoutes.cs`:

```csharp
    // Секрет конкретной сессии (не общий токен агента): им агент подтверждает, что
    // отчитывается за СВОЮ СЗ. Общего токена для этого не хватает — он один на весь флот.
    public const string SessionSecretHeader = "X-SzDiag-Session";
```

В `SessionRegistry`:

```csharp
    private readonly Dictionary<string, string> _secrets = new();

    /// <summary>Выдать секрет сессии. Агент кладёт его в state.json и предъявляет при
    /// headless-откате, когда живого SignalR-коннекта уже нет.</summary>
    public string IssueSecret(string sz)
    {
        var secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        lock (_secrets) _secrets[sz] = secret;
        return secret;
    }

    /// <summary>Есть ли у СЗ выданный секрет (нет — агент старой сборки).</summary>
    public bool HasSecret(string sz)
    {
        lock (_secrets) return _secrets.ContainsKey(sz);
    }

    public bool SecretMatches(string sz, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        lock (_secrets)
        {
            if (!_secrets.TryGetValue(sz, out var known)) return false;
            // Сравнение фиксированного времени: секрет проверяется по сети.
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(known),
                System.Text.Encoding.UTF8.GetBytes(secret));
        }
    }
```

В `RevertStatusApi`:

```csharp
    /// <summary>Привязка «эта СЗ — точно этот агент». Раньше сверяли IP вызова с IP
    /// регистрации, но за Cloudflare Tunnel RemoteIpAddress у всех агентов одинаков (адрес
    /// cloudflared) — сверка совпадала бы всегда. Теперь основной механизм — секрет сессии,
    /// выданный при Register и живущий в state.json агента.</summary>
    public static bool IsAuthorizedForSz(SessionRegistry registry, string sz, string? remoteIp,
        string? sessionSecret)
    {
        var info = registry.TryGetInfo(sz);
        // Сессии нет — сверять не с чем: обычный случай watchdog-отчёта, живого коннекта нет.
        if (info is null) return true;

        // Секрет выдан — требуем его и только его (fail closed).
        if (registry.HasSecret(sz)) return registry.SecretMatches(sz, sessionSecret);

        // Переходная ветка: агент старой сборки секрета не получал. Убрать вместе с веткой,
        // когда апдейтер выведет флот.
        if (remoteIp is null) return true;
        return info.Ip == remoteIp;
    }
```

И в обработчике `/revert-status` передать заголовок:

```csharp
            var sessionSecret = http.Request.Headers[HubRoutes.SessionSecretHeader].ToString();
            if (!IsAuthorizedForSz(registry, report.Sz, http.Connection.RemoteIpAddress?.ToString(),
                    string.IsNullOrEmpty(sessionSecret) ? null : sessionSecret))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
```

В `AgentHub.Register` после успешной регистрации выдать секрет и вернуть его агенту (сменить
возвращаемый тип на `Task<RegisterResponse>` либо отдать отдельным client-методом — выбрать по
тому, как устроены соседние ответы, и не ломать существующих вызывающих).

Агент сохраняет секрет в `state.json` рядом с `RevertState` и шлёт заголовком при
`--revert`-отчёте.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~SessionSecretAuthTests`
Expected: PASS (6 тестов)

- [ ] **Step 5: Прогнать весь набор**

Run: `dotnet test`
Expected: PASS. Существующие тесты `IsAuthorizedForSz` с тремя аргументами — обновить на четыре.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Hub src/SzDiag.Contracts/HubRoutes.cs tests/SzDiag.Hub.Tests/SessionSecretAuthTests.cs
git commit -m "fix(hub): секрет сессии вместо IP-сверки в revert-status (за туннелем IP одинаков)"
```

---

### Task 9: Пиннинг host-ключа

**Files:**
- Create: `src/SzDiag.Hub/KnownHostsWriter.cs`
- Modify: `src/SzDiag.Hub/ManagementApi.cs`
- Modify: `src/SzDiag.Agent/WindowsSystemAccessManager.cs`
- Test: `tests/SzDiag.Hub.Tests/KnownHostsWriterTests.cs`

**Interfaces:**
- Consumes: `SessionInfo.SshHostKeyFingerprint` (Task 1), `AccessReportRequest` (Task 3).
- Produces: `KnownHostsWriter.Write(string root, string sz, string host, string publicKeyLine)` →
  путь к файлу; `KnownHostsWriter.PathFor(string root, string sz)`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/KnownHostsWriterTests.cs`:

```csharp
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class KnownHostsWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "szdiag-kh-" + Guid.NewGuid());

    [Fact]
    public void Пишет_строку_в_формате_known_hosts()
    {
        var path = KnownHostsWriter.Write(_root, "162003", "aaa-bbb.trycloudflare.com",
            "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAI");

        var line = File.ReadAllText(path).Trim();
        Assert.StartsWith("aaa-bbb.trycloudflare.com ssh-ed25519 ", line);
    }

    [Fact]
    public void Новое_имя_туннеля_перезаписывает_файл_а_не_копит()
    {
        // После ребута имя ДРУГОЕ. Если копить, ssh увидит два ключа на разные имена и
        // рано или поздно упрётся в несовпадение.
        KnownHostsWriter.Write(_root, "162003", "старое.trycloudflare.com", "ssh-ed25519 AAAA1");
        var path = KnownHostsWriter.Write(_root, "162003", "новое.trycloudflare.com", "ssh-ed25519 AAAA2");

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("старое.trycloudflare.com", text);
        Assert.Contains("новое.trycloudflare.com", text);
    }

    [Fact]
    public void У_каждой_СЗ_свой_файл()
    {
        var a = KnownHostsWriter.Write(_root, "162003", "a.trycloudflare.com", "ssh-ed25519 AAAA1");
        var b = KnownHostsWriter.Write(_root, "162004", "b.trycloudflare.com", "ssh-ed25519 AAAA2");

        Assert.NotEqual(a, b);
        Assert.DoesNotContain("b.trycloudflare.com", File.ReadAllText(a));
    }

    [Fact]
    public void Мусорный_номер_СЗ_не_уводит_запись_из_корня()
    {
        // Тот же класс дыры, что Critical-5: номер приходит от наименее доверенной машины.
        Assert.ThrowsAny<Exception>(() =>
            KnownHostsWriter.Write(_root, @"..\..\Windows\System32", "x.trycloudflare.com",
                "ssh-ed25519 AAAA1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~KnownHostsWriterTests`
Expected: FAIL — `KnownHostsWriter` не существует.

- [ ] **Step 3: Написать минимальную реализацию**

Создать `src/SzDiag.Hub/KnownHostsWriter.cs`:

```csharp
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Файл known_hosts на каждую СЗ. Нужен, чтобы уйти от StrictHostKeyChecking=no:
/// имя quick tunnel'а публично и не аутентифицировано, без пиннинга подмену не отличить.
///
/// Якорь доверия — управляющий канал (SignalR, аутентифицирован, по TLS): публичный ключ
/// приезжает оттуда, а не с того конца, к которому мы подключаемся.</summary>
public static class KnownHostsWriter
{
    public static string PathFor(string root, string sz)
    {
        // Номер приходит от наименее доверенной машины — без валидации произвольная строка
        // уводит запись куда угодно (тот же класс дыры, что Critical-5).
        if (!SzNumber.IsValid(sz)) throw new ArgumentException(SzNumber.Explain(sz), nameof(sz));
        return Path.Combine(root, sz);
    }

    /// <summary>Перезаписывает файл целиком: после ребута клиента имя туннеля другое, и
    /// копить старые строки — значит однажды упереться в несовпадение ключей.</summary>
    public static string Write(string root, string sz, string host, string publicKeyLine)
    {
        var path = PathFor(root, sz);
        Directory.CreateDirectory(root);
        File.WriteAllText(path, $"{host} {publicKeyLine}\n");
        return path;
    }
}
```

Агент в `AccessReportRequest.SshHostKeyFingerprint` шлёт **строку публичного ключа** из своей
рабочей папки sshd (`ISshServer.WorkDir`, файл `ssh_host_ed25519_key.pub`, без комментария).
Хаб при `ReportAccess` с непустым ключом и непустым `AccessHost` зовёт `KnownHostsWriter.Write`,
а эндпоинт `target` подставляет `KnownHostsWriter.PathFor` в `TargetSsh.Build`.

> Поле названо `SshHostKeyFingerprint`, а несёт полную строку ключа — это осознанно: отпечаток
> в `known_hosts` не годится, туда нужен сам ключ. Если имя поля начнёт путать, переименовать
> в `SshHostPublicKey` **одним** заходом по всем трём проектам.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~KnownHostsWriterTests`
Expected: PASS (4 теста)

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Hub/KnownHostsWriter.cs src/SzDiag.Hub/ManagementApi.cs src/SzDiag.Agent/WindowsSystemAccessManager.cs tests/SzDiag.Hub.Tests/KnownHostsWriterTests.cs
git commit -m "feat(hub): пиннинг host-ключа на время заявки вместо StrictHostKeyChecking=no"
```

---

### Task 10: Service token и порядок поиска хаба

**Files:**
- Modify: `src/SzDiag.Agent/AgentOptions.cs`
- Modify: `src/SzDiag.Agent/Program.cs`
- Modify: `src/SzDiag.Updater/UpdaterOptions.cs`
- Create: `src/SzDiag.Contracts/HubResolver.cs`
- Modify: `tools/build-dist.ps1`
- Test: `tests/SzDiag.Agent.Tests/HubResolverTests.cs`

**Interfaces:**
- Consumes: `HubDiscovery.FindHubAsync` (существует).
- Produces: `HubResolver.ResolveAsync(string? configuredUrl, Func<Task<string>> broadcast)` →
  `(string Url, bool FoundByBroadcast)`; `AgentOptions.AccessClientId` / `AccessClientSecret`.

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Agent.Tests/HubResolverTests.cs`:

```csharp
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class HubResolverTests
{
    [Fact]
    public async Task Домен_из_конфига_идёт_первым()
    {
        var (url, byBroadcast) = await HubResolver.ResolveAsync("https://hub.example.com",
            () => throw new InvalidOperationException("broadcast звать не должны"));

        Assert.Equal("https://hub.example.com", url);
        Assert.False(byBroadcast);
    }

    [Fact]
    public async Task Пустой_конфиг_уходит_в_broadcast()
    {
        var (url, byBroadcast) = await HubResolver.ResolveAsync(null,
            () => Task.FromResult("http://192.168.1.10:5000"));

        Assert.Equal("http://192.168.1.10:5000", url);
        Assert.True(byBroadcast);
    }

    [Fact]
    public async Task Домен_недоступен_откатываемся_на_broadcast()
    {
        // Cloudflare лёг целиком: для машины в LAN бокса это полный обход.
        var (url, byBroadcast) = await HubResolver.ResolveAsync("https://hub.example.com",
            () => Task.FromResult("http://192.168.1.10:5000"),
            probe: _ => Task.FromResult(false));

        Assert.Equal("http://192.168.1.10:5000", url);
        Assert.True(byBroadcast);
    }

    [Fact]
    public async Task Оба_пути_мертвы_бросаем_HubNotFound()
    {
        await Assert.ThrowsAsync<HubNotFoundException>(() => HubResolver.ResolveAsync(
            "https://hub.example.com",
            () => throw new HubNotFoundException("нет hub"),
            probe: _ => Task.FromResult(false)));
    }
}
```

- [ ] **Step 2: Прогнать тест и убедиться, что падает**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HubResolverTests`
Expected: FAIL — `HubResolver` не существует.

- [ ] **Step 3: Написать минимальную реализацию**

Создать `src/SzDiag.Contracts/HubResolver.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>Порядок поиска hub: сначала постоянный домен из конфига, при неудаче —
/// UDP-broadcast по локалке. Broadcast не выбрасывается вместе с переездом на домен: он
/// остаётся рабочим путём, когда интернета нет, а машина стоит в одной сети с боксом.</summary>
public static class HubResolver
{
    /// <param name="probe">Проверка доступности адреса. По умолчанию считаем доступным:
    /// реальную проверку делает уже само подключение.</param>
    public static async Task<(string Url, bool FoundByBroadcast)> ResolveAsync(
        string? configuredUrl, Func<Task<string>> broadcast,
        Func<string, Task<bool>>? probe = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            var alive = probe is null || await probe(configuredUrl);
            if (alive) return (configuredUrl, false);
        }

        return (await broadcast(), true);
    }
}
```

В `AgentOptions` и `UpdaterOptions` добавить:

```csharp
    /// <summary>Service token приложения Access перед hub. Пара заголовков
    /// CF-Access-Client-Id / CF-Access-Client-Secret. Пусто — Access не используется
    /// (hub в локальной сети).</summary>
    public string AccessClientId { get; set; } = "";
    public string AccessClientSecret { get; set; } = "";
```

В `Program.cs` агента при сборке SignalR-подключения добавлять заголовки, когда они заданы:

```csharp
    .WithUrl(hubUrl + HubRoutes.Path, o =>
    {
        o.Headers[HubRoutes.TokenHeader] = opts.AgentToken;
        if (!string.IsNullOrWhiteSpace(opts.AccessClientId))
        {
            o.Headers["CF-Access-Client-Id"] = opts.AccessClientId;
            o.Headers["CF-Access-Client-Secret"] = opts.AccessClientSecret;
        }
    })
```

В `tools/build-dist.ps1` — параметры `-HubUrl`, `-AccessClientId`, `-AccessClientSecret`,
попадающие в `dist\client\appsettings.json` рядом с `AgentToken`. Значения по умолчанию пустые:
без них собирается прежняя локальная конфигурация.

> **Не хардкодить реальный домен ни в скрипте, ни в шаблоне конфига.** Значение передаётся
> параметром при сборке dist.

- [ ] **Step 4: Прогнать тест — должен пройти**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HubResolverTests`
Expected: PASS (4 теста)

- [ ] **Step 5: Прогнать весь набор и собрать dist**

Run: `dotnet test`
Expected: PASS

Run: `.\tools\build-dist.ps1`
Expected: собирается, `dist\client\appsettings.json` содержит пустые `AccessClientId` /
`AccessClientSecret` и прежний `AgentToken`.

- [ ] **Step 6: Коммит**

```bash
git add src/SzDiag.Contracts/HubResolver.cs src/SzDiag.Agent src/SzDiag.Updater tools/build-dist.ps1 tests/SzDiag.Agent.Tests/HubResolverTests.cs
git commit -m "feat(agent): домен как основной адрес hub, broadcast как запасной, Access service token"
```

---

### Task 11: Доставка cloudflared на клиента и документация

**Files:**
- Modify: `tools/build-dist.ps1`
- Modify: `CLAUDE.md`
- Modify: `docs/dev-knowledge-base.md`
- Create: `docs/live-checklist-2026-09-14.md`
- Test: ручная проверка (живой прогон), юнит-тестов нет

**Interfaces:**
- Consumes: всё предыдущее.
- Produces: cloudflared в каталоге раздачи `Hub.ToolsRoot`; чек-лист живой проверки.

- [ ] **Step 1: Положить cloudflared в каталог раздачи**

В `tools/build-dist.ps1` — шаг, который кладёт `cloudflared.exe` в `client-tools\cloudflared\`
(туда же, где tm5/occt/furmark), если он найден на боксе по
`C:\Program Files (x86)\cloudflared\cloudflared.exe`. Не найден — предупреждение, не ошибка:
доставка не обязательна для локальной конфигурации.

Проверить: `szcli push --list` показывает `cloudflared`.

- [ ] **Step 2: Обновить CLAUDE.md**

В разделе «Статус / следующее» — абзац про новый режим доступа со ссылкой на спеку. В описании
`SzDiag.Agent` — упоминание шага 9 `Open`. В разделе про жизненный цикл доступа — что туннель
снимается первым в `Revert`.

- [ ] **Step 3: Обновить dev-knowledge-base.md**

Добавить `ReportAccess` в таблицу методов SignalR, `X-SzDiag-Session` — в раздел заголовков,
новые поля `SessionInfo` — в описание сессии. Файл обязан оставаться актуальным при правках
протокола (правило из CLAUDE.md).

- [ ] **Step 4: Написать чек-лист живой проверки**

Создать `docs/live-checklist-2026-09-14.md`:

```markdown
# Живая проверка: доступ через Cloudflare Tunnel (2026-09-14)

Проверяется на первой же онлайн-СЗ. До прохождения этого списка схема остаётся
**дополнением** к прямому доступу, а не заменой.

## Базовый поток
- [ ] `szcli push <СЗ> cloudflared` доставил бинарь, sha256 сошёлся
- [ ] Агент поднял туннель, `szcli list` показывает СЗ online
- [ ] `szcli target <СЗ>` печатает строку с ProxyCommand
- [ ] Строка скопирована и вставлена как есть — SSH подключился
- [ ] Host-ключ проверился строго (StrictHostKeyChecking=yes), предупреждений нет

## Скорость и задержка (замер на боксе не показателен — оба конца были на одном канале)
- [ ] Реальная скорость `szcli pull` файла 50+ МБ: ____ MB/s
- [ ] Задержка интерактивного SSH на глаз: печать не отстаёт / отстаёт

## Ребут
- [ ] После ребута клиента СЗ вернулась online сама
- [ ] `AccessHost` в `szcli list` **другой**, чем был до ребута
- [ ] `szcli target` ведёт на новое имя, подключение работает

## Под нагрузкой
- [ ] Под OCCT туннель жив (сейчас под нагрузкой глохнет sshd — проверить, глохнет ли туннель)
- [ ] `szcli exec --detach` по-прежнему проходит

## Честность
- [ ] При снятом туннеле `szcli target` говорит «туннель не поднят», а не печатает строку
- [ ] `szcli close` откатил всё: задачи `szdiag-cfd-*` нет, процесса cloudflared нет
- [ ] `szcli client info` не показывает остатков
```

- [ ] **Step 5: Прогнать весь набор и собрать dist**

Run: `dotnet test`
Expected: PASS

Run: `.\tools\build-dist.ps1`
Expected: собирается, cloudflared в `client-tools`.

- [ ] **Step 6: Коммит**

```bash
git add tools/build-dist.ps1 CLAUDE.md docs/dev-knowledge-base.md docs/live-checklist-2026-09-14.md
git commit -m "docs(tunnel): доставка cloudflared, чек-лист живой проверки, обновление доки"
```

---

## Самопроверка плана

**Покрытие спеки.** Канал управления — Task 10. Канал доступа — Task 5-7. Адресация
(`LanIp`/`AccessHost`/`AccessMode`) — Task 1-2, потребление в Task 4. Доставка cloudflared —
Task 11. Жизненный цикл `Open`/`Revert` — Task 6. Ребут и смена имени — Task 2 (`SetAccess`),
Task 7 (`--resume`), чек-лист в Task 11. Секрет сессии — Task 8. Host-ключ — Task 9.
Запрет на персертификаты `sz-<номер>` — требование «ничего не делать», отдельной задачи не
требует; зафиксировано в спеке. Деградация — Task 4 (`Unavailable`), Task 6 (Open не падает),
Task 10 (фоллбэк на broadcast). План Б с именованным туннелем на ноуте — сознательно не
реализуется.

**Известные места, требующие чтения соседнего кода** (не placeholder'ы — там есть рабочий
образец в репозитории): фикстуры `AgentHubTestFixture` и `AccessManagerFixture` берутся из
существующих тестов; тело `CloudflaredTunnel` пишется по образцу `PortableSshServer`; форма
ответа `Register` с секретом выбирается по тому, как устроены соседние ответы hub.

**Согласованность типов.** `AccessMode` — строковые константы во всех задачах.
`AccessHost` — `string?` везде. `TargetSsh.Build` вызывается с одной сигнатурой в Task 4 и
Task 9. `IsAuthorizedForSz` — четыре параметра начиная с Task 8, существующие вызовы правятся
там же. Поле `SshHostKeyFingerprint` несёт полную строку ключа — расхождение имени и смысла
отмечено в Task 9 явно.
