using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AgentHubIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-it-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-it-{Guid.NewGuid():N}");

    public AgentHubIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
    }

    private HubConnection BuildConnection(string token)
    {
        var handler = _factory.Server.CreateHandler();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = token;
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    [Fact]
    public async Task Register_MakesSessionVisibleInRegistry()
    {
        var conn = BuildConnection("test-token");
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("156864", "PC-1"));

        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        Assert.Contains(registry.GetActive(), s => s.Sz == "156864" && s.Status == SessionStatus.Online);

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task Hub_CanPushRevertToAgent()
    {
        var conn = BuildConnection("test-token");
        var reverted = new TaskCompletionSource<string>();
        conn.On<string>(HubRoutes.Revert, sz => reverted.TrySetResult(sz));

        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("156865", "PC-2"));

        var registry = _factory.Services.GetRequiredService<SessionRegistry>();
        var connId = registry.TryGetConnectionId("156865");
        Assert.NotNull(connId);

        var hub = _factory.Services.GetRequiredService<IHubContext<AgentHub>>();
        await hub.Clients.Client(connId!).SendAsync(HubRoutes.Revert, "156865");

        var completed = await Task.WhenAny(reverted.Task, Task.Delay(5000));
        Assert.Same(reverted.Task, completed);
        Assert.Equal("156865", await reverted.Task);

        await conn.DisposeAsync();
    }

    [Fact]
    public async Task ReportActivity_UpdatesRegistry()
    {
        var conn = BuildConnection("test-token");
        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest("156866", "PC-3"));

        await conn.InvokeAsync(HubRoutes.ReportActivity, "156866", "Тест OCCT", DateTimeOffset.UtcNow);

        var reg = _factory.Services.GetRequiredService<SessionRegistry>();
        var s = reg.GetActive().Single(x => x.Sz == "156866");
        Assert.Equal("Тест OCCT", s.Activity);
        Assert.NotNull(s.ActivitySince);

        await conn.DisposeAsync();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { /* best effort */ }
    }
}
