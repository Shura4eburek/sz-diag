using System.Reflection;
using System.Runtime.InteropServices;
using ZtSocket = ZeroTier.Sockets.Socket;

/// <summary>Stream поверх zts-сокета мимо Send/Receive из ZeroTier.Sockets 1.8.4: те передают
/// в нативный вызов длину ВСЕГО массива вместо size (Send шлёт мусор хвоста, Receive при
/// offset>0 пишет за границу) и берут адрес незакреплённого массива на время блокирующего
/// вызова. Здесь — свой P/Invoke с fixed-закреплением и точной длиной.
/// Чтение и запись независимы (full-duplex для WebSocket): ReadAsync/WriteAsync не идут
/// через базовый BeginRead/BeginWrite, который сериализует их одним семафором.</summary>
sealed unsafe class ZtStream : Stream
{
    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_send")]
    static extern int zts_bsd_send(int fd, byte* buf, uint len, int flags);

    [DllImport("libzt", EntryPoint = "CSharp_zts_bsd_recv")]
    static extern int zts_bsd_recv(int fd, byte* buf, uint len, int flags);

    static readonly FieldInfo FdField =
        typeof(ZtSocket).GetField("_fd", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException("ZeroTier.Sockets.Socket._fd");

    readonly ZtSocket _socket;
    readonly int _fd;
    int _disposed;

    public ZtStream(ZtSocket socket)
    {
        _socket = socket;
        _fd = (int)FdField.GetValue(socket)!;
        socket.NoDelay = true;
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty) return 0;
        int n;
        fixed (byte* p = buffer) n = zts_bsd_recv(_fd, p, (uint)buffer.Length, 0);
        if (n < 0) throw new IOException($"zts recv: {n}");
        return n;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            int n;
            fixed (byte* p = buffer) n = zts_bsd_send(_fd, p, (uint)buffer.Length, 0);
            if (n <= 0) throw new IOException($"zts send: {n}");
            buffer = buffer[n..];
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        new(Task.Factory.StartNew(() => Read(buffer.Span), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        new(Task.Run(() => Write(buffer.Span), ct));

    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => WriteAsync(b.AsMemory(o, c), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _socket.Shutdown(System.Net.Sockets.SocketShutdown.Both); } catch { }
            try { _socket.Close(); } catch { }
        }
        base.Dispose(disposing);
    }
}
