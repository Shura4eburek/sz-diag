using System.Text;

namespace SzDiag.Agent;

/// <summary>
/// Пишет лог-файл рядом с exe и дублирует туда весь консольный вывод.
/// Нужен, чтобы диагностировать падение агента: окно консоли на клиенте
/// закрывается вместе с процессом, а файл остаётся.
/// </summary>
public static class AgentLog
{
    /// <summary>Открывает (дозаписью) лог и пишет заголовок сессии.</summary>
    public static StreamWriter Init(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var sw = new StreamWriter(path, append: true, new UTF8Encoding(false)) { AutoFlush = true };
        sw.WriteLine();
        sw.WriteLine($"===== старт агента {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        return sw;
    }
}

/// <summary>Раздваивает поток вывода: в консоль и в лог-файл одновременно.</summary>
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
    public override void Write(char value) { _console.Write(value); _file.Write(value); }
    public override void Write(string? value) { _console.Write(value); _file.Write(value); }
    public override void WriteLine(string? value) { _console.WriteLine(value); _file.WriteLine(value); }
    public override void Flush() { _console.Flush(); _file.Flush(); }
}
