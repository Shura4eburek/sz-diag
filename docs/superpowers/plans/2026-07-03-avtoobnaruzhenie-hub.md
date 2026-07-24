# Автообнаружение hub'а по UDP-broadcast — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Агент находит hub в локальной сети автоматически (UDP-broadcast), без ручного ввода IP в конфиге; ручной `HubUrl` остаётся как override.

**Architecture:** Новый лёгкий UDP-протокол (`SzDiag.Contracts.DiscoveryProtocol`, порт 5098, отдельный от TCP/SignalR 5099). Hub слушает `Any:5098` (`HubDiscoveryResponder`, hosted service) и отвечает unicast своим портом только если токен в запросе совпал с `Hub:AgentToken`. Агент (`HubDiscovery.FindHubAsync`) рассылает broadcast по всем локальным подсетям, слушает ответ на том же сокете, с которого слал, повторяя отправку до таймаута. IP hub'а берётся из адреса отправителя ответа — ни одна сторона не должна знать/угадывать свой "правильный" интерфейс, за это отвечает обычная IP-маршрутизация ОС.

**Tech Stack:** .NET 8, `System.Net.Sockets.UdpClient`, `System.Net.NetworkInformation.NetworkInterface`, ASP.NET Core `BackgroundService`, xUnit.

**Проверки после каждой задачи:** `dotnet build` зелёный, `dotnet test` зелёный. Комментарии/вывод — на русском.

---

### Task 1: Протокол обнаружения (`DiscoveryProtocol`)

**Files:**
- Create: `src/SzDiag.Contracts/DiscoveryProtocol.cs`
- Test: `tests/SzDiag.Hub.Tests/DiscoveryProtocolTests.cs`

- [ ] **Step 1: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/DiscoveryProtocolTests.cs`:

```csharp
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class DiscoveryProtocolTests
{
    [Fact]
    public void BuildRequest_TryParseRequest_RoundTrips()
    {
        var msg = DiscoveryProtocol.BuildRequest("dev-token");

        Assert.True(DiscoveryProtocol.TryParseRequest(msg, out var token));
        Assert.Equal("dev-token", token);
    }

    [Fact]
    public void TryParseRequest_WrongPrefix_ReturnsFalse()
        => Assert.False(DiscoveryProtocol.TryParseRequest("garbage", out _));

    [Fact]
    public void BuildResponse_TryParseResponse_RoundTrips()
    {
        var msg = DiscoveryProtocol.BuildResponse(5099);

        Assert.True(DiscoveryProtocol.TryParseResponse(msg, out var port));
        Assert.Equal(5099, port);
    }

    [Fact]
    public void TryParseResponse_WrongPrefix_ReturnsFalse()
        => Assert.False(DiscoveryProtocol.TryParseResponse("garbage", out _));

    [Fact]
    public void TryParseResponse_NonNumericPort_ReturnsFalse()
        => Assert.False(DiscoveryProtocol.TryParseResponse("SZDIAG-HUB:abc", out _));
}
```

- [ ] **Step 2: Запустить — убедиться, что не компилируется**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~DiscoveryProtocolTests`
Expected: ошибка компиляции — нет типа `DiscoveryProtocol`.

- [ ] **Step 3: Реализовать протокол**

Создать `src/SzDiag.Contracts/DiscoveryProtocol.cs`:

