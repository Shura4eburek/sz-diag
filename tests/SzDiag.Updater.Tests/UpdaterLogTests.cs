using System.Text;
using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

/// <summary>Лог апдейтера — единственный след его работы на клиенте: окно консоли
/// закрывается вместе с процессом (на 161642 «окно появилось и сразу пропало», а причина
/// раннего выхода не осталась нигде).</summary>
public sealed class UpdaterLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szupd-log-" + Guid.NewGuid().ToString("N"));

    public UpdaterLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Open_создаёт_файл_и_пишет_заголовок_сессии()
    {
        var path = Path.Combine(_dir, "logs", "updater.log");

        using (var log = UpdaterLog.Open(path))
            log.Writer.WriteLine("строка");

        var text = File.ReadAllText(path);
        Assert.Contains("старт апдейтера", text);
        Assert.Contains($"pid {Environment.ProcessId}", text);
        Assert.Contains("строка", text);
    }

    [Fact]
    public void Open_возвращает_фактический_путь_файла()
    {
        var path = Path.Combine(_dir, "logs", "updater.log");

        using var log = UpdaterLog.Open(path);

        Assert.Equal(path, log.Path);
    }

    [Fact]
    public void Open_дозаписывает_в_существующий_файл()
    {
        var path = Path.Combine(_dir, "updater.log");
        File.WriteAllText(path, "прошлый запуск" + Environment.NewLine);

        using (var log = UpdaterLog.Open(path))
            log.Writer.WriteLine("новый запуск");

        var text = File.ReadAllText(path);
        Assert.Contains("прошлый запуск", text);
        Assert.Contains("новый запуск", text);
    }

    [Fact]
    public void Open_при_намертво_занятом_файле_пишет_рядом_свой_с_pid()
    {
        var path = Path.Combine(_dir, "updater.log");
        using var blocker = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);

        using (var log = UpdaterLog.Open(path))
            log.Writer.WriteLine("запасной путь");

        var fallback = UpdaterLog.WithPid(path);
        Assert.True(File.Exists(fallback), $"нет запасного файла {fallback}");
        Assert.Contains("запасной путь", File.ReadAllText(fallback));
    }

    [Fact]
    public void Open_когда_рядом_с_exe_писать_нельзя_уводит_лог_в_temp()
    {
        // Каталог занят файлом с тем же именем — создать его под лог невозможно. Так же
        // выглядит папка без прав на запись (Program Files, флешка только на чтение).
        // Терять лог в этом случае нельзя: ровно ради него апдейтер и запускают повторно
        // (161642 — «лог файла нет» после запуска из-под Windows).
        var busy = Path.Combine(_dir, "занято");
        File.WriteAllText(busy, "");

        string? actual;
        // Читаем после закрытия: открытый на запись хэндл не пускает File.ReadAllText
        // (тот просит FileShare.Read, а живой писатель держит Write).
        using (var log = UpdaterLog.Open(Path.Combine(busy, "logs", "updater.log")))
        {
            actual = log.Path;
            log.Writer.WriteLine("ушло в temp");
        }

        Assert.NotNull(actual);
        Assert.StartsWith(Path.GetTempPath(), actual!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ушло в temp", File.ReadAllText(actual!));
    }

    [Fact]
    public void Tee_пишет_и_в_консоль_и_в_файл()
    {
        var console = new StringWriter();
        var file = new StringWriter();

        using (var tee = UpdaterLog.Tee(console, file))
            tee.WriteLine("важное");

        Assert.Contains("важное", console.ToString());
        Assert.Contains("важное", file.ToString());
    }

    [Fact]
    public void Tee_не_роняет_вывод_если_файл_сломался()
    {
        var console = new StringWriter();
        var broken = new ThrowingWriter();

        using (var tee = UpdaterLog.Tee(console, broken))
            tee.WriteLine("важное");

        Assert.Contains("важное", console.ToString());
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => throw new IOException("диск отвалился");
        public override void Write(string? value) => throw new IOException("диск отвалился");
    }
}

/// <summary>Куда класть лог: рядом с exe, но НЕ в синхронизируемую папку — иначе логи
/// сессии уедут в личное облако клиента (та же причина, что у CloudInstallGuard).</summary>
public sealed class UpdaterLogPathTests
{
    [Fact]
    public void PathFor_кладёт_лог_рядом_с_exe()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "szdiag-client");

        var path = UpdaterLog.PathFor(baseDir);

        Assert.Equal(Path.Combine(baseDir, "logs", "updater.log"), path);
    }

    [Fact]
    public void PathFor_из_облачной_папки_уводит_лог_наружу()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "OneDrive", "Робочий стіл", "client");

        var path = UpdaterLog.PathFor(baseDir);

        Assert.False(path.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase),
            $"лог остался в облачной папке: {path}");
        Assert.EndsWith("updater.log", path);
    }

    [Fact]
    public void PathFor_не_бросает_на_негодном_пути()
    {
        // Определение «облачности» пути само лезет в файловую систему и переменные среды.
        // Бросить оно может ДО того, как открыт лог, — и тогда апдейтер снова умирает молча,
        // ровно от той диагностики, ради которой всё затевалось.
        var path = UpdaterLog.PathFor("\0:<>|");

        Assert.EndsWith("updater.log", path);
    }
}
