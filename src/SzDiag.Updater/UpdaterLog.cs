using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Updater;

/// <summary>Открытый лог апдейтера: куда пишем и чем пишем. <see cref="Path"/> = null —
/// писать не удалось никуда (тогда апдейтер работает молча, но не падает).</summary>
public sealed class UpdaterLogHandle : IDisposable
{
    public UpdaterLogHandle(TextWriter writer, string? path)
    {
        Writer = writer;
        Path = path;
    }

    public TextWriter Writer { get; }
    public string? Path { get; }

    public void Dispose() => Writer.Dispose();
}

/// <summary>
/// Пишет лог рядом с exe и дублирует туда весь консольный вывод апдейтера.
///
/// Зачем: апдейтер — точка входа на клиенте, запускается двойным кликом, и любой ранний
/// выход закрывает окно вместе с причиной. На 161642 «окно появилось и сразу пропало» —
/// и понять, что это было (облачная папка, ненайденный hub, отказ UAC или падение),
/// оказалось нечем: следа не оставалось вообще.
///
/// Логика повторяет <c>AgentLog</c>, но живёт здесь: апдейтер намеренно тонкий и не
/// ссылается ни на агента, ни на ConsoleUi (Spectre ему не нужен).
/// </summary>
public static class UpdaterLog
{
    /// <summary>Открывает (дозаписью) лог и пишет заголовок запуска.
    ///
    /// Никогда не бросает: лог — вспомогательная вещь, из-за неё апдейтер падать не должен.
    /// Порядок попыток: заданный файл → <c>updater-&lt;pid&gt;.log</c> рядом с ним →
    /// то же самое в <c>%TEMP%\szdiag\</c> → без лога.
    ///
    /// Уход в <c>%TEMP%</c> обязателен: папка рядом с exe может быть недоступна на запись
    /// (Program Files, флешка только на чтение, каталог занят одноимённым файлом), и тогда
    /// апдейтер снова умирает молча — ровно та ситуация, ради которой лог и заводился
    /// (161642, второй заход: «лог файла нет»).</summary>
    public static UpdaterLogHandle Open(string preferredPath)
    {
        var tempPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "szdiag", "updater.log");
        foreach (var candidate in new[] { preferredPath, WithPid(preferredPath), tempPath, WithPid(tempPath) })
        {
            var writer = TryOpen(candidate);
            if (writer is null) continue;

            try
            {
                writer.WriteLine();
                writer.WriteLine($"===== старт апдейтера {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) =====");
            }
            catch { /* открылся, но не пишется — считаем негодным и идём дальше */ }

            return new UpdaterLogHandle(writer, candidate);
        }

        return new UpdaterLogHandle(TextWriter.Null, null);
    }

    /// <summary>Куда писать лог: <c>logs\updater.log</c> рядом с exe, а из синхронизируемой
    /// облачной папки — во временный каталог машины. В облако логи класть нельзя по той же
    /// причине, по которой оттуда отказывается работать <see cref="CloudInstallGuard"/>:
    /// они уедут в личное облако клиента и не откатятся при закрытии СЗ (бэклог п.41).
    ///
    /// Проверку «облачности» держим здесь, внутри try: она лезет в файловую систему и
    /// переменные среды, а вызывается ДО открытия лога — брошенное отсюда исключение
    /// снова оставило бы апдейтер без единого следа.</summary>
    public static string PathFor(string baseDir)
    {
        bool cloudSynced;
        try { cloudSynced = CloudSyncPaths.IsSynced(baseDir); }
        catch { cloudSynced = false; }

        if (cloudSynced) return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "szdiag", "updater.log");

        try { return System.IO.Path.Combine(baseDir, "logs", "updater.log"); }
        catch { return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "szdiag", "updater.log"); }
    }

    /// <summary>Путь вида <c>updater-1234.log</c> — когда общий файл занят намертво.</summary>
    public static string WithPid(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var ext = System.IO.Path.GetExtension(path);
        return System.IO.Path.Combine(dir, $"{name}-{Environment.ProcessId}{ext}");
    }

    /// <summary>Раздваивает вывод: в консоль и в лог. Ошибки записи в файл глотаются —
    /// сломанный лог не должен ломать работу апдейтера.</summary>
    public static TextWriter Tee(TextWriter console, TextWriter file) => new TeeWriter(console, file);

    private static TextWriter? TryOpen(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // FileShare.ReadWrite: параллельный запуск и хвост из редактора должны спокойно
            // открывать тот же файл, а не ронять процесс.
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch { return null; }
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly TextWriter _file;

        public TeeWriter(TextWriter console, TextWriter file)
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
}
