using SzDiag.Desk.Services;

namespace SzDiag.Desk.Tests;

/// <summary>Настоящий подпроцесс: вместо SzDiag.Cli.exe — копия cmd.exe в раскладке dist
/// (`host\szcli.cmd` + `host\cli\SzDiag.Cli.exe`).</summary>
public class SzcliRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szcli-" + Guid.NewGuid().ToString("N"));
    private string Cmd => Path.Combine(_dir, "szcli.cmd");

    public SzcliRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cli"));
        File.WriteAllText(Cmd, "@echo off");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(_dir, "cli", "SzDiag.Cli.exe"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Runs_CapturesOutputAndExitCode_NoColor()
    {
        var r = await new SzcliRunner(() => Cmd).RunAsync(new[] { "/d", "/c", "echo hello& echo %NO_COLOR%& exit 3" }, default);
        Assert.Equal(3, r.ExitCode);
        Assert.Contains("hello", r.Output);
        Assert.Contains("1", r.Output);   // NO_COLOR=1: Spectre не красит, в окне нет ESC-мусора
    }

    [Fact]
    public async Task NoExe_ExplainsBuildDist()
    {
        var r = await new SzcliRunner(() => null).RunAsync(new[] { "list" }, default);
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("build-dist", r.Output);
    }

    [Fact]
    public async Task Cancel_KillsAndSaysSo()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var r = await new SzcliRunner(() => Cmd).RunAsync(new[] { "/d", "/c", "ping -n 30 127.0.0.1 >nul" }, cts.Token);
        Assert.Equal(-2, r.ExitCode);
        Assert.Contains("прервано", r.Output);
    }

    [Fact]
    public async Task ExeWontStart_ResultNotCrash()
    {
        // Ревью I-5: Process.Start бросает Win32Exception (антивирус, битый exe) — команда из
        // окна не должна ронять Desk.
        var dir = Path.Combine(_dir, "broken");
        Directory.CreateDirectory(Path.Combine(dir, "cli"));
        File.WriteAllText(Path.Combine(dir, "szcli.cmd"), "@echo off");
        File.WriteAllText(Path.Combine(dir, "cli", "SzDiag.Cli.exe"), "это не exe");

        var r = await new SzcliRunner(() => Path.Combine(dir, "szcli.cmd")).RunAsync(new[] { "list" }, default);
        Assert.Equal(SzcliRunner.NotFound, r.ExitCode);
        Assert.Contains("не запустился", r.Output);
    }

    [Fact]
    public void StripAnsi_RemovesEscapes()
        => Assert.Equal("СЗ 161432 ok", SzcliRunner.StripAnsi("\u001b[32mСЗ 161432\u001b[0m ok"));

    [Fact]
    public void FreezeProbe_SameFileAsSzcliList()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "cli", "freeze"));
        File.WriteAllText(Path.Combine(_dir, "cli", "freeze", "161432.json"), "{}");
        var probe = new FreezeProbe(() => Cmd);
        Assert.True(probe.IsFrozen("161432"));
        Assert.False(probe.IsFrozen("161501"));
        Assert.False(new FreezeProbe(() => null).IsFrozen("161432"));
    }
}