```csharp
namespace SzDiag.Contracts;

/// <summary>
/// UDP-протокол обнаружения hub'а в локальной сети (отдельный порт от TCP/SignalR).
/// Агент шлёт broadcast-запрос с pre-shared токеном; hub отвечает unicast своим портом,
/// если токен совпал (иначе молчит — фильтр от постороннего сетевого шума).
/// </summary>
public static class DiscoveryProtocol
{
    public const int Port = 5098;

    private const string RequestPrefix = "SZDIAG-DISCOVER:";
    private const string ResponsePrefix = "SZDIAG-HUB:";

    public static string BuildRequest(string token) => RequestPrefix + token;

    public static bool TryParseRequest(string message, out string token)
    {
        if (message.StartsWith(RequestPrefix, StringComparison.Ordinal))
        {
            token = message[RequestPrefix.Length..];
            return true;
        }
        token = "";
        return false;
    }

    public static string BuildResponse(int hubPort) => ResponsePrefix + hubPort;

    public static bool TryParseResponse(string message, out int hubPort)
    {
        if (message.StartsWith(ResponsePrefix, StringComparison.Ordinal)
            && int.TryParse(message[ResponsePrefix.Length..], out hubPort))
            return true;
        hubPort = 0;
        return false;
    }
}
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~DiscoveryProtocolTests`
Expected: PASS (5 тестов).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Contracts/DiscoveryProtocol.cs tests/SzDiag.Hub.Tests/DiscoveryProtocolTests.cs
git commit -m "feat(contracts): протокол UDP-обнаружения hub'а (DiscoveryProtocol)"
```

---

### Task 2: `HubDiscoveryResponder` на хабе

**Files:**
- Modify: `src/SzDiag.Hub/HubOptions.cs`
- Create: `src/SzDiag.Hub/HubDiscoveryResponder.cs`
- Modify: `src/SzDiag.Hub/Program.cs`
- Test: `tests/SzDiag.Hub.Tests/HubDiscoveryResponderTests.cs`

> Важно: этот hosted service будет подниматься в каждом `WebApplicationFactory` в тестах
> хаба (`AgentHubIntegrationTests`, `ManagementApiTests`). Если два тестовых хоста запустятся
> параллельно, второй не сможет забиндить тот же UDP-порт — это НЕ должно ронять весь hub
> (см. `try/catch (SocketException)` в Step 5 ниже). `HubDiscoveryResponderTests` использует
> свои выделенные высокие порты (15098/15099), поэтому не конфликтует с реальным 5098.

- [ ] **Step 1: Добавить `Port` в `HubOptions`**

В `src/SzDiag.Hub/HubOptions.cs` добавить в конец класса (перед закрывающей `}`):

```csharp

    /// <summary>TCP-порт, на котором слушает hub (для UDP-автообнаружения — что отдавать агенту).</summary>
    public int Port { get; set; } = 5099;
```

- [ ] **Step 2: Написать падающий тест**

Создать `tests/SzDiag.Hub.Tests/HubDiscoveryResponderTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class HubDiscoveryResponderTests
{
    private static async Task<(HubDiscoveryResponder responder, UdpClient sender)> StartAsync(
        int listenPort, string token, int hubPort)
    {
        var options = Options.Create(new HubOptions { AgentToken = token, Port = hubPort });
        var responder = new HubDiscoveryResponder(options, listenPort);
        await responder.StartAsync(CancellationToken.None);
        await Task.Delay(50); // дать сокету открыться
        var sender = new UdpClient(0);
        sender.Connect(IPAddress.Loopback, listenPort);
        return (responder, sender);
    }

    [Fact]
    public async Task ValidToken_RespondsWithHubPort()
    {
        var (responder, sender) = await StartAsync(15098, "dev-token", 5099);
        try
        {
            var request = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildRequest("dev-token"));
            await sender.SendAsync(request, request.Length);

            var receiveTask = sender.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(2000));
            Assert.Same(receiveTask, completed);

            var text = Encoding.UTF8.GetString((await receiveTask).Buffer);
            Assert.True(DiscoveryProtocol.TryParseResponse(text, out var port));
            Assert.Equal(5099, port);
        }
        finally
        {
            sender.Dispose();
            await responder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WrongToken_DoesNotRespond()
    {
        var (responder, sender) = await StartAsync(15099, "dev-token", 5099);
        try
        {
            var request = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildRequest("wrong-token"));
            await sender.SendAsync(request, request.Length);

            var receiveTask = sender.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(500));
            Assert.NotSame(receiveTask, completed);
        }
        finally
        {
            sender.Dispose();
            await responder.StopAsync(CancellationToken.None);
        }
    }
}
```

- [ ] **Step 3: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Hub.Tests --filter FullyQualifiedName~HubDiscoveryResponderTests`
Expected: ошибка компиляции — нет типа `HubDiscoveryResponder`.

- [ ] **Step 4: Реализовать `HubDiscoveryResponder`**

