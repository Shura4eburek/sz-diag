using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using SzDiag.Agent.Zt;

namespace SzDiag.Agent.Tests;

/// <summary>T044/T045: нода поднимается лениво пробой zt-адреса, неудача гасит её и не оставляет
/// storage; остатки прошлых сессий (крах до Open) снимаются при старте.</summary>
public class ZtHubSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "szzts-" + Guid.NewGuid().ToString("N"), "zt");

    public void Dispose()
    {
        var top = Path.GetDirectoryName(_root)!;
        if (Directory.Exists(top)) Directory.Delete(top, true);
    }

    private sealed class FakeNode(string storage, string network) : IZtNode
    {
        public bool Disposed;
        public string NetworkId => network;
        public string NodeId => "0123456789";
        public IPAddress Address => IPAddress.Parse("10.88.0.42");
        public string StoragePath => storage;
        public bool WipeOnDispose { get; set; }

        public async Task<Stream> ConnectAsync(IPEndPoint remote, CancellationToken ct)
        {
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, remote.Port, ct);
            return c.GetStream();
        }

        public IZtListener Listen(int port) => throw new NotSupportedException();

        public void Dispose()
        {
            Disposed = true;
            if (WipeOnDispose) ZtStorage.Wipe(storage);
        }
    }

    private (ZtHubSession Session, List<FakeNode> Started) Session(string storage, bool alwaysWipe = false)
    {
        var started = new List<FakeNode>();
        var s = new ZtHubSession(storage, (st, net, _, _) =>
        {
            Directory.CreateDirectory(st);
            File.WriteAllText(Path.Combine(st, "identity.secret"), "x");
            var n = new FakeNode(st, net);
            started.Add(n);
            return n;
        }, alwaysWipe: alwaysWipe, probeBudget: TimeSpan.FromSeconds(2));
        return (s, started);
    }

    private static async Task<(WebApplication App, int Port)> Hub()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        app.MapGet("/healthz", () => "ok");
        await app.StartAsync();
        return (app, new Uri(app.Urls.First()).Port);
    }

    [Fact]
    public async Task Probe_starts_node_once_and_keeps_it_as_transport_when_hub_answers()
    {
        var (app, port) = await Hub();
        await using var _ = app;
        var (s, started) = Session(Path.Combine(_root, "123456"));

        Assert.True(await s.ProbeAsync("aa4bb3b8ba8f7d86", $"http://10.88.0.1:{port}", default));
        Assert.True(await s.ProbeAsync("aa4bb3b8ba8f7d86", $"http://10.88.0.1:{port}", default));

        Assert.Single(started);
        Assert.NotNull(s.Transport);
        Assert.False(started[0].Disposed);
    }

    [Fact]
    public async Task Failed_probe_stops_node_and_wipes_storage_it_created()
    {
        var storage = Path.Combine(_root, "123456");
        var (s, started) = Session(storage);
        var deadPort = FreePort();

        Assert.False(await s.ProbeAsync("aa4bb3b8ba8f7d86", $"http://10.88.0.1:{deadPort}",
            new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token));

        Assert.True(started[0].Disposed);
        Assert.Null(s.Node);
        Assert.False(Directory.Exists(storage));
    }

    [Fact]
    public async Task Failed_probe_keeps_preexisting_session_storage_for_resume()
    {
        var storage = Path.Combine(_root, "123456");
        Directory.CreateDirectory(storage); // осталось от сессии до ребута
        var (s, _) = Session(storage);

        Assert.False(await s.ProbeAsync("aa4bb3b8ba8f7d86", $"http://10.88.0.1:{FreePort()}",
            new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token));
        Assert.True(Directory.Exists(storage));
    }

    [Fact]
    public async Task Revert_session_always_wipes_its_throwaway_storage_on_dispose()
    {
        var (app, port) = await Hub();
        await using var _ = app;
        var storage = Path.Combine(_root, "revert-abc");
        var (s, _) = Session(storage, alwaysWipe: true);
        Assert.True(await s.ProbeAsync("aa4bb3b8ba8f7d86", $"http://10.88.0.1:{port}", default));

        s.Dispose();
        Assert.False(Directory.Exists(storage));
    }

    [Fact]
    public async Task WipeOnStop_marks_running_node_and_dispose_wipes()
    {
        var (app, port) = await Hub();
        await using var _ = app;
        var storage = Path.Combine(_root, "123456");
        var (s, started) = Session(storage);
        Assert.True(await s.ProbeAsync("aa4bb3b8ba8f7d86", $"http://10.88.0.1:{port}", default));

        s.WipeOnStop();
        Assert.True(Directory.Exists(storage)); // нода ещё работает — канал отчёта об откате жив
        s.Dispose();
        Assert.False(Directory.Exists(storage));
    }

    [Fact]
    public void Leftovers_of_other_sessions_are_wiped_but_own_and_fresh_revert_kept()
    {
        foreach (var d in new[] { "111111", "222222", "revert-fresh" })
            Directory.CreateDirectory(Path.Combine(_root, d));
        var old = Path.Combine(_root, "revert-old");
        Directory.CreateDirectory(old);
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-1));

        var wiped = ZtStorage.WipeLeftovers("222222", _root);

        Assert.Equal(["111111", "revert-old"], wiped.Order());
        Assert.True(Directory.Exists(Path.Combine(_root, "222222")));
        Assert.True(Directory.Exists(Path.Combine(_root, "revert-fresh")));
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
