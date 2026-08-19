using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class TestRunArgsTests
{
    [Fact]
    public void Parse_ConfigFlag_TakesNextArgumentAsLabel()
    {
        var args = TestRunArgs.Parse(new[] { "occt", "--config", "EXPO 6000, штатний БЖ" });

        Assert.Equal("occt", args.Filter);
        Assert.Equal("EXPO 6000, штатний БЖ", args.Config);
        Assert.False(args.SameConfig);
    }

    [Fact]
    public void Parse_SameConfigFlag_SetsFlagWithoutLabel()
    {
        var args = TestRunArgs.Parse(new[] { "occt", "--same-config" });

        Assert.True(args.SameConfig);
        Assert.Null(args.Config);
    }

    [Fact]
    public void Parse_NoFilter_LeavesFilterNull()
    {
        var args = TestRunArgs.Parse(new[] { "--config", "сток JEDEC 4800" });

        Assert.Null(args.Filter);
        Assert.Equal("сток JEDEC 4800", args.Config);
    }

    [Fact]
    public void Parse_ConfigWithoutValue_LeavesConfigNull()
    {
        var args = TestRunArgs.Parse(new[] { "occt", "--config" });

        Assert.Equal("occt", args.Filter);
        Assert.Null(args.Config);
    }

    [Fact]
    public void Parse_Empty_GivesNothing()
    {
        var args = TestRunArgs.Parse(Array.Empty<string>());

        Assert.Null(args.Filter);
        Assert.Null(args.Config);
        Assert.False(args.SameConfig);
    }
}
