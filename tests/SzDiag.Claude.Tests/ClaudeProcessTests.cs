using System.Diagnostics;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

/// <summary>Обвязка процесса на обычных программах Windows: sort.exe читает stdin до EOF и
/// печатает строки, cmd пишет в stderr, ping живёт 30 с и не читает stdin.</summary>
public class ClaudeProcessTests
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
        var p = new ClaudeProcess();
        var lines = new List<string>();
        var exited = new TaskCompletionSource<int?>();
        p.OutputLine += l => { lock (lines) lines.Add(l); };
        p.Exited += c => exited.TrySetResult(c);

        p.Start(Psi("sort.exe"));
        Assert.True(p.IsRunning);
        await p.WriteLineAsync("b");
        await p.WriteLineAsync("a");
        await p.StopAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { "a", "b" }, lines);
        Assert.False(p.IsRunning);
    }

    [Fact]
    public async Task Stderr_KeptInTail()
    {
        var p = new ClaudeProcess();
        var exited = new TaskCompletionSource<int?>();
        p.Exited += c => exited.TrySetResult(c);
        p.Start(Psi("cmd.exe", "/d", "/c", "echo oops 1>&2"));
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(p.StderrTail, l => l.Contains("oops"));
    }

    [Fact]
    public async Task OrphanHoldingPipes_ExitAndStopStillComplete()
    {
        // cmd уходит сразу, а запущенный через start /b ping наследует его stdout/stderr и держит
        // пайпы ещё 8 с. Выход процесса не должен ждать EOF: иначе «■» и закрытие Desk висят,
        // пока жив осиротевший внук (ревью I-4).
        var p = new ClaudeProcess();
        var exited = new TaskCompletionSource<int?>();
        p.Exited += c => exited.TrySetResult(c);
        p.Start(Psi("cmd.exe", "/d", "/c", "start /b ping -n 8 127.0.0.1 >nul & exit 0"));

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(4));
        var sw = Stopwatch.StartNew();
        await p.StopAsync(TimeSpan.FromMilliseconds(300));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"остановка заняла {sw.Elapsed}");
    }

    [Fact]
    public async Task Stop_KillsProcessThatIgnoresStdin()
    {
        var p = new ClaudeProcess();
        p.Start(Psi("ping.exe", "-n", "30", "127.0.0.1"));
        var sw = Stopwatch.StartNew();
        await p.StopAsync(TimeSpan.FromMilliseconds(300));
        Assert.False(p.IsRunning);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"остановка заняла {sw.Elapsed}");
    }
}
