using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ZeroTier.Core;
using ZtSocket = ZeroTier.Sockets.Socket;

namespace SzDiag.Agent.Zt;

/// <summary>Нода ZeroTier внутри процесса агента (US3). Абстракция — ради тестов транспорта и
/// проброса: libzt в `dotnet test` не поднимается.</summary>
public interface IZtNode : IDisposable
{
    string NetworkId { get; }
    string NodeId { get; }
    IPAddress Address { get; }
    string StoragePath { get; }

    /// <summary>Исходящее TCP-соединение через сеть ZT.</summary>
    Task<Stream> ConnectAsync(IPEndPoint remote, CancellationToken ct);

    /// <summary>Входящие TCP-соединения на адрес ноды в сети ZT.</summary>
    IZtListener Listen(int port);

    /// <summary>Стереть storage при остановке ноды: откат «без следов» (identity, конфиг сети).</summary>
    bool WipeOnDispose { get; set; }
}

public interface IZtListener : IDisposable
{
    /// <summary>Блокирует до входящего соединения; после Dispose — исключение.</summary>
    Stream Accept();
}

/// <summary>Реальная нода на libzt. Одна на процесс (libzt — синглтон): повторный
/// <see cref="Start"/> при живой ноде — ошибка вызывающего.</summary>
public sealed class LibztNode : IZtNode
{
    private static int _alive;
    private readonly Node _node;
    private int _disposed;

    public string NetworkId { get; }
    public string NodeId { get; }
    public IPAddress Address { get; }
    public string StoragePath { get; }
    public bool WipeOnDispose { get; set; }

    private LibztNode(Node node, string networkId, string nodeId, IPAddress address, string storage)
    {
        _node = node;
        NetworkId = networkId;
        NodeId = nodeId;
        Address = address;
        StoragePath = storage;
    }

    /// <summary>Поднять ноду, войти в сеть и дождаться адреса. null — не вышло за
    /// <paramref name="timeout"/> (причина — в <paramref name="log"/>); нода при этом остановлена,
    /// storage стёрт, если его создали здесь.</summary>
    public static LibztNode? Start(string storage, string networkId, TimeSpan timeout, Action<string>? log = null)
    {
        if (!ulong.TryParse(networkId, System.Globalization.NumberStyles.HexNumber, null, out var nwid))
        {
            log?.Invoke($"zt: id сети «{networkId}» не hex");
            return null;
        }
        if (Interlocked.Exchange(ref _alive, 1) != 0)
            throw new InvalidOperationException("нода ZeroTier в этом процессе уже поднята");

        var createdStorage = !Directory.Exists(storage);
        Node? node = null;
        try
        {
            Directory.CreateDirectory(storage);
            var sw = Stopwatch.StartNew();
            node = new Node();
            node.InitFromStorage(storage);
            // Не 9993: на машине может стоять ZeroTier One со своей сетью.
            node.InitSetPort((ushort)Random.Shared.Next(20000, 40000));
            node.InitSetEventHandler(_ => { });
            node.Start();
            while (!node.Online && sw.Elapsed < timeout) Thread.Sleep(100);
            if (!node.Online) { log?.Invoke($"zt: нода не вышла в online за {timeout.TotalSeconds:F0} с"); return Fail(); }

            node.Join(nwid);
            while (!node.IsNetworkTransportReady(nwid) && sw.Elapsed < timeout) Thread.Sleep(100);
            if (!node.IsNetworkTransportReady(nwid)) { log?.Invoke($"zt: адрес в сети {networkId} не получен за {timeout.TotalSeconds:F0} с"); return Fail(); }

            var ip = node.GetNetworkAddresses(nwid).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (ip is null) { log?.Invoke("zt: в сети нет IPv4-адреса"); return Fail(); }
            log?.Invoke($"zt: в сети {networkId} как {ip} (узел {node.IdString}) за {sw.Elapsed.TotalSeconds:F1} с");
            return new LibztNode(node, networkId.ToLowerInvariant(), node.IdString, ip, storage);
        }
        catch (Exception ex)
        {
            log?.Invoke($"zt: {ex.GetType().Name}: {ex.Message}");
            return Fail();
        }

        LibztNode? Fail()
        {
            try { node?.Stop(); } catch { }
            Interlocked.Exchange(ref _alive, 0);
            if (createdStorage) ZtStorage.Wipe(storage);
            return null;
        }
    }

    public async Task<Stream> ConnectAsync(IPEndPoint remote, CancellationToken ct)
    {
        var s = new ZtSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await Task.Run(() => s.Connect(remote), ct).ConfigureAwait(false);
            return new ZtStream(new LibztSocketIo(s), $"zt-read {remote}");
        }
        catch
        {
            try { s.Close(); } catch { }
            throw;
        }
    }

    public IZtListener Listen(int port)
    {
        var s = new ZtSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        s.Bind(new IPEndPoint(IPAddress.Any, port));
        s.Listen(16);
        return new Listener(s);
    }

    private sealed class Listener(ZtSocket socket) : IZtListener
    {
        public Stream Accept() => new ZtStream(new LibztSocketIo(socket.Accept()), "zt-read accepted");
        public void Dispose() { try { socket.Close(); } catch { } }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _node.Stop(); } catch { }
        Interlocked.Exchange(ref _alive, 0);
        if (WipeOnDispose) ZtStorage.Wipe(StoragePath);
    }
}

/// <summary>Где живёт storage ноды и как его стирать (откат без следов).</summary>
public static class ZtStorage
{
    /// <summary>Корень: <c>C:\ProgramData\szdiag\zt</c>. Папка на СЗ — для основного агента и
    /// <c>--resume</c> (одна identity на сессию); headless-откат берёт одноразовую.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "szdiag", "zt");

    public static string ForSession(string sz) => Path.Combine(Root, sz);

    public static string Throwaway() => Path.Combine(Root, "revert-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Стереть storage и, если корень опустел, — сам корень (и пустой <c>szdiag</c>).
    /// Ошибки глотаются: откат продолжается, остаток покажет client info.</summary>
    public static bool Wipe(string? storage)
    {
        if (string.IsNullOrWhiteSpace(storage)) return true;
        var ok = true;
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(storage)) Directory.Delete(storage, recursive: true);
                ok = true;
                break;
            }
            catch { ok = false; Thread.Sleep(200); }
        }
        TryRemoveIfEmpty(Path.GetDirectoryName(storage));
        TryRemoveIfEmpty(Path.GetDirectoryName(Path.GetDirectoryName(storage)));
        return ok;
    }

    private static void TryRemoveIfEmpty(string? dir)
    {
        try
        {
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch { }
    }
}
