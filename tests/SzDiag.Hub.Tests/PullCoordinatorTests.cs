using System.Security.Cryptography;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class PullCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szpulled-{Guid.NewGuid():N}");

    private sealed class SpySender : IAgentCommandSender
    {
        public List<PullRequest> Sent { get; } = new();
        public Func<PullRequest, Task>? OnSent { get; set; }

        public Task SendRevertAsync(string c, string sz, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunTestsAsync(string c, string sz, string? f, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendRunDiagAsync(string c, string sz, string? s, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecAsync(string c, ExecRequest r, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendExecStatusAsync(string c, ExecStatusRequest request, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPushAsync(string c, PushRequest request, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SendPullAsync(string c, PullRequest request, CancellationToken ct = default)
        {
            Sent.Add(request);
            // Ответ агента имитируем асинхронно: PullAsync ждёт итог, и отвечать синхронно
            // изнутри отправки — значит проверять не тот порядок, что в жизни.
            if (OnSent is not null) _ = Task.Run(() => OnSent(request));
            return Task.CompletedTask;
        }
    }

    private static SessionRegistry RegistryWith(string sz)
    {
        var reg = new SessionRegistry();
        reg.Register(sz, "10.0.0.42", "PC-1", "conn-1");
        return reg;
    }

    private static byte[] Bytes(int size)
    {
        var data = new byte[size];
        new Random(7).NextBytes(data);
        return data;
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    [Fact]
    public async Task Pull_AssemblesChunksIntoFileOnHost()
    {
        var content = Bytes(3000);
        var sender = new SpySender();
        var coordinator = new PullCoordinator(RegistryWith("160705"), sender, _root, timeoutSeconds: 10);
        sender.OnSent = req =>
        {
            // Два куска, как это делает агент.
            coordinator.AcceptChunk(new PullChunk(req.RequestId, @"C:\dumps\a.dmp", 0, content[..2000], false));
            coordinator.AcceptChunk(new PullChunk(req.RequestId, @"C:\dumps\a.dmp", 1, content[2000..], true));
            coordinator.Complete(new PullResult(req.RequestId, new[]
            {
                new PullFileInfo("a.dmp", @"C:\dumps\a.dmp", content.Length, Sha(content))
            }));
            return Task.CompletedTask;
        };

        var response = await coordinator.PullAsync("160705", @"C:\dumps\*.dmp");

        var file = Assert.Single(response!.Files);
        Assert.False(file.Skipped);
        Assert.NotNull(file.SavedPath);
        Assert.Equal(content, File.ReadAllBytes(file.SavedPath!));
        Assert.Equal(Sha(content), file.Sha256);
    }

    [Fact]
    public async Task Pull_ShaMismatch_MarkedAsBrokenNotSilentlyAccepted()
    {
        var content = Bytes(500);
        var sender = new SpySender();
        var coordinator = new PullCoordinator(RegistryWith("160705"), sender, _root, timeoutSeconds: 10);
        sender.OnSent = req =>
        {
            coordinator.AcceptChunk(new PullChunk(req.RequestId, @"C:\x\b.dmp", 0, content, true));
            // Агент «обещал» другой хеш — значит по дороге что-то побилось.
            coordinator.Complete(new PullResult(req.RequestId, new[]
            {
                new PullFileInfo("b.dmp", @"C:\x\b.dmp", content.Length, new string('0', 64))
            }));
            return Task.CompletedTask;
        };

        var response = await coordinator.PullAsync("160705", @"C:\x\b.dmp");

        var file = Assert.Single(response!.Files);
        Assert.True(file.Skipped);
        Assert.Contains("побился", file.SkipReason);
    }

    [Fact]
    public async Task Pull_SkippedByAgent_PassedThroughWithReason()
    {
        var sender = new SpySender();
        var coordinator = new PullCoordinator(RegistryWith("160705"), sender, _root, timeoutSeconds: 10);
        sender.OnSent = req =>
        {
            coordinator.Complete(new PullResult(req.RequestId, new[]
            {
                new PullFileInfo("huge.dmp", @"C:\x\huge.dmp", 8_900_000_000, "", true,
                    "больше лимита (8 490.4 МБ > 256.0 МБ)")
            }));
            return Task.CompletedTask;
        };

        var response = await coordinator.PullAsync("160705", @"C:\x\huge.dmp");

        var file = Assert.Single(response!.Files);
        Assert.True(file.Skipped);
        Assert.Null(file.SavedPath);
        Assert.Contains("больше лимита", file.SkipReason);
    }

    [Fact]
    public async Task Pull_SameNameFromDifferentFolders_DoesNotOverwrite()
    {
        var first = Bytes(100);
        var second = Bytes(120);
        var sender = new SpySender();
        var coordinator = new PullCoordinator(RegistryWith("160705"), sender, _root, timeoutSeconds: 10);
        sender.OnSent = req =>
        {
            coordinator.AcceptChunk(new PullChunk(req.RequestId, @"C:\one\log.txt", 0, first, true));
            coordinator.AcceptChunk(new PullChunk(req.RequestId, @"C:\two\log.txt", 0, second, true));
            coordinator.Complete(new PullResult(req.RequestId, new[]
            {
                new PullFileInfo("log.txt", @"C:\one\log.txt", first.Length, Sha(first)),
                new PullFileInfo("log.txt", @"C:\two\log.txt", second.Length, Sha(second)),
            }));
            return Task.CompletedTask;
        };

        var response = await coordinator.PullAsync("160705", @"C:\log.txt");

        Assert.Equal(2, response!.Files.Count);
        Assert.All(response.Files, f => Assert.False(f.Skipped));
        Assert.NotEqual(response.Files[0].SavedPath, response.Files[1].SavedPath);
    }

    [Fact]
    public async Task Pull_OfflineSz_ReturnsNull()
    {
        var coordinator = new PullCoordinator(new SessionRegistry(), new SpySender(), _root, timeoutSeconds: 5);

        Assert.Null(await coordinator.PullAsync("999999", @"C:\x.dmp"));
    }

    [Fact]
    public async Task Pull_AgentSilent_ThrowsTimeoutAndLeavesNoPending()
    {
        var coordinator = new PullCoordinator(RegistryWith("160705"), new SpySender(), _root, timeoutSeconds: 1);

        await Assert.ThrowsAsync<TimeoutException>(() => coordinator.PullAsync("160705", @"C:\x.dmp"));
        Assert.Equal(0, coordinator.PendingCount);
    }

    [Fact]
    public void AcceptChunk_UnknownRequestId_Ignored()
    {
        // Иначе на хосте копились бы файлы-сироты от истёкших запросов.
        var coordinator = new PullCoordinator(RegistryWith("160705"), new SpySender(), _root);

        Assert.False(coordinator.AcceptChunk(new PullChunk("нет-такого", @"C:\x.dmp", 0, new byte[] { 1 }, true)));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
