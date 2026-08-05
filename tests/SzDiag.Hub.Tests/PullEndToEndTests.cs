using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Сквозная проверка забора файлов: настоящий SignalR-канал + настоящий обработчик
/// агента (<see cref="PullCommandHandler"/>) + HTTP-эндпоинт CLI. Живой e2e на клиентской
/// машине этим не заменить, но весь путь «CLI → hub → агент → чанки → файл на хосте»
/// проверяется без прав администратора (агент требует их из-за app.manifest).</summary>
public class PullEndToEndTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-pull-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-pull-{Guid.NewGuid():N}");
    private readonly string _pullRoot = Path.Combine(Path.GetTempPath(), $"szpulled-{Guid.NewGuid():N}");
    private readonly string _clientDir = Path.Combine(Path.GetTempPath(), $"szclient-{Guid.NewGuid():N}");

    public PullEndToEndTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_clientDir);
        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .UseSetting("Hub:PullRoot", _pullRoot)
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

    /// <summary>Подключает «агента»: реальный обработчик забора поверх SignalR-соединения.</summary>
    private async Task<HubConnection> ConnectAgentAsync(string sz)
    {
        var conn = BuildConnection();
        var handler = new PullCommandHandler(
            (chunk, ct) => conn.InvokeAsync(HubRoutes.PullChunk, chunk, ct),
            chunkBytes: 4096);
        conn.On<PullRequest>(HubRoutes.Pull, async req =>
        {
            var result = await handler.HandleAsync(req);
            await conn.InvokeAsync(HubRoutes.PullResult, result);
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
        new Random(11).NextBytes(data);
        return data;
    }

    [Fact]
    public async Task Pull_ByMask_DeliversFilesToHostIntact()
    {
        // 10 КБ при чанке 4 КБ — три куска: проверяем и сборку, и порядок.
        var dumpA = Bytes(10_000);
        var dumpB = Bytes(1_500);
        File.WriteAllBytes(Path.Combine(_clientDir, "WATCHDOG-a.dmp"), dumpA);
        File.WriteAllBytes(Path.Combine(_clientDir, "WATCHDOG-b.dmp"), dumpB);
        File.WriteAllBytes(Path.Combine(_clientDir, "не-дамп.log"), Bytes(50));

        await using var agent = await ConnectAgentAsync("160705");

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160705/pull",
            new PullCommandRequest(Path.Combine(_clientDir, "*.dmp")));
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<PullResponse>();

        Assert.NotNull(body);
        Assert.Null(body!.Error);
        Assert.Equal(2, body.Files.Count);
        Assert.All(body.Files, f => Assert.False(f.Skipped, f.SkipReason));

        var savedA = body.Files.Single(f => f.Name == "WATCHDOG-a.dmp").SavedPath!;
        var savedB = body.Files.Single(f => f.Name == "WATCHDOG-b.dmp").SavedPath!;
        Assert.Equal(dumpA, File.ReadAllBytes(savedA));
        Assert.Equal(dumpB, File.ReadAllBytes(savedB));
        Assert.Equal(Sha(dumpA), body.Files.Single(f => f.Name == "WATCHDOG-a.dmp").Sha256);

        // Файлы легли вне vault: дампы в git-репозитории базы знаний недопустимы.
        Assert.StartsWith(_pullRoot, savedA);
        Assert.False(Directory.Exists(Path.Combine(_kbRoot, "СЗ", "160705", "pulled")));
    }

    [Fact]
    public async Task Pull_TooBigFile_SkippedWithSizeAndNothingWritten()
    {
        File.WriteAllBytes(Path.Combine(_clientDir, "huge.dmp"), Bytes(50_000));
        await using var agent = await ConnectAgentAsync("160706");

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160706/pull",
            new PullCommandRequest(Path.Combine(_clientDir, "huge.dmp"), MaxBytes: 10_000));
        var body = await resp.Content.ReadFromJsonAsync<PullResponse>();

        var file = Assert.Single(body!.Files);
        Assert.True(file.Skipped);
        Assert.Equal(50_000, file.Size);
        Assert.Contains("больше лимита", file.SkipReason);
        Assert.Null(file.SavedPath);
    }

    [Fact]
    public async Task Pull_OfflineSz_Returns404()
    {
        var resp = await Cli().PostAsJsonAsync("/api/sessions/999999/pull",
            new PullCommandRequest(@"C:\нет\файла.dmp"));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Pull_MissingPath_ReportsErrorInsteadOfHanging()
    {
        await using var agent = await ConnectAgentAsync("160707");

        var resp = await Cli().PostAsJsonAsync("/api/sessions/160707/pull",
            new PullCommandRequest(Path.Combine(_clientDir, "нет-такого-файла.dmp")));
        var body = await resp.Content.ReadFromJsonAsync<PullResponse>();

        Assert.NotNull(body!.Error);
        Assert.Empty(body.Files);
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { _kbRoot, _pullRoot, _clientDir })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }
}
