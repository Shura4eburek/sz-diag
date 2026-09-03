using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

/// <summary>Restart Manager называет держателя файла - тот же API, что диалог «Файл занят» в
/// проводнике. Нужно там, где `FileShare.ReadWrite | FileShare.Delete` не спасает: писатель
/// (например, живой `sshd`) открыл файл вовсе без совместного чтения (бэклог п.104).</summary>
public class FileLockInspectorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"szlock-{Guid.NewGuid():N}.log");

    [Fact]
    public void WhoIsLocking_FileHeldExclusivelyByThisProcess_NamesCurrentProcess()
    {
        File.WriteAllText(_path, "held");
        using var exclusive = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var holders = FileLockInspector.WhoIsLocking(_path);

        Assert.Contains(holders, h => h.Contains($"({Environment.ProcessId})"));
    }

    [Fact]
    public void WhoIsLocking_FileNotLocked_ReturnsEmpty()
    {
        File.WriteAllText(_path, "free");

        var holders = FileLockInspector.WhoIsLocking(_path);

        Assert.Empty(holders);
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
    }
}
