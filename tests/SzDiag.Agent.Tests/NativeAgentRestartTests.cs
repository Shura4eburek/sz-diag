using System.Text;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Регрессия (бэклог п.202/п.215, СЗ 161211/162367): `agent restart` ходил как
/// обычный exec-скрипт через `IPowerShellRunner`, и был бесполезен ровно тогда, когда нужен —
/// сам делил канал/очередь с задавленным exec. `NativeAgentRestart` строит независимый
/// `ProcessStartInfo` для свежего `powershell.exe`, минуя `IPowerShellRunner` целиком.</summary>
public class NativeAgentRestartTests
{
    [Fact]
    public void BuildStartInfo_LaunchesFreshPowerShellIndependently()
    {
        var psi = NativeAgentRestart.BuildStartInfo("160705");

        Assert.Equal("powershell.exe", psi.FileName);
        Assert.Contains("-EncodedCommand", psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
    }

    [Fact]
    public void BuildStartInfo_EncodedCommandDecodesToAgentRestartScript()
    {
        // Текст скрипта переиспользуется как есть — он уже собран через
        // New-ScheduledTaskAction -Execute -Argument (не schtasks /tr, который ломается на
        // путях с пробелами — бэклог п.148).
        var psi = NativeAgentRestart.BuildStartInfo("160705", delaySeconds: 45);

        var idx = psi.ArgumentList.IndexOf("-EncodedCommand");
        var encoded = psi.ArgumentList[idx + 1];
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

        Assert.Equal(AgentRestart.BuildScript("160705", 45), decoded);
    }
}
