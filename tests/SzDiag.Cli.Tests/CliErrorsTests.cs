using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Под нагрузкой агент молчит — это штатная ситуация, и CLI обязан говорить о ней
/// строкой, а не 25 строками `TaskCanceledException` (бэклог п.70/78).</summary>
public class CliErrorsTests
{
    [Fact]
    public void TaskCanceled_IsExpected_AndExplainedWithoutStackTrace()
    {
        var ex = new TaskCanceledException("A task was canceled.");

        Assert.True(CliErrors.IsExpected(ex));
        var text = CliErrors.Describe(ex);
        Assert.Contains("Таймаут", text);
        Assert.Contains("нагрузкой", text);
        Assert.DoesNotContain("TaskCanceledException", text);
        Assert.NotEqual(0, CliErrors.ExitCode(ex));
    }

    [Fact]
    public void Timeout_KeepsOwnMessage()
    {
        var text = CliErrors.Describe(new TimeoutException("агент СЗ 160306 не ответил на exec"));

        Assert.Contains("агент СЗ 160306 не ответил на exec", text);
        Assert.StartsWith("Таймаут", text);
    }

    [Fact]
    public void HubUnreachable_NamesTheUrl()
    {
        var text = CliErrors.Describe(new HttpRequestException("Connection refused"), "http://127.0.0.1:5080");

        Assert.Contains("Hub недоступен", text);
        Assert.Contains("http://127.0.0.1:5080", text);
        Assert.Equal(4, CliErrors.ExitCode(new HttpRequestException("x")));
    }

    [Fact]
    public void UnexpectedException_NotSwallowed()
    {
        // Дефект CLI должен падать со стектрейсом, а не маскироваться под «агент занят».
        Assert.False(CliErrors.IsExpected(new InvalidOperationException("bug")));
    }
}
