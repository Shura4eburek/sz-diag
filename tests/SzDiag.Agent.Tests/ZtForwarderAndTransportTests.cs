using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SzDiag.Agent.Zt;

namespace SzDiag.Agent.Tests;

/// <summary>T043/T044 без libzt: «сеть ZT» подменена loopback-TCP. Проверяется логика проброса
/// и того, что HttpClient/SignalR действительно идут через ConnectCallback ноды.</summary>
public class ZtForwarderAndTransportTests
{
    /// <summary>Нода-фейк: любое ConnectAsync уходит на 127.0.0.1:<c>port</c> того же номера;
    /// Listen — обычный TcpListener на loopback.</summary>
    private sealed class LoopbackNode : IZtNode
    {
        public int Connects;
        public string NetworkId => "aa4bb3b8ba8f7d86";
        public string NodeId => "0123456789";
        public IPAddress Address => IPAddress.Parse("10.88.0.42");
        public string StoragePath => "";
        public bool WipeOnDispose { get; set; }

        public async Task<Stream> ConnectAsync(IPEndPoint remote, CancellationToken ct)
        {
            Interlocked.Increment(ref Connects);
            var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, remote.Port, ct);
            return c.GetStream();
        }

        public IZtListener Listen(int port) => new TcpZtListener(port);
        public void Dispose() { }
    }

    private sealed class TcpZtListener : IZtListener
    {
        public readonly TcpListener L;
        public TcpZtListener(int port) { L = new TcpListener(IPAddress.Loopback, port); L.Start(); }
        public int Port => ((IPEndPoint)L.LocalEndpoint).Port;
        public Stream Accept() => L.AcceptTcpClient().GetStream();
        public void Dispose() => L.Stop();
    }

    [Fact]
    public async Task Forwarder_pipes_both_ways_to_local_port_and_stops_cleanly()
    {
        // «sshd»: эхо-сервер на loopback.
        var echo = new TcpListener(IPAddress.Loopback, 0);
        echo.Start();
        var echoPort = ((IPEndPoint)echo.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            using var c = await echo.AcceptTcpClientAsync();
            var s = c.GetStream();
            var buf = new byte[64];
            int n;
            while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
        });

        var zt = new TcpZtListener(0);
        using var fwd = new ZtSshForwarder(zt, echoPort);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, zt.Port);
        var cs = client.GetStream();
        await cs.WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-test"));
        var got = new byte[12];
        var read = 0;
        while (read < 12) read += await cs.ReadAsync(got.AsMemory(read)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("SSH-2.0-test", Encoding.ASCII.GetString(got));

        fwd.Dispose();
        var after = new byte[1];
        var n = await cs.ReadAsync(after).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, n);
        echo.Stop();
    }

    public sealed class EchoHub : Hub
    {
        public string Echo(string s) => s;
        public string Header() => Context.GetHttpContext()?.Request.Headers["X-SzDiag-Token"].ToString() ?? "";
    }

    [Theory]
    [InlineData(HttpTransportType.WebSockets)]
    [InlineData(HttpTransportType.ServerSentEvents)]
    public async Task HttpClient_and_SignalR_go_through_node_connect(HttpTransportType transport)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        await using var app = builder.Build();
        app.MapGet("/healthz", () => "ok");
        app.MapHub<EchoHub>("/agents");
        await app.StartAsync();
        var port = new Uri(app.Urls.First()).Port;

        var node = new LoopbackNode();
        var t = new ZtHubTransport(node);
        var hubUrl = $"http://10.88.0.1:{port}";

        using (var http = t.CreateHttpClient(hubUrl))
            Assert.Equal("ok", await http.GetStringAsync("/healthz"));
        Assert.True(node.Connects >= 1);

        var before = node.Connects;
        await using var conn = new HubConnectionBuilder()
            .WithUrl($"{hubUrl}/agents", o =>
            {
                o.Transports = transport;
                o.Headers["X-SzDiag-Token"] = "agent-token";
                t.Configure(o);
            })
            .Build();
        await conn.StartAsync();
        Assert.Equal("привет", await conn.InvokeAsync<string>("Echo", "привет"));
        Assert.Equal("agent-token", await conn.InvokeAsync<string>("Header"));
        Assert.True(node.Connects > before, "SignalR обошёл ноду");
    }

    [Fact]
    public async Task Hostname_instead_of_ip_is_rejected_not_resolved()
    {
        var t = new ZtHubTransport(new LoopbackNode());
        using var http = t.CreateHttpClient("http://hub.example:5099");
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/healthz"));
    }

    [Fact]
    public void Storage_wipe_removes_session_folder_and_empty_parents()
    {
        var root = Path.Combine(Path.GetTempPath(), "szzt-" + Guid.NewGuid().ToString("N"), "zt");
        var storage = Path.Combine(root, "123456");
        Directory.CreateDirectory(Path.Combine(storage, "networks.d"));
        File.WriteAllText(Path.Combine(storage, "identity.secret"), "x");

        Assert.True(ZtStorage.Wipe(storage));
        Assert.False(Directory.Exists(storage));
        Assert.False(Directory.Exists(root));
        Assert.False(Directory.Exists(Path.GetDirectoryName(root)));
    }

    [Fact]
    public void Storage_wipe_keeps_parent_with_other_sessions()
    {
        var root = Path.Combine(Path.GetTempPath(), "szzt-" + Guid.NewGuid().ToString("N"), "zt");
        Directory.CreateDirectory(Path.Combine(root, "111111"));
        Directory.CreateDirectory(Path.Combine(root, "222222"));
        try
        {
            Assert.True(ZtStorage.Wipe(Path.Combine(root, "111111")));
            Assert.True(Directory.Exists(Path.Combine(root, "222222")));
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, true); }
    }
}