Создать `src/SzDiag.Hub/HubDiscoveryResponder.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Отвечает на UDP-broadcast запросы автообнаружения hub'а в локальной сети.</summary>
public sealed class HubDiscoveryResponder : BackgroundService
{
    private readonly HubOptions _options;
    private readonly int _listenPort;

    public HubDiscoveryResponder(IOptions<HubOptions> options, int listenPort = DiscoveryProtocol.Port)
    {
        _options = options.Value;
        _listenPort = listenPort;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, _listenPort));
            DisableConnectionReset(udp);
        }
        catch (SocketException)
        {
            // Порт уже занят (напр. несколько тестовых хостов параллельно) — автообнаружение
            // просто недоступно на этом экземпляре, остальной hub продолжает работать.
            return;
        }

        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { continue; } // повреждённый пакет — слушаем дальше

                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!DiscoveryProtocol.TryParseRequest(text, out var token)) continue;
                if (token != _options.AgentToken) continue; // чужой/битый токен — молча игнорируем

                var response = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildResponse(_options.Port));
                try { await udp.SendAsync(response, result.RemoteEndPoint, stoppingToken); }
                catch { /* отправитель мог уйти из сети — не критично */ }
            }
        }
    }

    /// <summary>
    /// На Windows отправка UDP-ответа отправителю, который уже ушёл из сети, иногда прилетает
    /// обратно ICMP Port Unreachable, из-за чего следующий ReceiveAsync падает с SocketException
    /// (WSAECONNRESET) — без этой настройки получался бесконечный тугой цикл retry в ExecuteAsync.
    /// SIO_UDP_CONNRESET отключает этот сигнал для connectionless-сокета.
    /// </summary>
    private static void DisableConnectionReset(UdpClient udp)
    {
        const int SioUdpConnReset = -1744830452; // 0x9800000C
        try { udp.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* не Windows или не поддерживается — не критично, просто не подавили сигнал */ }
    }
}
```

- [ ] **Step 5: Зарегистрировать hosted service в `Program.cs`**

В `src/SzDiag.Hub/Program.cs` найти строку `builder.Services.AddHostedService<OfflineSweeper>();` и добавить сразу после неё:

```csharp
builder.Services.AddHostedService(sp =>
    new HubDiscoveryResponder(sp.GetRequiredService<IOptions<HubOptions>>()));
```

(Явная фабрика, а не `AddHostedService<HubDiscoveryResponder>()` — чтобы не полагаться на то,
что DI подставит дефолт для параметра `int listenPort`.)

- [ ] **Step 6: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Hub.Tests --nologo`
Expected: PASS, включая новые тесты и все существующие (в т.ч. `AgentHubIntegrationTests`,
`ManagementApiTests` — не должны падать из-за нового hosted service).

- [ ] **Step 7: Коммит**

```bash
git add src/SzDiag.Hub/HubOptions.cs src/SzDiag.Hub/HubDiscoveryResponder.cs src/SzDiag.Hub/Program.cs tests/SzDiag.Hub.Tests/HubDiscoveryResponderTests.cs
git commit -m "feat(hub): HubDiscoveryResponder — ответ на UDP-обнаружение"
```

---

### Task 3: `HubDiscovery` на агенте

**Files:**
- Create: `src/SzDiag.Agent/HubDiscovery.cs`
- Test: `tests/SzDiag.Agent.Tests/HubDiscoveryTests.cs`

- [ ] **Step 1: Написать падающие тесты**

Создать `tests/SzDiag.Agent.Tests/HubDiscoveryTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class HubDiscoveryTests
{
    [Fact]
    public async Task FindHubAsync_RespondingHub_ReturnsUrl()
    {
        using var fakeHub = new UdpClient(new IPEndPoint(IPAddress.Loopback, 15100));
        var listenTask = Task.Run(async () =>
        {
            var result = await fakeHub.ReceiveAsync();
            var text = Encoding.UTF8.GetString(result.Buffer);
            Assert.True(DiscoveryProtocol.TryParseRequest(text, out var token));
            Assert.Equal("dev-token", token);

            var response = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildResponse(5099));
            await fakeHub.SendAsync(response, result.RemoteEndPoint);
        });

        var url = await HubDiscovery.FindHubAsync("dev-token",
            new[] { IPAddress.Loopback }, 15100, TimeSpan.FromSeconds(2));

        await listenTask;
        Assert.Equal("http://127.0.0.1:5099", url);
    }

    [Fact]
    public async Task FindHubAsync_NoResponse_ThrowsHubNotFoundException()
    {
        await Assert.ThrowsAsync<HubNotFoundException>(() =>
            HubDiscovery.FindHubAsync("dev-token", new[] { IPAddress.Loopback }, 15101, TimeSpan.FromMilliseconds(300)));
    }
}
```

- [ ] **Step 2: Запустить — не компилируется**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HubDiscoveryTests`
Expected: ошибка компиляции — нет типа `HubDiscovery`/`HubNotFoundException`.

