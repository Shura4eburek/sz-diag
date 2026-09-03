using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class DiagCompletionWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szdiagwait-{Guid.NewGuid():N}");

    [Fact]
    public void FindLatest_NoReportsDir_ReturnsNull()
        => Assert.Null(DiagCompletionWatcher.FindLatest(_root, DateTime.Now));

    [Fact]
    public void FindLatest_IgnoresReportsOlderThanAfter()
    {
        var old = Path.Combine(_root, "20260101-000000");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "diag.md"), "old");

        var after = DateTime.Now.AddMinutes(1);   // старый отчёт «раньше», чем момент запуска
        Assert.Null(DiagCompletionWatcher.FindLatest(_root, after));
    }

    [Fact]
    public void FindLatest_ReportWrittenAfter_ReturnsPathAndSize()
    {
        var after = DateTime.Now.AddSeconds(-1);
        var dir = Path.Combine(_root, "20260904-120000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "diag.md"), "hello");

        var found = DiagCompletionWatcher.FindLatest(_root, after);

        Assert.NotNull(found);
        Assert.EndsWith(Path.Combine("20260904-120000", "diag.md"), found!.Value.Path);
        Assert.Equal(5, found.Value.Bytes);
    }

    [Fact]
    public async Task WaitAsync_FileAppearsWhilePolling_ReturnsBeforeTimeout()
    {
        var after = DateTime.Now.AddSeconds(-1);
        Directory.CreateDirectory(_root);

        var waitTask = DiagCompletionWatcher.WaitAsync(_root, after, TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(50));

        await Task.Delay(150);
        var dir = Path.Combine(_root, "20260904-120000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "diag.md"), "hello");

        var found = await waitTask;
        Assert.NotNull(found);
    }

    [Fact]
    public async Task WaitAsync_NeverAppears_ReturnsNullAfterTimeout()
    {
        var found = await DiagCompletionWatcher.WaitAsync(_root, DateTime.Now, TimeSpan.FromMilliseconds(120),
            pollInterval: TimeSpan.FromMilliseconds(30));
        Assert.Null(found);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
