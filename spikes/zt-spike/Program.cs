// Спайк T035 (specs/002-domain-independent-access): libzt под net8.0.
//   zt-spike probe  <storage> [nwid]                — п.1/5/6: загрузка, online, адрес, файлы storage
//   zt-spike serve  <storage> <nwid> <ztPort>       — п.4: accept на ZT → прокси на локальный Kestrel
//   zt-spike client <storage> <nwid> <ip> <ztPort>  — п.2/3: HttpClient и SignalR поверх ZT
// libzt — синглтон на процесс, поэтому две ноды = два процесса.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using ZeroTier.Core;
using ZtSocket = ZeroTier.Sockets.Socket;

var mode = args.ElementAtOrDefault(0) ?? "probe";
var storage = args.ElementAtOrDefault(1) ?? Path.Combine(Path.GetTempPath(), "zt-spike-" + Guid.NewGuid().ToString("N")[..8]);
var nwid = args.Length > 2 ? Convert.ToUInt64(args[2], 16) : 0UL;

var node = StartNode(storage, nwid);
try
{
    switch (mode)
    {
        case "probe": break;
        case "serve": await Serve(int.Parse(args[3])); break;
        case "client": await Client(IPAddress.Parse(args[3]), int.Parse(args[4])); break;
        default: Console.WriteLine("режим: probe|serve|client"); break;
    }
}
finally
{
    node.Stop();
    Thread.Sleep(500);
    Console.WriteLine("файлы storage:");
    foreach (var f in Directory.EnumerateFileSystemEntries(storage, "*", SearchOption.AllDirectories))
        Console.WriteLine($"  {Path.GetRelativePath(storage, f)}  {(File.Exists(f) ? new FileInfo(f).Length : 0)} b");
}

static Node StartNode(string storage, ulong nwid)
{
    Directory.CreateDirectory(storage);
    Console.WriteLine($"storage: {storage}");
    var sw = Stopwatch.StartNew();
    var node = new Node();
    node.InitFromStorage(storage);
    node.InitSetPort((ushort)Random.Shared.Next(20000, 40000)); // не 9993 — там ZeroTier One владельца
    node.InitSetEventHandler(e =>
    {
        if (e.Code is not (240 or 241 or 243)) Console.WriteLine($"[{sw.Elapsed.TotalSeconds,6:F1}s] event {e.Code} {e.Name}");
    });
    node.Start();
    while (!node.Online && sw.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(50);
    Console.WriteLine(node.Online ? $"online за {sw.Elapsed.TotalSeconds:F1} с, nodeId {node.IdString}" : "НЕ online за 60 с");
    if (nwid == 0 || !node.Online) return node;

    node.Join(nwid);
    var t = Stopwatch.StartNew();
    while (!node.IsNetworkTransportReady(nwid) && t.Elapsed < TimeSpan.FromSeconds(60)) Thread.Sleep(100);
    if (!node.IsNetworkTransportReady(nwid)) throw new Exception("адрес не получен за 60 с");
    var ip = node.GetNetworkAddresses(nwid).First(a => a.AddressFamily == AddressFamily.InterNetwork);
    Console.WriteLine($"ZTIP={ip} (адрес за {t.Elapsed.TotalSeconds:F1} с после join)");
    return node;
}

// ---------- serve: Kestrel на 127.0.0.1 + ZT-accept с пробросом (модель ZtSshForwarder) ----------
static async Task Serve(int ztPort)
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 4 * 1024 * 1024);
    var app = builder.Build();
    app.MapGet("/ping", () => "pong");
    app.MapGet("/blob/{mb:int}", async (int mb, HttpContext ctx) =>
    {
        var chunk = new byte[64 * 1024];
        Random.Shared.NextBytes(chunk);
        ctx.Response.ContentLength = (long)mb * 1024 * 1024;
        for (var i = 0; i < mb * 16; i++) await ctx.Response.Body.WriteAsync(chunk);
    });
    app.MapPost("/sha", async (HttpContext ctx) =>
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(ctx.Request.Body);
        return Convert.ToHexString(hash);
    });
    app.MapHub<EchoHub>("/hub");
    await app.StartAsync();
    var local = new Uri(app.Urls.First()).Port;
    Console.WriteLine($"kestrel 127.0.0.1:{local}");

    var listener = new ZtSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    listener.Bind(new IPEndPoint(IPAddress.Any, ztPort));
    listener.Listen(32);
    Console.WriteLine($"ZT listen :{ztPort} → 127.0.0.1:{local}  (Ctrl+C — стоп)");

    var stop = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
    new Thread(() =>
    {
        while (true)
        {
            ZtSocket c;
            try { c = listener.Accept(); } catch (Exception ex) { Console.WriteLine($"accept: {ex.Message}"); return; }
            _ = Task.Run(() => Proxy(c, local));
        }
    }) { IsBackground = true }.Start();

    var lifetime = args0Timeout();
    await Task.WhenAny(stop.Task, Task.Delay(lifetime));
    await app.StopAsync();

    static TimeSpan args0Timeout() => TimeSpan.FromMinutes(10);
}

