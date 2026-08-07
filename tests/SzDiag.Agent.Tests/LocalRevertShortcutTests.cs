using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>После ребута агент живёт headless (сессия 0, окна нет), и человек за клиентской
/// машиной снять доступ локально не мог ничем (бэклог п.87).</summary>
public class LocalRevertShortcutTests
{
    private const string Exe = @"C:\szdiag\agent.exe";
    private const string State = @"C:\ProgramData\szdiag\state.json";

    [Fact]
    public void Script_RunsRevertAsAdminWithForce()
    {
        var script = LocalRevertShortcut.BuildScript(Exe, State);

        Assert.Contains("--revert", script);
        // --force обязателен: это осознанное действие человека, а не watchdog, и метка
        // живости агента тут ничего не решает.
        Assert.Contains("'--force'", script);
        Assert.Contains("-Verb RunAs", script);
        Assert.Contains(Exe, script);
        Assert.Contains(State, script);
    }

    [Fact]
    public void ShortcutName_CarriesSzNumber_SoTwoSessionsAreDistinguishable()
    {
        Assert.Contains("160467", LocalRevertShortcut.FileName("160467"));
        Assert.EndsWith(".lnk", LocalRevertShortcut.FileName("160467"));
    }

    [Fact]
    public void ScriptPath_SitsNextToState_NotOnDesktop()
    {
        var path = LocalRevertShortcut.ScriptPath(State, "160467");

        Assert.Equal(Path.GetDirectoryName(State), Path.GetDirectoryName(path));
        Assert.Contains("160467", path);
    }

    [Fact]
    public void CreateCommand_UsesWScriptShell_AndQuotesPaths()
    {
        var cmd = LocalRevertShortcut.BuildCreateCommand(@"C:\Users\Public\Desktop\a b.lnk",
            @"C:\ProgramData\szdiag\revert-160467.cmd");

        Assert.Contains("WScript.Shell", cmd);
        Assert.Contains("$s.Save()", cmd);
        Assert.Contains(@"'C:\Users\Public\Desktop\a b.lnk'", cmd);
    }

    [Fact]
    public void Remove_OnMissingFiles_DoesNotThrow()
    {
        // Откат идёт по шагам и не должен спотыкаться о уже удалённый ярлык.
        LocalRevertShortcut.Remove("999999", Path.Combine(Path.GetTempPath(), "no-such-state.json"));
    }
}
