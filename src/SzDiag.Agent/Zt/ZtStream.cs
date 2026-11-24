using System.Buffers;
using System.IO.Pipelines;

namespace SzDiag.Agent.Zt;

/// <summary>Блокирующий ввод-вывод одного zts-сокета. Реальная реализация — P/Invoke в libzt
/// (<see cref="LibztSocketIo"/>), в тестах — фейк: нативный код в `dotnet test` не запускается.</summary>
public interface IZtSocketIo : IDisposable
{
    /// <summary>Сколько байт ушло (может быть меньше длины). ≤0 — ошибка/закрыт.</summary>
    int Send(ReadOnlySpan<byte> data);

    /// <summary>Сколько байт пришло; 0 — собеседник закрыл, &lt;0 — ошибка. Блокирует до данных.</summary>
    int Receive(Span<byte> buffer);

    /// <summary>Разбудить заблокированный <see cref="Receive"/> и закрыть сокет.</summary>
    void Shutdown();
}

/// <summary>Stream поверх zts-сокета для HttpClient/SignalR/проброса SSH (US3, T042).
///
/// Почему не <c>ZeroTier.Sockets.NetworkStream</c>: в 1.8.4 их Send/Receive передают в нативный
/// вызов длину ВСЕГО массива вместо size (мусорный хвост / запись за границу) и берут адрес
/// незакреплённого массива на время блокирующего вызова (research.md R8).
///
/// Чтение — ОДИН выделенный поток на соединение, складывающий данные в <see cref="Pipe"/>:
/// поток на каждый ReadAsync под SignalR = поток на сообщение, а на клиенте под OCCT это прямая
/// дорога к голоданию пула (бэклог п.50). Запись — независимо от чтения (full-duplex для
/// WebSocket): базовый Stream сериализовал бы их одним семафором.</summary>
public sealed class ZtStream : Stream
{
    private readonly IZtSocketIo _io;
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 1 << 20, resumeWriterThreshold: 1 << 19,
        useSynchronizationContext: false));
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Thread _reader;
    private int _disposed;

    public ZtStream(IZtSocketIo io, string? name = null)
    {
        _io = io;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = name ?? "zt-read" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        Exception? error = null;
        try
        {
            while (true)
            {
                var mem = _pipe.Writer.GetMemory(16 * 1024);
                var n = _io.Receive(mem.Span);
                if (n == 0) break;
                if (n < 0) { error = new IOException($"zts recv: {n}"); break; }
                _pipe.Writer.Advance(n);
                var flush = _pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                if (flush.IsCompleted || flush.IsCanceled) break;
            }
        }
        catch (Exception ex) when (Volatile.Read(ref _disposed) == 0) { error = ex; }
        catch { /* закрыли мы сами — тихо */ }
        _pipe.Writer.Complete(error);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.IsEmpty) return 0;
        while (true)
        {
            var r = await _pipe.Reader.ReadAsync(ct).ConfigureAwait(false);
            var seq = r.Buffer;
            if (!seq.IsEmpty)
            {
                var take = (int)Math.Min(seq.Length, buffer.Length);
                seq.Slice(0, take).CopyTo(buffer.Span);
                _pipe.Reader.AdvanceTo(seq.GetPosition(take));
                return take;
            }
            _pipe.Reader.AdvanceTo(seq.Start, seq.End);
            if (r.IsCompleted || r.IsCanceled) return 0;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Блокирующий send — в пуле, но без LongRunning: запись короткая и идёт по запросу,
            // а не постоянно висит, как чтение.
            await Task.Run(() => WriteAll(buffer.Span), ct).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    private void WriteAll(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            var n = _io.Send(data);
            if (n <= 0) throw new IOException($"zts send: {n}");
            data = data[n..];
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _writeGate.Wait();
        try { WriteAll(buffer.AsSpan(offset, count)); }
        finally { _writeGate.Release(); }
    }

    public override Task<int> ReadAsync(byte[] b, int o, int c, CancellationToken ct) => ReadAsync(b.AsMemory(o, c), ct).AsTask();
    public override Task WriteAsync(byte[] b, int o, int c, CancellationToken ct) => WriteAsync(b.AsMemory(o, c), ct).AsTask();

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _io.Shutdown(); } catch { }
            _pipe.Reader.Complete();
            try { _io.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
