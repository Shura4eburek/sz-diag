using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`szcli test run --schedule <имя>` (бэклог п.124/#60) — выбор длины прогона
/// командой вместо подмены файла на клиенте руками.</summary>
public class OcctScheduleProfilesTests
{
    [Theory]
    [InlineData(null, "schedule.json")]
    [InlineData("", "schedule.json")]
    [InlineData("default", "schedule.json")]
    [InlineData("DEFAULT", "schedule.json")]
    [InlineData("smoke", "schedule-smoke.json")]
    [InlineData("long", "schedule-long.json")]
    [InlineData("LONG", "schedule-long.json")]
    [InlineData("infinite", "schedule-infinite.json")]
    public void ResolveFileName_KnownProfiles_ResolvesExpectedFile(string? profile, string expected)
        => Assert.Equal(expected, OcctScheduleProfiles.ResolveFileName(profile));

    [Fact]
    public void ResolveFileName_UnknownProfile_ReturnsNull()
        => Assert.Null(OcctScheduleProfiles.ResolveFileName("ultra-mega"));

    [Fact]
    public void KnownProfiles_ListsDefaultAndAllFiles()
    {
        var known = OcctScheduleProfiles.KnownProfiles.ToList();

        Assert.Contains("default", known);
        Assert.Contains("smoke", known);
        Assert.Contains("long", known);
        Assert.Contains("infinite", known);
    }
}
