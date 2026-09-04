using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>#164/#194: запуск GUI на клиенте из session 0 (агент под SYSTEM) требует
/// транзиентной задачи в интерактивной сессии. Две детали, добытые дорого (бэклог п.220):
/// RunLevel Limited для GUI (Highest молча не создаёт окно) и имя пользователя из
/// Win32_ComputerSystem.UserName, а не quser (в session 0 quser ломает наивный парсер).</summary>
public class InteractiveSessionRunTests
{
    [Fact]
    public void BuildScript_NotElevated_UsesLimitedRunLevel()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\Windows\explorer.exe", null, elevated: false);

        Assert.Contains("-RunLevel Limited", script);
    }

    [Fact]
    public void BuildScript_Elevated_UsesHighestRunLevel()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\app\signalrgb.exe", null, elevated: true);

        Assert.Contains("-RunLevel Highest", script);
    }

    [Fact]
    public void BuildScript_UsesWin32ComputerSystemUserName_NotQuser()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\Windows\notepad.exe", null, elevated: false);

        Assert.Contains("Win32_ComputerSystem", script);
        // Комментарий вправе упоминать quser (объясняет, почему его не вызываем) — а вот
        // самого вызова быть не должно.
        Assert.DoesNotContain("quser 2>", script);
        Assert.DoesNotContain("(quser", script);
    }

    [Fact]
    public void BuildScript_IncludesTaskNameWithSz()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\Windows\notepad.exe", null, elevated: false);

        Assert.Contains(InteractiveSessionRun.TaskName("156864"), script);
    }

    [Fact]
    public void BuildScript_PassesArgsToAction()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\Windows\explorer.exe", @"D:\", elevated: false);

        Assert.Contains("-Argument 'D:\\'", script);
    }

    [Fact]
    public void BuildScript_ChecksProcessInNonZeroSession()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\Windows\notepad.exe", null, elevated: false);

        Assert.Contains("SessionId -gt 0", script);
    }

    [Fact]
    public void BuildScript_CleansUpTaskEvenOnFailure()
    {
        var script = InteractiveSessionRun.BuildScript("156864", @"C:\Windows\notepad.exe", null, elevated: false);

        // Unregister встречается дважды: заранее (прошлый прогон мог оставить след) и после
        // проверки — задача не должна остаться на клиенте в любом исходе.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(script, "Unregister-ScheduledTask").Count);
    }
}
