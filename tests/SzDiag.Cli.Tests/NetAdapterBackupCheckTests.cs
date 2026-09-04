using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>#160/бэклог п.206: `close` должен кричать про забытый откат настроек сетевого
/// адаптера — как уже кричит про забытый `unfreeze` (СЗ 162367).</summary>
public class NetAdapterBackupCheckTests
{
    [Fact]
    public void ParseExists_True_ReturnsTrue()
        => Assert.True(NetAdapterBackupCheck.ParseExists("True"));

    [Fact]
    public void ParseExists_False_ReturnsFalse()
        => Assert.False(NetAdapterBackupCheck.ParseExists("False"));

    [Fact]
    public void ParseExists_WithWhitespace_StillParsed()
        => Assert.True(NetAdapterBackupCheck.ParseExists("  True  \r\n"));

    [Fact]
    public void ParseExists_Null_ReturnsFalse()
        => Assert.False(NetAdapterBackupCheck.ParseExists(null));

    [Fact]
    public void ParseExists_Empty_ReturnsFalse()
        => Assert.False(NetAdapterBackupCheck.ParseExists(""));

    [Fact]
    public void ProbeScript_MentionsBackupPath()
        => Assert.Contains(NetAdapterBackupCheck.BackupPath, NetAdapterBackupCheck.ProbeScript);
}
