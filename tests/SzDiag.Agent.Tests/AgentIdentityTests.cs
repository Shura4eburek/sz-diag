using System.Diagnostics;
using System.Security.Principal;
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>#194/бэклог п.220: агент обязан знать и сообщать hub, под кем и в какой сессии
/// он работает — на живой машине это различает «GUI работает» от «GUI ломается молча».</summary>
public class AgentIdentityTests
{
    // review W2 T-2: `catch { return "?"; }` тоже проходит IsNullOrWhiteSpace — тест был
    // зелёным и для рабочего кода, и для полностью упавшего WindowsIdentity.GetCurrent().
    // Сверяем с реальным значением напрямую — тестовый процесс на CI/боксе гарантированно
    // может получить свою Windows-идентичность.
    [Fact]
    public void CurrentUser_MatchesWindowsIdentity()
        => Assert.Equal(WindowsIdentity.GetCurrent().Name, AgentIdentity.CurrentUser());

    // review W2 T-2: `catch { return -1; }` тоже проходит `id >= 0`-ассерт — единственный,
    // что был в тесте. Сверяем с Process.GetCurrentProcess().SessionId напрямую: и рабочий
    // код, и `return 0`, оба удовлетворяли старому ассерту, но не этому.
    [Fact]
    public void CurrentSessionId_MatchesProcessSessionId()
        => Assert.Equal(Process.GetCurrentProcess().SessionId, AgentIdentity.CurrentSessionId());
}
