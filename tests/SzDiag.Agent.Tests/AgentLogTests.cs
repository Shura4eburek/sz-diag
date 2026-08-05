using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class AgentLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"szlog-{Guid.NewGuid():N}");

    private string LogPath => Path.Combine(_dir, "logs", "agent.log");

    /// <summary>Читает лог, пока агент держит его открытым: File.ReadAllText просит
    /// FileShare.Read и упирается в живой хендл на запись (так же ведёт себя любой tail).</summary>
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [Fact]
    public void Init_CreatesFileAndWritesHeader()
    {
        using var w = AgentLog.Init(LogPath);
        w.WriteLine("проверка кириллицы");
        w.Flush();

        var text = ReadShared(LogPath);
        Assert.Contains("старт агента", text);
        Assert.Contains("проверка кириллицы", text);
    }

    [Fact]
    public void Init_SecondInstance_SharesFileInsteadOfThrowing()
    {
        // Регрессия (бэклог п.52): второй экземпляр агента падал необработанным IOException
        // на занятом agent.log, и машина оставалась без heartbeat.
        using var first = AgentLog.Init(LogPath);
        var second = AgentLog.Init(LogPath);   // не должно бросить

        second.WriteLine("второй экземпляр");
        second.Flush();
        second.Dispose();

        Assert.Contains("второй экземпляр", ReadShared(LogPath));
    }

    [Fact]
    public void Init_FileLockedExclusively_FallsBackToPidLogWithoutThrowing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        using var exclusive = new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.None);

        using var w = AgentLog.Init(LogPath);
        w.WriteLine("ушли в запасной лог");
        w.Flush();

        var fallback = AgentLog.WithPid(LogPath);
        Assert.True(File.Exists(fallback), "должен появиться agent-<pid>.log");
        Assert.Contains("ушли в запасной лог", ReadShared(fallback));
    }

    [Fact]
    public void Init_UnwritableDirectory_ReturnsNullWriterInsteadOfThrowing()
    {
        // Путь заведомо невозможный: логирование обязано деградировать молча, а не ронять агента.
        var impossible = Path.Combine(LogPath, "nested", "agent.log");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        File.WriteAllText(LogPath, "это файл, а не каталог");

        var w = AgentLog.Init(impossible);

        w.WriteLine("никуда");   // не бросает
        w.Flush();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }
}

public class SingleInstanceGuardTests
{
    [Fact]
    public void Acquire_FirstIsPrimary_SecondIsNot()
    {
        var name = $@"Local\szdiag-agent-test-{Guid.NewGuid():N}";

        using var first = SingleInstanceGuard.Acquire(name);
        using var second = SingleInstanceGuard.Acquire(name);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void Acquire_AfterDispose_SlotIsFreeAgain()
    {
        var name = $@"Local\szdiag-agent-test-{Guid.NewGuid():N}";

        var first = SingleInstanceGuard.Acquire(name);
        Assert.True(first.IsPrimary);
        first.Dispose();

        using var next = SingleInstanceGuard.Acquire(name);
        Assert.True(next.IsPrimary);
    }
}
