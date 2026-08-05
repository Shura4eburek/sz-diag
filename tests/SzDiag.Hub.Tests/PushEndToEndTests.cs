using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Сквозная проверка доставки инструментов: настоящий обработчик агента
/// (<see cref="PushCommandHandler"/>) качает файлы с настоящей раздачи hub по HTTP,
/// команда идёт по настоящему SignalR. Ровно тот путь, которым на живых заявках не смог
/// пойти SMB (бэклог п.1).</summary>
public class PushEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-push-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-push-{Guid.NewGuid():N}");
    private readonly string _toolsRoot = Path.Combine(Path.GetTempPath(), $"sztools-push-{Guid.NewGuid():N}");
    private readonly string _clientDir = Path.Combine(Path.GetTempPath(), $"szclient-push-{Guid.NewGuid():N}");

    public PushEndToEndTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(Path.Combine(_toolsRoot, "occt", "schedules"));
        File.WriteAllBytes(Path.Combine(_toolsRoot, "occt", "OCCTCmd.exe"), Bytes(50_000));
        File.WriteAllText(Path.Combine(_toolsRoot, "occt", "schedules", "long.json"), "{\"Periods\":[]}");
        Directory.CreateDirectory(_clientDir);

        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .UseSetting("Hub:ToolsRoot", _toolsRoot)
             .WithoutSystemLogging());
    }

    /// <summary>Агент: SignalR для команды + HTTP-клиент к раздаче hub (как в бою).</summary>
    private async Task<HubConnection> ConnectAgentAsync(string sz, string targetDir)
    {
        var handler = _factory.Server.CreateHandler();
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "agents"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
                o.Headers[HubRoutes.TokenHeader] = "test-token";
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, "test-token");
        var push = new PushCommandHandler(http, targetDir);
        conn.On<PushRequest>(HubRoutes.Push, async req =>
        {
            var result = await push.HandleAsync(req);
            await conn.InvokeAsync(HubRoutes.PushResult, result);
        });

        await conn.StartAsync();
        await conn.InvokeAsync(HubRoutes.Register, new RegisterRequest(sz, "PC-1"));
        return conn;
    }

    private HttpClient Cli()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(ManagementApi.TokenHeader, "mgmt-token");
        return c;
    }

    private static byte[] Bytes(int size)
    {
        var data = new byte[size];
        new Random(5).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task Push_DeliversToolWithNestedFiles()
    {
        await using var agent = await ConnectAgentAsync("160705", _clientDir);

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160705/push", new PushCommandRequest("occt"));
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<PushResult>();

        Assert.Null(result!.Error);
        Assert.Equal(2, result.Downloaded);
        Assert.Equal(0, result.Skipped);

        var exe = Path.Combine(_clientDir, "occt", "OCCTCmd.exe");
        var schedule = Path.Combine(_clientDir, "occt", "schedules", "long.json");
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(schedule), "вложенные папки инструмента должны сохраняться");
        Assert.Equal(File.ReadAllBytes(Path.Combine(_toolsRoot, "occt", "OCCTCmd.exe")), File.ReadAllBytes(exe));
    }

    [Fact]
    public async Task Push_Repeat_SkipsFilesAlreadyThere()
    {
        // Повторный push после обрыва должен дотягивать остаток, а не 300 МБ заново.
        await using var agent = await ConnectAgentAsync("160706", _clientDir);
        await Cli().PostAsJsonAsync("/api/sessions/160706/push", new PushCommandRequest("occt"));

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160706/push", new PushCommandRequest("occt"));
        var result = await resp.Content.ReadFromJsonAsync<PushResult>();

        Assert.Equal(0, result!.Downloaded);
        Assert.Equal(2, result.Skipped);
        Assert.Equal(0, result.Bytes);
    }

    [Fact]
    public async Task Push_UnknownTool_ReportsErrorInsteadOfHanging()
    {
        await using var agent = await ConnectAgentAsync("160707", _clientDir);

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160707/push",
            new PushCommandRequest("нет-такого-тула"));
        var result = await resp.Content.ReadFromJsonAsync<PushResult>();

        Assert.NotNull(result!.Error);
        Assert.Equal(0, result.Downloaded);
    }

    [Fact]
    public async Task Push_OfflineSz_Returns404()
    {
        var resp = await Cli().PostAsJsonAsync("/api/sessions/999999/push", new PushCommandRequest("occt"));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ToolsApi_WithoutAgentToken_Unauthorized()
    {
        // Раздача инструментов закрыта тем же токеном, что и пакет агента.
        var resp = await _factory.CreateClient().GetAsync(ToolRoutes.ListRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task ToolsList_ThroughManagementApi_ShowsCatalog()
    {
        var tools = await Cli().GetFromJsonAsync<List<ToolInfo>>("/api/tools");

        var occt = Assert.Single(tools!);
        Assert.Equal("occt", occt.Name);
        Assert.Equal(2, occt.Files);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _kbRoot, _toolsRoot, _clientDir })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
