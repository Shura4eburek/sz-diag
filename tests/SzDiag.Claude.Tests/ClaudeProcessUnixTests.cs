using System.Diagnostics;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Те же проверки, что в <see cref="ClaudeProcessTests"/>, на программах Unix: вне Windows у
/// <see cref="ClaudeProcess"/> своё ожидание выхода. На Windows тесты выходят сразу.</summary>
public class ClaudeProcessUnixTests
{
    private static ProcessStartInfo Psi(string exe, params string[] args)
    {
        var p = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) p.ArgumentList.Add(a);
        return p;
    }

    [Fact]
    public async Task WriteLines_CloseStdin_ReadOutput_Exit()
    {
        if (OperatingSystem.IsWindows()) return;
        var p = new ClaudeProcess();
        var lines = new List<string>();
        var exited = new TaskCompletionSource<int?>();
        p.OutputLine += l => { lock (lines) lines.Add(l); };
        p.Exited += c => exited.TrySetResult(c);

        p.Start(Psi("/usr/bin/sort"));
        Assert.True(p.IsRunning);
        await p.WriteLineAsync("b");
        await p.WriteLineAsync("a");
        await p.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "a", "b" }, lines);
        Assert.False(p.IsRunning);
    }

    [Fact]
    public async Task OrphanHoldingPipes_ExitAndStopStillComplete()
    {
        if (OperatingSystem.IsWindows()) return;
        var p = new ClaudeProcess();
        var exited = new TaskCompletionSource<int?>();
        p.Exited += c => exited.TrySetResult(c);
        p.Start(Psi("/bin/sh", "-c", "sleep 8 & exit 0"));

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var sw = Stopwatch.StartNew();
        await p.StopAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"остановка заняла {sw.Elapsed}");
    }

    [Fact]
    public async Task Stop_KillsProcessThatIgnoresStdin()
    {
        if (OperatingSystem.IsWindows()) return;
        var p = new ClaudeProcess();
        p.Start(Psi("/bin/sleep", "30"));
        var sw = Stopwatch.StartNew();
        await p.StopAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(p.IsRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"остановка заняла {sw.Elapsed}");
    }
}
