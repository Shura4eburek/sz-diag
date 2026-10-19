using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>
/// Агент возвращается после перезапуска hub (СЗ 162003, 16.09.2026, бэклог п.280): живая
/// машина исчезла из сессий после обычного рестарта хаба и сама не вернулась.
///
/// Здесь — настоящий Kestrel на реальном порту (TestServer не подходит: проверяется работа
/// самого `SignalRHubLink` по URL), хост убивается и поднимается заново на том же порту.
/// Инвариант: агент не только переподключился, но и ПЕРЕРЕГИСТРИРОВАЛСЯ — иначе hub
/// адресовал бы команды на мёртвое соединение (п.273).
///
/// Чего тест НЕ проверяет: он зелёный и со старой `WithAutomaticReconnect()` без параметров —
/// её четыре попытки растягиваются дольше номинальных 42 с, потому что каждая висит на
/// таймауте negotiate. Замер: дефолтная политика пересидела 50-секундный простой и
/// зарегистрировалась через 19 мс после подъёма хоста. Ловится только полное отсутствие
/// переподключения.
/// </summary>
public class AgentSurvivesHubRestartTests
{
    /// <summary>Регистрации, дошедшие до ОДНОГО поколения хоста. Счётчик намеренно не
    /// общий: на общем тест был зелёным и со старым кодом — SignalR успевал переподключиться
    /// к ещё не до конца погашенному Kestrel'ю и регистрировался там, а это не возвращение
    /// после перезапуска.</summary>
    private sealed class RegisterLog
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AnyRegistration => _first.Task;

        public void Record() => _first.TrySetResult();
    }

    private sealed class ProbeHub : Microsoft.AspNetCore.SignalR.Hub
    {
        private readonly RegisterLog _log;
        public ProbeHub(RegisterLog log) => _log = log;

        public RegisterResponse Register(RegisterRequest request)
        {
            _log.Record();
            return new RegisterResponse("секрет-сессии");
        }

        public void Heartbeat(string sz) { }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<WebApplication> StartHostAsync(int port, RegisterLog log)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", $"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(log);
        builder.Services.AddSignalR();

        var app = builder.Build();
        app.MapHub<ProbeHub>(HubRoutes.Path);
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task AgentReRegisters_AfterHubGoesDownAndComesBack()
    {
        var port = FreePort();

        var firstLog = new RegisterLog();
        var first = await StartHostAsync(port, firstLog);
        await using var link = new SignalRHubLink($"http://127.0.0.1:{port}", "test-token");

        // Как в AgentSession: после восстановления связи агент обязан перерегистрироваться.
        link.OnReconnected(() => link.RegisterAsync("162003", "PC-1"));

        await link.ConnectAsync();
        await link.RegisterAsync("162003", "PC-1");

        await first.StopAsync();
        await first.DisposeAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));   // агент успевает увидеть обрыв

        var secondLog = new RegisterLog();
        var second = await StartHostAsync(port, secondLog);
        try
        {
            var returned = await Task.WhenAny(secondLog.AnyRegistration, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(returned == secondLog.AnyRegistration,
                "агент не вернулся в hub после его перезапуска");
        }
        finally
        {
            await second.StopAsync();
            await second.DisposeAsync();
        }
    }
}
