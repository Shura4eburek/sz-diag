using System.Reflection;
using System.Runtime.InteropServices;
using ZtSocket = ZeroTier.Sockets.Socket;

namespace SzDiag.Agent.Zt;

/// <summary>Ввод-вывод zts-сокета мимо Send/Receive из ZeroTier.Sockets 1.8.4 (баги длины и
/// закрепления, research.md R8): свой P/Invoke в libzt с fixed-закреплением и точной длиной.
/// Connect/Bind/Listen/Accept у их Socket корректны — ими и пользуемся, дескриптор берём из
/// приватного <c>_fd</c>.</summary>
public sealed unsafe class LibztSocketIo : IZtSocketIo
{
    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_send")]
    private static extern int zts_bsd_send(int fd, byte* buf, uint len, int flags);

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_recv")]
    private static extern int zts_bsd_recv(int fd, byte* buf, uint len, int flags);

    private static readonly FieldInfo FdField =
        typeof(ZtSocket).GetField("_fd", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("ZeroTier.Sockets.Socket._fd — сменилась версия пакета?");

    private readonly ZtSocket _socket;
    private readonly int _fd;
    private int _closed;

    public LibztSocketIo(ZtSocket socket)
    {
        _socket = socket;
        _fd = (int)FdField.GetValue(socket)!;
        try { socket.NoDelay = true; } catch { }
    }

    public int Send(ReadOnlySpan<byte> data)
    {
        fixed (byte* p = data) return zts_bsd_send(_fd, p, (uint)data.Length, 0);
    }

    public int Receive(Span<byte> buffer)
    {
        fixed (byte* p = buffer) return zts_bsd_recv(_fd, p, (uint)buffer.Length, 0);
    }

    public void Shutdown()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { _socket.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _socket.Close(); } catch { }
    }
}
