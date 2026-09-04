using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Critical-1 (ревью волны 1): сводка `RevertResult` (self-revert по клавише C,
/// close с хоста) обязана попадать в то же состояние сессии (<see cref="SessionInfo.RevertNote"/>),
/// что и `/agent/revert-status` (watchdog/headless) — иначе неудачный откат по этому пути
/// не виден нигде, кроме однократного вывода `close`.</summary>
public class AgentHubRevertResultTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-arr-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-arr-{Guid.NewGuid():N}");

    public AgentHubRevertResultTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private HubConnection BuildConnection()
    {
        var handler = _factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    [Fact]
    public async Task RevertResult_Failure_MarksSessionRevertNote()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("160705", "PC-1"));
        await conn.InvokeAsync(HubRoutes.RevertResult,
            new RevertResult("160705", new[] { "firewall" },
                new[] { new RevertResultFailure("sshd", "Access denied") }));

        var info = registry.GetActive().Single(s => s.Sz == "160705");
        Assert.Equal(SessionStatus.Offline, info.Status);
        Assert.Contains("sshd", info.RevertNote);
    }

    [Fact]
    public async Task RevertResult_Success_RemovesSessionFromRegistry()
    {
        var registry = _factory.Services.GetRequiredService<SessionRegistry>();

        await using var conn = BuildConnection();
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("160706", "PC-2"));
        await conn.InvokeAsync(HubRoutes.RevertResult,
            new RevertResult("160706", new[] { "sshd" }, Array.Empty<RevertResultFailure>()));

        Assert.DoesNotContain(registry.GetActive(), s => s.Sz == "160706");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
