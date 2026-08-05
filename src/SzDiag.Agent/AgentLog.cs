using System.Text;

namespace SzDiag.Agent;

/// <summary>
/// Пишет лог-файл рядом с exe и дублирует туда весь консольный вывод.
/// Нужен, чтобы диагностировать падение агента: окно консоли на клиенте
/// закрывается вместе с процессом, а файл остаётся.
/// </summary>
public static class AgentLog
{
    /// <summary>Открывает (дозаписью) лог и пишет заголовок сессии.
    ///
    /// Никогда не бросает: лог — вспомогательная вещь, из-за неё процесс падать не должен.
    /// На 160306 второй экземпляр агента не смог открыть занятый <c>agent.log</c> и умер
    /// необработанным <c>IOException</c> — машина осталась без heartbeat на час (бэклог п.52).
    /// Поэтому: сначала общий доступ на запись, при неудаче — свой файл
    /// <c>agent-&lt;pid&gt;.log</c>, в крайнем случае — <see cref="TextWriter.Null"/>.</summary>
    public static TextWriter Init(string path)
    {
        var dir = Path.GetDirectoryName(path);
        try
        {
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch { /* нет прав на каталог — уйдём в фоллбэки ниже */ }

        var writer = TryOpen(path) ?? TryOpen(WithPid(path)) ?? TextWriter.Null;
        try
        {
            writer.WriteLine();
            writer.WriteLine($"===== старт агента {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) =====");
        }
        catch { /* писать некуда — работаем без лога */ }
        return writer;
    }

    /// <summary>Путь вида <c>agent-1234.log</c> — когда общий файл занят намертво.</summary>
    public static string WithPid(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        return Path.Combine(dir, $"{name}-{Environment.ProcessId}{ext}");
    }

    private static TextWriter? TryOpen(string path)
    {
        try
        {
            // FileShare.ReadWrite: второй экземпляр агента (и хвост из редактора) должен
            // спокойно открыть тот же файл, а не ронять процесс.
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

// TeeTextWriter переехал в SzDiag.ConsoleUi — им пользуются и агент, и hub (лог hub в файл).
