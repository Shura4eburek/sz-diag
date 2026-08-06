using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`pull` по папке звался пять раз подряд (нет рекурсии), а пропуск 3-гигового файла
/// по лимиту возвращал exit 1 — в скрипте это выглядит как провал (бэклог п.75).</summary>
public class PullCommandTests
{
    private static PullSavedFile Ok(string name)
        => new(name, $@"C:\Windows\Minidump\{name}", 100, "sha", $@"C:\pulled\{name}");

    private static PullSavedFile OverLimit(string name)
        => new(name, $@"C:\Windows\LiveKernelReports\{name}", 3_087_000_000, "", null, true,
            "больше лимита (3 087,2 МБ > 5,0 МБ)", OverLimit: true);

    private static PullSavedFile Broken(string name)
        => new(name, $@"C:\x\{name}", 100, "", null, true, "нет доступа");

    [Fact]
    public void Parse_TakesSeveralPathsAndFlags()
    {
        var args = new[] { "pull", "161312", @"C:\Windows\Minidump", @"C:\Windows\LiveKernelReports",
            "--max-mb", "5", "-r" };

        var parsed = PullCommand.Parse(args);

        Assert.Equal("161312", parsed.Sz);
        Assert.Equal(2, parsed.Paths.Count);
        Assert.Equal(5L * 1024 * 1024, parsed.MaxBytes);
        Assert.True(parsed.Recurse);
    }

    [Fact]
    public void Parse_WithoutFlags_KeepsDefaults()
    {
        var parsed = PullCommand.Parse(new[] { "pull", "161312", @"C:\Windows\Minidump\*.dmp" });

        Assert.Single(parsed.Paths);
        Assert.Null(parsed.MaxBytes);
        Assert.False(parsed.Recurse);
    }

    [Fact]
    public void ExitCode_OnlyLimitSkips_IsSuccess()
    {
        Assert.Equal(0, PullCommand.ExitCodeFor(new[] { OverLimit("WATCHDOG.dmp") }, anyError: false));
    }

    [Fact]
    public void ExitCode_SomethingPulled_IsSuccessEvenWithLimitSkips()
    {
        Assert.Equal(0, PullCommand.ExitCodeFor(new[] { Ok("a.dmp"), OverLimit("huge.dmp") }, anyError: false));
    }

    [Fact]
    public void ExitCode_NothingPulledForRealReason_Fails()
    {
        Assert.NotEqual(0, PullCommand.ExitCodeFor(new[] { Broken("a.dmp") }, anyError: true));
        Assert.NotEqual(0, PullCommand.ExitCodeFor(Array.Empty<PullSavedFile>(), anyError: false));
    }
}