static async Task Proxy(ZtSocket zt, int localPort)
{
    using var zs = new ZtStream(zt);
    using var tcp = new TcpClient { NoDelay = true };
    try
    {
        await tcp.ConnectAsync(IPAddress.Loopback, localPort);
        var ls = tcp.GetStream();
        var a = zs.CopyToAsync(ls, 64 * 1024);
        var b = ls.CopyToAsync(zs, 64 * 1024);
        await Task.WhenAny(a, b);
    }
    catch (Exception ex) { Console.WriteLine($"proxy: {ex.GetType().Name} {ex.Message}"); }
}

// ---------- client: HttpClient и SignalR через ConnectCallback ----------
static async Task Client(IPAddress server, int port)
{
    var handler = NewHandler();
    var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri($"http://{server}:{port}") };
    await RunTests(http, handler, server, port);
}

static SocketsHttpHandler NewHandler() => new SocketsHttpHandler
    {
        ConnectCallback = async (ctx, ct) =>
        {
            var s = new ZtSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            var ep = new IPEndPoint(IPAddress.Parse(ctx.DnsEndPoint.Host), ctx.DnsEndPoint.Port);
            await Task.Run(() => s.Connect(ep), ct);
            return new ZtStream(s);
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

static async Task RunTests(HttpClient http, SocketsHttpHandler handler, IPAddress server, int port)
{

    var sw = Stopwatch.StartNew();
    Console.WriteLine($"[2] GET /ping → {await http.GetStringAsync("/ping")} за {sw.ElapsedMilliseconds} мс");

    sw.Restart();
    using (var resp = await http.GetAsync("/blob/32", HttpCompletionOption.ResponseHeadersRead))
    {
        var body = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[64 * 1024]; long total = 0; int n;
        while ((n = await body.ReadAsync(buf)) > 0) total += n;
        Console.WriteLine($"[2] скачано {total / 1048576.0:F1} МБ за {sw.Elapsed.TotalSeconds:F1} с = {total / 1048576.0 / sw.Elapsed.TotalSeconds:F1} МБ/с");
    }

    var upload = new byte[8 * 1024 * 1024];
    Random.Shared.NextBytes(upload);
    var expect = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(upload));
    sw.Restart();
    var got = await (await http.PostAsync("/sha", new ByteArrayContent(upload))).Content.ReadAsStringAsync();
    Console.WriteLine($"[2] залито 8 МБ за {sw.Elapsed.TotalSeconds:F1} с, sha256 {(got.Trim('"') == expect ? "совпал" : "НЕ СОВПАЛ: " + got)}");

    foreach (var transport in new[] { HttpTransportType.WebSockets, HttpTransportType.ServerSentEvents })
    {
        var h = NewHandler(); // SignalR освобождает handler из фабрики при Dispose подключения
        var conn = new HubConnectionBuilder()
            .WithUrl($"http://{server}:{port}/hub", o =>
            {
                o.Transports = transport;
                o.HttpMessageHandlerFactory = _ => h;
                o.WebSocketFactory = async (ctx, ct) =>
                {
                    var ws = new ClientWebSocket();
                    await ws.ConnectAsync(ctx.Uri, new HttpMessageInvoker(h, disposeHandler: false), ct);
                    return ws;
                };
            })
            .Build();
        sw.Restart();
        try
        {
            await conn.StartAsync();
            var echo = await conn.InvokeAsync<string>("Echo", "привет");
            var big = new string('x', 512 * 1024);
            var len = await conn.InvokeAsync<int>("Len", big);
            var pushed = new TaskCompletionSource<string>();
            conn.On<string>("Push", s => pushed.TrySetResult(s));
            await conn.InvokeAsync("AskPush");
            var push = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"[3] SignalR {transport}: Echo={echo}, Len(512K)={len}, push={push}, {sw.ElapsedMilliseconds} мс");
        }
        catch (Exception ex) { Console.WriteLine($"[3] SignalR {transport}: ОШИБКА {ex.GetType().Name}: {ex.Message}"); }
        finally { await conn.DisposeAsync(); }
    }
}

sealed class EchoHub : Hub
{
    public string Echo(string s) => s;
    public int Len(string s) => s.Length;
    public Task AskPush() => Clients.Caller.SendAsync("Push", "с сервера");
}
