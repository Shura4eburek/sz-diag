using System.Collections.Concurrent;
using System.Text;
using SzDiag.Agent.Zt;

namespace SzDiag.Agent.Tests;

/// <summary>T042: свой Stream поверх zts-сокета. Регрессии, ради которых он существует:
/// точная длина (а не весь буфер), частичная запись, full-duplex и один поток чтения.</summary>
public class ZtStreamTests
{
    /// <summary>Сокет-фейк: Send принимает не больше <see cref="MaxSend"/> байт за раз, Receive
    /// блокируется до появления данных — как настоящий recv.</summary>
    private sealed class FakeIo : IZtSocketIo
    {
        public readonly List<byte> Sent = [];
        public readonly List<int> SendSizes = [];
        public int MaxSend = int.MaxValue;
        public int ReceiveCalls;
        private readonly BlockingCollection<byte[]> _incoming = new();
        public bool ShutdownCalled;

        public void Push(byte[] data) => _incoming.Add(data);
        public void Close() => _incoming.CompleteAdding();

        public int Send(ReadOnlySpan<byte> data)
        {
            var n = Math.Min(data.Length, MaxSend);
            lock (Sent) { Sent.AddRange(data[..n].ToArray()); SendSizes.Add(n); }
            return n;
        }

        private byte[]? _rest;
        public int Receive(Span<byte> buffer)
        {
            Interlocked.Increment(ref ReceiveCalls);
            var chunk = _rest;
            _rest = null;
            if (chunk is null && !_incoming.TryTake(out chunk, Timeout.Infinite)) return 0;
            var n = Math.Min(chunk!.Length, buffer.Length);
            chunk.AsSpan(0, n).CopyTo(buffer);
            if (n < chunk.Length) _rest = chunk[n..];
            return n;
        }

        public void Shutdown() { ShutdownCalled = true; _incoming.CompleteAdding(); }
        public void Dispose() { }
    }

    [Fact]
    public async Task Write_with_offset_sends_exactly_the_slice_and_loops_on_partial_send()
    {
        var io = new FakeIo { MaxSend = 3 };
        using var s = new ZtStream(io);
        var buf = Encoding.ASCII.GetBytes("XXhello-worldYYYY");

        await s.WriteAsync(buf, 2, 11);

        Assert.Equal("hello-world", Encoding.ASCII.GetString(io.Sent.ToArray()));
        Assert.All(io.SendSizes, n => Assert.True(n <= 3));
    }

    [Fact]
    public async Task Read_into_offset_does_not_touch_bytes_outside_the_slice()
    {
        var io = new FakeIo();
        using var s = new ZtStream(io);
        io.Push(Encoding.ASCII.GetBytes("abcdef"));
        var buf = Enumerable.Repeat((byte)'.', 10).ToArray();

        var n = await s.ReadAsync(buf, 3, 4);

        Assert.Equal(4, n);
        Assert.Equal("...abcd...", Encoding.ASCII.GetString(buf));
        var rest = new byte[10];
        Assert.Equal(2, await s.ReadAsync(rest));
        Assert.Equal("ef", Encoding.ASCII.GetString(rest, 0, 2));
    }

    [Fact]
    public async Task Write_completes_while_a_read_is_pending_full_duplex()
    {
        var io = new FakeIo();
        using var s = new ZtStream(io);
        var pendingRead = s.ReadAsync(new byte[16]).AsTask();

        var write = s.WriteAsync(Encoding.ASCII.GetBytes("ping")).AsTask();
        Assert.Same(write, await Task.WhenAny(write, Task.Delay(5000)));
        Assert.False(pendingRead.IsCompleted);

        io.Push(Encoding.ASCII.GetBytes("pong"));
        Assert.Equal(4, await pendingRead.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Peer_close_reads_as_end_of_stream()
    {
        var io = new FakeIo();
        using var s = new ZtStream(io);
        io.Push([1, 2]);
        io.Close();

        var buf = new byte[8];
        Assert.Equal(2, await s.ReadAsync(buf));
        Assert.Equal(0, await s.ReadAsync(buf));
    }

    [Fact]
    public async Task Many_reads_use_one_receive_loop_not_a_thread_per_read()
    {
        var io = new FakeIo();
        using var s = new ZtStream(io);
        var threadsBefore = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        for (var i = 0; i < 200; i++)
        {
            io.Push([(byte)i]);
            var b = new byte[1];
            Assert.Equal(1, await s.ReadAsync(b));
        }
        var threadsAfter = System.Diagnostics.Process.GetCurrentProcess().Threads.Count;
        Assert.True(threadsAfter - threadsBefore < 20, $"потоков стало больше на {threadsAfter - threadsBefore}");
    }

    [Fact]
    public void Dispose_wakes_blocked_receive()
    {
        var io = new FakeIo();
        var s = new ZtStream(io);
        s.Dispose();
        Assert.True(io.ShutdownCalled);
    }

    [Fact]
    public async Task Send_failure_surfaces_as_IOException()
    {
        var io = new FakeIo { MaxSend = 0 };
        using var s = new ZtStream(io);
        await Assert.ThrowsAsync<IOException>(() => s.WriteAsync(new byte[] { 1 }).AsTask());
    }
}
