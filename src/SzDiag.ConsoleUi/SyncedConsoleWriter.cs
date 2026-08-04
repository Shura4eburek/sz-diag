using System.Text;

namespace SzDiag.ConsoleUi;

/// <summary>
/// Обёртка над консольным writer'ом, сериализующая записи общим локом.
/// Тем же локом пользуется <see cref="StickyHeader"/> при перерисовке панели: иначе
/// таймер переставит курсор наверх посреди чужой строки, и её хвост уедет в панель.
/// </summary>
public sealed class SyncedConsoleWriter : TextWriter
{
    private readonly TextWriter _inner;
    private readonly object _gate;

    public SyncedConsoleWriter(TextWriter inner, object gate)
    {
        _inner = inner;
        _gate = gate;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) { lock (_gate) _inner.Write(value); }
    public override void Write(string? value) { lock (_gate) _inner.Write(value); }
    public override void WriteLine() { lock (_gate) _inner.WriteLine(); }
    public override void WriteLine(string? value) { lock (_gate) _inner.WriteLine(value); }
    public override void Flush() { lock (_gate) _inner.Flush(); }
}
