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

    [Fact]
    public void HttpErrorWithStatusCode_IsNotDescribedAsHubUnavailable()
    {
        // Регрессия (бэклог п.212, СЗ 161498): `--result ""` уходил в hub без jobId и получал
        // 405 (Method Not Allowed) — hub был полностью жив, но CLI рендерил это как
        // «Hub недоступен», уводя диагностику не туда. 4xx/5xx с явным StatusCode — это ОТВЕТ
        // от живого hub, а не недоступность транспорта.
        var ex = new HttpRequestException("Response status code does not indicate success: 405 (Method Not Allowed).",
            null, System.Net.HttpStatusCode.MethodNotAllowed);

        var text = CliErrors.Describe(ex, "http://127.0.0.1:5080");

        Assert.DoesNotContain("Hub недоступен", text);
        Assert.Contains("405", text);
        Assert.NotEqual(CliErrors.ExitCode(new HttpRequestException("x")), CliErrors.ExitCode(ex));
    }

    [Fact]
    public void HttpErrorWithoutStatusCode_StaysHubUnavailable()
    {
        // Настоящий обрыв транспорта (connection refused и т.п.) не несёт StatusCode —
        // такие остаются «Hub недоступен», как и раньше.
        var text = CliErrors.Describe(new HttpRequestException("Connection refused"), "http://127.0.0.1:5080");

        Assert.Contains("Hub недоступен", text);
    }
}
