using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class TestSuiteTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"suite-{Guid.NewGuid():N}.json");

    [Fact]
    public void Load_ParsesStepsInOrder()
    {
        File.WriteAllText(_path, """
            { "steps": [
              { "type": "command", "name": "Система", "run": "systeminfo" },
              { "type": "screenshot", "name": "Экран" }
            ] }
            """);

        var suite = TestSuite.Load(_path);

        Assert.Equal(2, suite.Steps.Count);
        Assert.Equal("command", suite.Steps[0].Type);
        Assert.Equal("systeminfo", suite.Steps[0].Run);
        Assert.Equal("screenshot", suite.Steps[1].Type);
        Assert.Null(suite.Steps[1].Run);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
