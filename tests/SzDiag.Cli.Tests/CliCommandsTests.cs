using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Протухший `szcli` из dist на неизвестную команду печатал usage с кодом 0 —
/// «note не работает» дважды списывалось на кавычки, пока не выяснилось, что в бинаре
/// команды просто нет (бэклог п.198).</summary>
public class CliCommandsTests
{
    [Theory]
    [InlineData("watch")]
    [InlineData("note")]
    [InlineData("EXEC")]   // регистр не важен
    public void IsKnown_RealCommands_True(string command)
        => Assert.True(CliCommands.IsKnown(command));

    [Theory]
    [InlineData("чегототам")]
    [InlineData("note2")]
    public void IsKnown_Garbage_False(string command)
        => Assert.False(CliCommands.IsKnown(command));

    [Fact]
    public void Describe_MentionsBuildDate()
        => Assert.Contains("сборка", CliCommands.Describe());
}
