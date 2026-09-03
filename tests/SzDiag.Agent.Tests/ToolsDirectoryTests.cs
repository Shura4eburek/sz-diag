using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class ToolsDirectoryTests
{
    [Theory]
    [InlineData(@"C:\Users\msi-pc\OneDrive\Desktop\Client-test")]     // реальный путь с СЗ 160705
    [InlineData(@"C:\Users\User\OneDrive\Робочий стіл\Client-test")]  // и с 160636
    [InlineData(@"C:\Users\u\Dropbox\szdiag")]
    [InlineData(@"C:\Users\u\Google Drive\szdiag")]
    public void IsCloudSynced_KnownCloudFolders_True(string path)
        => Assert.True(ToolsDirectory.IsCloudSynced(path));

    [Theory]
    [InlineData(@"C:\Client-test")]
    [InlineData(@"C:\Users\User\Desktop\Client-test")]
    [InlineData(@"D:\szdiag\agent")]
    public void IsCloudSynced_LocalFolders_False(string path)
        => Assert.False(ToolsDirectory.IsCloudSynced(path));

    [Fact]
    public void Resolve_LocalDir_KeepsToolsNextToAgent()
    {
        var (dir, moved) = ToolsDirectory.Resolve(@"C:\Client-test");

        Assert.False(moved);
        Assert.Equal(Path.Combine(@"C:\Client-test", "tools"), dir);
    }

    [Fact]
    public void Resolve_CloudDir_MovesToProgramData()
    {
        // 250 МБ инструментов в личном облаке клиента откатить невозможно (бэклог п.63).
        var (dir, moved) = ToolsDirectory.Resolve(@"C:\Users\msi-pc\OneDrive\Desktop\Client-test");

        Assert.True(moved);
        Assert.Contains("szdiag", dir);
        Assert.DoesNotContain("OneDrive", dir);
    }

    [Fact]
    public void ResolveStepPath_ToolsPrefix_UsesActualToolsDir()
    {
        // Регрессия (бэклог п.151, СЗ 161716): push увёл раздачу в ProgramData (агент в
        // OneDrive), а TestRunner резолвил exe от AppContext.BaseDirectory — occt/tm5
        // никогда не находились, хотя push отчитался успехом.
        var path = ToolsDirectory.ResolveStepPath(
            @"C:\Users\vasya\OneDrive\Desktop\Client-test",
            @"C:\ProgramData\szdiag\tools",
            @"tools\occt\OCCTCmd.exe");

        Assert.Equal(Path.Combine(@"C:\ProgramData\szdiag\tools", "occt", "OCCTCmd.exe"), path);
    }

    [Fact]
    public void ResolveStepPath_NonToolsRelativePath_ResolvesAgainstBaseDir()
    {
        var path = ToolsDirectory.ResolveStepPath(
            @"C:\Client-test", @"C:\Client-test\tools", @"testsuite.json");

        Assert.Equal(Path.Combine(@"C:\Client-test", "testsuite.json"), path);
    }

    [Fact]
    public void ResolveStepPath_AbsolutePath_PassesThrough()
    {
        var path = ToolsDirectory.ResolveStepPath(
            @"C:\Client-test", @"C:\ProgramData\szdiag\tools", @"D:\custom\occt.exe");

        Assert.Equal(@"D:\custom\occt.exe", path);
    }

    [Fact]
    public void ResolveStepPath_LocalAgentDir_MatchesPlainCombine()
    {
        // Обычная (не облачная) папка: toolsDir == baseDir\tools, поведение не меняется.
        var path = ToolsDirectory.ResolveStepPath(
            @"C:\Client-test", @"C:\Client-test\tools", @"tools\tm5\TM5.exe");

        Assert.Equal(Path.Combine(@"C:\Client-test\tools", "tm5", "TM5.exe"), path);
    }
}
