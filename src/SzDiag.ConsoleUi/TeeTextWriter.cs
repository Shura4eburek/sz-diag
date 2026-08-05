using System.Text;

namespace SzDiag.ConsoleUi;

/// <summary>Раздваивает поток вывода: в консоль и в лог-файл одновременно.
///
/// Ошибки записи в файл глотаются намеренно: лог — вспомогательная вещь, из-за него
/// не должен ломаться ни вывод в консоль, ни сам процесс (см. историю падения агента
/// на занятом <c>agent.log</c>).</summary>
public sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly TextWriter _file;

    public TeeTextWriter(TextWriter console, TextWriter file)
    {
        _console = console;
        _file = file;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        _console.Write(value);
        try { _file.Write(value); } catch { }
    }

    public override void Write(string? value)
    {
        _console.Write(value);
        try { _file.Write(value); } catch { }
    }

    public override void WriteLine(string? value)
    {
        _console.WriteLine(value);
        try { _file.WriteLine(value); } catch { }
    }

    public override void Flush()
    {
        _console.Flush();
        try { _file.Flush(); } catch { }
    }
}
