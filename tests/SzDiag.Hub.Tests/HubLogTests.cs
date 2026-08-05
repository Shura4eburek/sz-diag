using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class HubLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szhublog-{Guid.NewGuid():N}");

    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [Fact]
    public void Init_WritesDailyFileWithHeader()
    {
        using var w = HubLog.Init(_dir);
        w.WriteLine("СЗ 160705: клиент перезагрузился");
        w.Flush();

        var file = Directory.GetFiles(_dir, "hub-*.log").Single();
        Assert.Contains($"hub-{DateTime.Now:yyyyMMdd}.log", file);
        var text = ReadShared(file);
        Assert.Contains("старт hub", text);
        Assert.Contains("СЗ 160705: клиент перезагрузился", text);   // кириллица не бьётся
    }

    [Fact]
    public void Init_AppendsInsteadOfTruncating()
    {
        var first = HubLog.Init(_dir);
        first.WriteLine("первый запуск");
        first.Dispose();

        using var second = HubLog.Init(_dir);
        second.WriteLine("второй запуск");
        second.Flush();

        var text = ReadShared(Directory.GetFiles(_dir, "hub-*.log").Single());
        Assert.Contains("первый запуск", text);
        Assert.Contains("второй запуск", text);
    }

    [Fact]
    public void Init_SecondInstance_DoesNotThrowOnBusyFile()
    {
        // hub может быть запущен дважды по ошибке — из-за лога он падать не должен.
        using var first = HubLog.Init(_dir);
        using var second = HubLog.Init(_dir);

        second.WriteLine("второй экземпляр");
        second.Flush();

        Assert.Contains("второй экземпляр", ReadShared(Directory.GetFiles(_dir, "hub-*.log").Single()));
    }

    [Fact]
    public void Prune_RemovesOldLogsAndKeepsFresh()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "hub-20260101.log");
        var fresh = Path.Combine(_dir, $"hub-{DateTime.Now:yyyyMMdd}.log");
        File.WriteAllText(old, "старый");
        File.WriteAllText(fresh, "свежий");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

        HubLog.Prune(_dir, retentionDays: 14);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Prune_ZeroRetention_KeepsEverything()
    {
        Directory.CreateDirectory(_dir);
        var old = Path.Combine(_dir, "hub-20260101.log");
        File.WriteAllText(old, "старый");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-300));

        HubLog.Prune(_dir, retentionDays: 0);

        Assert.True(File.Exists(old));
    }

    [Fact]
    public void Init_UnusableDirectory_ReturnsNullWriter()
    {
        // Путь занят файлом — логирование обязано деградировать молча, а не ронять hub.
        Directory.CreateDirectory(_dir);
        var asFile = Path.Combine(_dir, "occupied");
        File.WriteAllText(asFile, "это файл");

        var w = HubLog.Init(Path.Combine(asFile, "logs"));

        Assert.Same(TextWriter.Null, w);
        w.WriteLine("никуда");   // не бросает
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}
