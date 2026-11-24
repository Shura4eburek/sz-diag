using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>Апдейтер, собранный на соседнем hub, принимается по ExtraAgentTokens.</summary>
public class HubOptionsAgentTokenTests
{
    [Fact]
    public void Принимает_свой_и_соседские_токены()
    {
        var o = new HubOptions { AgentToken = "own", ExtraAgentTokens = ["mac", "work"] };
        Assert.True(o.IsAgentToken("own"));
        Assert.True(o.IsAgentToken("mac"));
        Assert.True(o.IsAgentToken("work"));
        Assert.False(o.IsAgentToken("other"));
    }

    [Fact]
    public void Пустой_токен_не_принимается_никогда()
    {
        var o = new HubOptions { AgentToken = "", ExtraAgentTokens = [""] };
        Assert.False(o.IsAgentToken(""));
        Assert.False(o.IsAgentToken(null));
    }
}