- [ ] **Step 3: Реализовать `HubDiscovery`**

Создать `src/SzDiag.Agent/HubDiscovery.cs`:

```csharp
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>hub не откликнулся на автообнаружение за отведённое время.</summary>
public sealed class HubNotFoundException : Exception
{
    public HubNotFoundException(string message) : base(message) { }
}

/// <summary>Находит hub в локальной сети через UDP-broadcast (см. DiscoveryProtocol).</summary>
public static class HubDiscovery
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Разослать broadcast по всем локальным подсетям и дождаться ответа hub'а.</summary>
    public static Task<string> FindHubAsync(string token, int port = DiscoveryProtocol.Port,
        TimeSpan? timeout = null, CancellationToken ct = default)
        => FindHubAsync(token, BroadcastTargets(), port, timeout, ct);

    /// <summary>Тестируемая перегрузка с явным списком адресов для отправки запроса.</summary>
    public static async Task<string> FindHubAsync(string token, IReadOnlyList<IPAddress> targets, int port,
        TimeSpan? timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var payload = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildRequest(token));

        using var udp = new UdpClient();
        // Явный bind ДО старта ReceiveLoopAsync: UdpClient() иначе привязывается лениво при
        // первом Send, а ReceiveAsync на ещё не забинженном сокете кидает исключение
        // синхронно (не await-suspend) — catch{continue} в ReceiveLoopAsync тогда крутится
        // на этом же потоке и никогда не доходит до SendAsync ниже, который и должен был
        // выполнить bind. Итог — livelock на 100% CPU (обнаружено при первом прогоне тестов
        // этой задачи — testhost завис на несколько минут). Явный Bind разрывает эту цепочку.
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;
        DisableConnectionReset(udp);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = ReceiveLoopAsync(udp, linkedCts.Token);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                foreach (var target in targets)
                {
                    try { await udp.SendAsync(payload, new IPEndPoint(target, port), ct); }
                    catch { /* интерфейс мог отвалиться между вызовами — не критично */ }
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var waitFor = remaining < RetryInterval ? remaining : RetryInterval;

                var completed = await Task.WhenAny(receiveTask, Task.Delay(waitFor, ct));
                if (completed == receiveTask) return await receiveTask;
            }
        }
        finally
        {
            linkedCts.Cancel();
            try { await receiveTask; } catch { /* отменили — ожидаемо */ }
        }

        throw new HubNotFoundException(
            "hub не найден в сети. Проверьте подключение к сети сервисного центра или укажите HubUrl вручную в appsettings.json");
    }

    private static async Task<string> ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (true)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // повреждённый пакет — слушаем дальше

            var text = Encoding.UTF8.GetString(result.Buffer);
            if (DiscoveryProtocol.TryParseResponse(text, out var hubPort))
                return $"http://{result.RemoteEndPoint.Address}:{hubPort}";
        }
    }

    /// <summary>
    /// На Windows отправка UDP на адрес без слушателя иногда прилетает обратно ICMP Port
    /// Unreachable, из-за чего следующий ReceiveAsync падает с SocketException (WSAECONNRESET) —
    /// без этой настройки получался бесконечный тугой цикл retry в ReceiveLoopAsync.
    /// SIO_UDP_CONNRESET отключает этот сигнал для connectionless-сокета.
    /// </summary>
    private static void DisableConnectionReset(UdpClient udp)
    {
        const int SioUdpConnReset = -1744830452; // 0x9800000C
        try { udp.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* не Windows или не поддерживается — не критично, просто не подавили сигнал */ }
    }

    /// <summary>Широковещательные адреса всех локальных IPv4-подсетей + 255.255.255.255 подстраховкой.</summary>
    private static IReadOnlyList<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;
                if (ua.IPv4Mask is null) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                targets.Add(new IPAddress(bcast));
            }
        }
        return targets.Distinct().ToList();
    }
}
```

