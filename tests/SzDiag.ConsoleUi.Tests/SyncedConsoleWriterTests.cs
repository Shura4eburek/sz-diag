using System.Text;
using SzDiag.ConsoleUi;

namespace SzDiag.ConsoleUi.Tests;

public class SyncedConsoleWriterTests
{
    /// <summary>Writer, который специально «зевает» между символами: без лока
    /// параллельные записи перемешаются, с локом — нет.</summary>
    private sealed class SlowWriter : TextWriter
    {
        private readonly StringBuilder _sb = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value)
        {
            _sb.Append(value);
            Thread.Sleep(1);
        }
        public override string ToString() => _sb.ToString();
    }

    [Fact]
    public void ConcurrentWrites_AreNotInterleaved()
    {
        var inner = new SlowWriter();
        var gate = new object();
        var writer = new SyncedConsoleWriter(inner, gate);

        Parallel.For(0, 8, i => writer.Write(i % 2 == 0 ? "AAAA" : "BBBB"));

        var text = inner.ToString();
        Assert.Equal(32, text.Length);
        // Каждая четвёрка символов должна быть однородной — иначе записи перемешались.
        for (var i = 0; i < text.Length; i += 4)
            Assert.Equal(1, text.Substring(i, 4).Distinct().Count());
    }

    [Fact]
    public void WriteLine_GoesThroughToInner()
    {
        var inner = new StringWriter();
        var writer = new SyncedConsoleWriter(inner, new object());
        writer.WriteLine("привет");
        Assert.Contains("привет", inner.ToString());
    }

    [Fact]
    public void RunLocked_UsesSameGate_BlocksWriters()
    {
        var inner = new SlowWriter();
        var gate = new object();
        var writer = new SyncedConsoleWriter(inner, gate);
        var insideLock = false;
        var sawWriteDuringLock = false;

        var t = Task.Run(() =>
        {
            lock (gate)
            {
                insideLock = true;
                Thread.Sleep(50);
                if (inner.ToString().Length > 0) sawWriteDuringLock = true;
                insideLock = false;
            }
        });

        while (!insideLock && !t.IsCompleted) Thread.Sleep(1);
        writer.Write("XXXX");
        t.Wait();

        Assert.False(sawWriteDuringLock);
        Assert.Equal("XXXX", inner.ToString());
    }
}
