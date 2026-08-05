using System.Text;

namespace SzDiag.Agent;

/// <summary>Отдельный лог отката — рядом с `state.json`, а не рядом с exe.
///
/// Откат по watchdog идёт **без консоли и без агента** (transient scheduled task под SYSTEM),
/// и когда он упал на 160705, единственным следом было обрезанное сообщение в Application-логе
/// Windows: ни типа исключения, ни пути (бэклог п.59). Файл рядом с состоянием переживает всё
/// и лежит там, где его будут искать — вместе с тем, что откатывали.
///
/// Как и остальное логирование в агенте, никогда не бросает.</summary>
public sealed class RevertLog : IDisposable
{
    private readonly TextWriter _writer;

    private RevertLog(TextWriter writer) => _writer = writer;

    /// <summary>Открывает `revert.log` рядом с указанным state-файлом.</summary>
    public static RevertLog Open(string statePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(statePath));
            if (string.IsNullOrEmpty(dir)) return new RevertLog(TextWriter.Null);
            Directory.CreateDirectory(dir);

            var fs = new FileStream(Path.Combine(dir, "revert.log"), FileMode.Append,
                FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine();
            writer.WriteLine($"===== откат {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) =====");
            return new RevertLog(writer);
        }
        catch
        {
            return new RevertLog(TextWriter.Null);
        }
    }

    public void Write(string message)
    {
        try { _writer.WriteLine(message); } catch { /* лог не должен мешать откату */ }
    }

    public void Dispose()
    {
        try { _writer.Flush(); _writer.Dispose(); } catch { }
    }
}