- [ ] **Step 4: Запустить — зелёный**

Run: `dotnet test tests/SzDiag.Agent.Tests --filter FullyQualifiedName~HubDiscoveryTests`
Expected: PASS (2 теста).

- [ ] **Step 5: Коммит**

```bash
git add src/SzDiag.Agent/HubDiscovery.cs tests/SzDiag.Agent.Tests/HubDiscoveryTests.cs
git commit -m "feat(agent): HubDiscovery.FindHubAsync — поиск hub'а по UDP-broadcast"
```

---

### Task 4: Дефолт `AgentOptions.HubUrl` — пусто = автообнаружение

**Files:**
- Modify: `src/SzDiag.Agent/AgentOptions.cs`

> Чисто конфигурационный шаг, отдельного теста нет (в `tests/` нет проверок на дефолт
> `HubUrl` — подтверждено поиском перед написанием плана). Критерий — зелёная сборка.

- [ ] **Step 1: Сменить дефолт и задокументировать поле**

В `src/SzDiag.Agent/AgentOptions.cs` заменить строку `public string HubUrl { get; set; } = "http://localhost:5000";` на:

```csharp
    /// <summary>Адрес hub'а. Пусто — автообнаружение по UDP-broadcast (см. HubDiscovery).
    /// Непустое значение — явный override, автообнаружение не запускается.</summary>
    public string HubUrl { get; set; } = "";
```

- [ ] **Step 2: Собрать — без ошибок**

Run: `dotnet build`
Expected: 0 ошибок.

- [ ] **Step 3: Коммит**

```bash
git add src/SzDiag.Agent/AgentOptions.cs
git commit -m "feat(agent): HubUrl по умолчанию пуст — включает автообнаружение"
```

---

### Task 5: Разводка в `Program.cs` агента

**Files:**
- Modify: `src/SzDiag.Agent/Program.cs`

> Program.cs не покрыт юнит-тестами (как и раньше); критерий — зелёная сборка/тесты и
> ручная/полевая проверка (см. Task 8).

- [ ] **Step 1: Вставить резолюцию hub'а перед созданием `SignalRHubLink`**

В `src/SzDiag.Agent/Program.cs` заменить строку:

```csharp
var manager = new WindowsSystemAccessManager(ps, opts.StatePath);
var link = new SignalRHubLink(opts.HubUrl, opts.AgentToken);
var session = new AgentSession(manager, link, spec, Environment.MachineName);
```

на:

```csharp
var manager = new WindowsSystemAccessManager(ps, opts.StatePath);

var hubUrl = opts.HubUrl;
if (string.IsNullOrWhiteSpace(hubUrl))
{
    Announce("Ищу hub в сети…", "[grey]Ищу hub в сети…[/]");
    try
    {
        hubUrl = await HubDiscovery.FindHubAsync(opts.AgentToken);
        Announce($"Hub найден: {hubUrl}", $"Hub найден: [green]{hubUrl}[/]");
    }
    catch (HubNotFoundException ex)
    {
        Announce(ex.Message, $"[red]{Markup.Escape(ex.Message)}[/]");
        return 1;
    }
}

var link = new SignalRHubLink(hubUrl, opts.AgentToken);
var session = new AgentSession(manager, link, spec, Environment.MachineName);
```

(Паттерн `return 1;` внутри общего `try {...} catch (Exception ex) {...}` блока — тот же,
что уже используется чуть выше для некорректного номера СЗ; `finally { logFile.Flush(); }`
всё равно отработает.)

- [ ] **Step 2: Собрать и прогнать весь солюшен**

Run: `dotnet build && dotnet test`
Expected: 0 ошибок сборки, все тесты PASS.

- [ ] **Step 3: Коммит**

