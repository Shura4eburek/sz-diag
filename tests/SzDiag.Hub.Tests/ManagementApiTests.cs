using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class ManagementApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-mgmt-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-mgmt-{Guid.NewGuid():N}");

    public ManagementApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:AgentToken", "agent-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .WithoutSystemLogging());
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
            .Register("156899", "10.0.0.77", "PC-9", "conn-9");

        var target = await Client().GetFromJsonAsync<TargetInfo>("/api/sessions/156899/target");

        Assert.Equal("10.0.0.77", target!.Ip);
        Assert.Equal("svc-diag", target.User);
        Assert.Equal("ssh svc-diag@10.0.0.77", target.Ssh);
        Assert.Null(target.Unavailable);
    }

    [Fact]
    public async Task Target_ТуннельныйРежим_АдресуетПоИмениТуннеля()
    {
        _factory.Services.GetRequiredService<SessionRegistry>()
            .Register("156900", "127.0.0.1", "PC-10", "conn-10",
                accessHost: "aaa-bbb.trycloudflare.com", accessMode: AccessMode.Tunnel);

        var target = await Client().GetFromJsonAsync<TargetInfo>("/api/sessions/156900/target");

        // Ip за туннелем — адрес cloudflared, подключаться по нему нельзя.
        Assert.Equal("aaa-bbb.trycloudflare.com", target!.AccessHost);
        Assert.Equal(AccessMode.Tunnel, target.AccessMode);
        Assert.Contains("aaa-bbb.trycloudflare.com", target.Ssh);
        Assert.Null(target.Unavailable);
    }

    // Честность отчётов: печатать заведомо нерабочую строку — врать. Сессия при этом жива,
    // управляющий канал отдельный от SSH.
    [Fact]
    public async Task Target_ТуннельНеПоднят_НазываетПричинуВместоСтроки()
    {
        _factory.Services.GetRequiredService<SessionRegistry>()
            .Register("156901", "127.0.0.1", "PC-11", "conn-11",
                accessHost: null, accessMode: AccessMode.Tunnel);

        var target = await Client().GetFromJsonAsync<TargetInfo>("/api/sessions/156901/target");

        Assert.NotNull(target!.Unavailable);
        Assert.Contains("туннель не поднят", target.Unavailable);
        Assert.Equal("", target.Ssh);
    }

    // review W2 I-9: "СЗ не найдена" и "hub не знает такого маршрута" раньше давали один и
    // тот же 404 — CLI против старого hub рапортовал "СЗ не найдена" вместо "hub старее CLI".
    // Эндпоинт обязан отдавать 200 с Sent=false для отсутствующей сессии — 404 остаётся ТОЛЬКО
    // за отсутствующим маршрутом (старый hub).
    [Fact]
    public async Task AgentRestart_UnknownSz_ReturnsOkWithSentFalse_NotNotFound()
    {
        var resp = await Client().PostAsync("/api/sessions/000000/agent/restart", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RestartAgentResponse>();
        Assert.False(body!.Sent);
    }

    [Fact]
    public async Task AgentRestart_KnownOnlineSz_ReturnsOkWithSentTrue()
    {
        _factory.Services.GetRequiredService<SessionRegistry>()
            .Register("156864", "10.0.0.42", "PC-1", "conn-1");

        var resp = await Client().PostAsync("/api/sessions/156864/agent/restart", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<RestartAgentResponse>();
        Assert.True(body!.Sent);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
