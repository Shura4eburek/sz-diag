namespace SzDiag.Hub;

/// <summary>Поток на чтение, который сообщает о каждом отданном куске — так hub видит
/// прогресс push, хотя байты качает сам агент обычным HTTP.</summary>
public sealed class CountingReadStream(Stream inner, Action<long> onRead) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        if (n > 0) onRead(n);
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        if (n > 0) onRead(n);
        return n;
    }

    // Явно, а не через базовый BeginRead/EndRead: тот уходит в синхронный Read на пуле.
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