```bash
git add src/SzDiag.Agent/Program.cs
git commit -m "feat(agent): автообнаружение hub'а при пустом HubUrl + понятная ошибка"
```

---

### Task 6: `build-dist.ps1` — дефолт "авто", порт в конфиге хаба, firewall для UDP

**Files:**
- Modify: `tools/build-dist.ps1`

> Скриптовый файл, тестов нет по конвенции репозитория. Критерий — успешный прогон
> скрипта и визуальная проверка сгенерированных конфигов.

- [ ] **Step 1: Сменить дефолт `-HubIp` и обновить doc-комментарий**

В `tools/build-dist.ps1` заменить блок комментария и `param`:

```powershell
<#
.SYNOPSIS
  Собирает готовый к запуску dist: хост (hub + cli) и клиент (agent),
  генерит SSH-ключ сервиса и пишет конфиги.

.PARAMETER HubIp
  Адрес хоста для конфига агента. По умолчанию пусто — агент сам найдёт hub через
  автообнаружение в локальной сети (UDP-broadcast, см. HubDiscovery). Если клиент в
  другой сети/VPN без broadcast, или нужен жёсткий адрес — укажи явно:
    .\tools\build-dist.ps1 -HubIp <HUB_LAN_IP>
    .\tools\build-dist.ps1 -HubIp localhost

.PARAMETER Port
  Порт hub (по умолчанию 5099).

.PARAMETER Token
  Общий токен hub/cli/agent (по умолчанию dev-token).
#>
param(
    [string]$HubIp = "",
    [int]$Port = 5099,
    [string]$Token = "dev-token"
)
```

- [ ] **Step 2: Обновить баннер запуска**

Заменить строку `Write-Host "== sz-diag: сборка dist (HubIp=$HubIp Port=$Port) =="` на:

```powershell
$hubIpLabel = if ([string]::IsNullOrWhiteSpace($HubIp)) { "авто (UDP-обнаружение)" } else { $HubIp }
Write-Host "== sz-diag: сборка dist (HubIp=$hubIpLabel Port=$Port) =="
```

- [ ] **Step 3: Добавить `Port` в конфиг хаба**

В блоке `$hubCfg` добавить строку `"Port": $Port,` сразу после `"ManagementToken": "$Token",`:

```powershell
$hubCfg = @"
{
  "Urls": "http://0.0.0.0:$Port",
  "Hub": {
    "AgentToken": "$Token",
    "ManagementToken": "$Token",
    "Port": $Port,
    "SqliteConnectionString": "Data Source=$db",
    "KnowledgeBaseRoot": "$kb",
    "HeartbeatTimeout": "00:01:00",
    "SweepInterval": "00:00:15"
  }
}
"@
```

- [ ] **Step 4: Условный `HubUrl` в конфиге агента**

Перед блоком `$agentCfg` добавить вычисление, и заменить строку `"HubUrl"` внутри на переменную:

```powershell
$hubUrlValue = if ([string]::IsNullOrWhiteSpace($HubIp)) { "" } else { "http://$($HubIp):$($Port)" }

$agentCfg = @"
{
  "HubUrl": "$hubUrlValue",
  "AgentToken": "$Token",
  "ServiceAccount": "svc-diag",
  "ServicePublicKeyPath": "service_key.pub",
  "SshPort": 22,
  "WatchdogHours": 1,
  "HeartbeatSeconds": 15,
  "StatePath": "C:\\ProgramData\\szdiag\\state.json",
  "TestSuitePath": "testsuite.json"
}
"@
```

- [ ] **Step 5: Напомнить про UDP-порт firewall в финальном баннере**

Заменить последнюю строку скрипта на две:

```powershell
Write-Host "  New-NetFirewallRule -DisplayName szdiag-hub-$Port -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow"
Write-Host "  New-NetFirewallRule -DisplayName szdiag-discovery-5098 -Direction Inbound -Protocol UDP -LocalPort 5098 -Action Allow"
```

- [ ] **Step 6: Прогнать скрипт и проверить конфиги**

Run: `.\tools\build-dist.ps1`
Expected: без ошибок; в выводе `HubIp=авто (UDP-обнаружение)`.

Run: `Get-Content dist\client\appsettings.json`
Expected: `"HubUrl": ""`.

