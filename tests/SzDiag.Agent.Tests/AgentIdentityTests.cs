using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>#194/бэклог п.220: агент обязан знать и сообщать hub, под кем и в какой сессии
/// он работает — на живой машине это различает «GUI работает» от «GUI ломается молча».</summary>
public class AgentIdentityTests
{
    [Fact]
    public void CurrentUser_ReturnsNonEmptyString()
        => Assert.False(string.IsNullOrWhiteSpace(AgentIdentity.CurrentUser()));

    [Fact]
    public void CurrentSessionId_ReturnsNonNegativeOnNormalProcess()
    {
        // Тестовый процесс сам обычно живёт в интерактивной сессии (не 0) — но проверяем
        // только то, что метод не бросает и возвращает осмысленное значение.
        var id = AgentIdentity.CurrentSessionId();
        Assert.True(id >= 0, $"ожидали неотрицательный SessionId, получили {id}");
    }
}