Run: `Get-Content dist\host\hub\appsettings.json`
Expected: содержит `"Port": 5099`.

- [ ] **Step 7: Коммит**

```bash
git add tools/build-dist.ps1
git commit -m "feat(build): dist по умолчанию — автообнаружение hub'а, Port в конфиге хаба"
```

---

### Task 7: Документация

**Files:**
- Modify: `docs/TESTING.md`

- [ ] **Step 1: Обновить шаг открытия портов**

В `docs/TESTING.md` в разделе «## 1. Хост: открыть порт и запустить hub» заменить блок:

```markdown
Порт (PowerShell **от админа**, один раз):
```powershell
New-NetFirewallRule -DisplayName "szdiag-hub-5099" -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow
```
```

на:

```markdown
Порты (PowerShell **от админа**, один раз): TCP — hub/SignalR, UDP — автообнаружение
клиентом.
```powershell
New-NetFirewallRule -DisplayName "szdiag-hub-5099" -Direction Inbound -Protocol TCP -LocalPort 5099 -Action Allow
New-NetFirewallRule -DisplayName "szdiag-discovery-5098" -Direction Inbound -Protocol UDP -LocalPort 5098 -Action Allow
```
```

- [ ] **Step 2: Отметить автообнаружение в шаге запуска агента**

В разделе «## 2. Клиент: запустить агента» после строки `Введи номер СЗ, напр. \`156864\`. Ждём \`доступ открыт ● online\`.` добавить:

```markdown
Если `HubUrl` в `dist\client\appsettings.json` пуст (значение по умолчанию) — агент сам
найдёт hub в локальной сети: `Ищу hub в сети…` → `Hub найден: http://<ip>:5099`. Работает
только в одном сетевом сегменте (broadcast); если хост и клиент разделены роутером/VPN —
укажи `HubUrl` вручную (`http://<IP-хоста>:5099`) или пересобери с `-HubIp <адрес>`.
```

- [ ] **Step 3: Добавить строку в таблицу траблшутинга**

В таблице «## Траблшутинг» добавить строку (после строки про `Test-NetConnection ... False`):

```markdown
| Агент: «hub не найден в сети» | Хост и клиент в разных сегментах (роутер/VPN/VLAN без broadcast) — автообнаружение не проходит. Укажи `HubUrl` вручную в `dist\client\appsettings.json` или пересобери с `-HubIp <IP-хоста>`. Проверь также, что открыт UDP-порт 5098 на хосте. |
```

- [ ] **Step 4: Коммит**

```bash
git add docs/TESTING.md
git commit -m "docs: автообнаружение hub'а в TESTING.md"
```

---

### Task 8: Финальная проверка

- [ ] **Step 1: Полная сборка и тесты**

Run: `dotnet build -c Release && dotnet test`
Expected: 0 ошибок, все тесты PASS (было 102 после прошлой фичи; ожидается 102 + 9 новых
= 111: 5 в `DiscoveryProtocolTests`, 2 в `HubDiscoveryResponderTests`, 2 в `HubDiscoveryTests`).

- [ ] **Step 2: Полевая проверка (вручную, по желанию)**

На хостовой машине: `.\tools\build-dist.ps1` (без `-HubIp`), запустить `start-hub.cmd`.
Скопировать свежий `dist\client\` на клиентскую машину той же сети, запустить
`SzDiag.Agent.exe`. Ожидается: `Ищу hub в сети…` → `Hub найден: http://<IP хоста>:5099`
→ штатная регистрация СЗ (без ручной правки `appsettings.json`).

## Обратная совместимость (для ревью)

- Уже развёрнутые `appsettings.json` с явным `HubUrl` продолжают работать без изменений —
  автообнаружение запускается только при пустом значении.
- `HubDiscoveryResponder` не может уронить hub при занятом UDP-порте (в т.ч. в параллельных
  тестах) — при ошибке бинда просто ничего не делает, HTTP/SignalR продолжают работать.
- Существующие тесты (`AgentHubIntegrationTests`, `ManagementApiTests`, `SessionRegistryTests`
  и т.д.) не зависят от `HubUrl`/`HubOptions.Port` — новые поля с дефолтами их не ломают.
