using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class ExecCommandHandlerTests
{
    private sealed class StubPs : IPowerShellRunner
    {
        private readonly PsResult? _result;
        private readonly Exception? _throw;
        public string? LastScript { get; private set; }
        public TimeSpan? LastTimeout { get; private set; }
        public bool? LastThrowOnError { get; private set; }

        public StubPs(PsResult result) => _result = result;
        public StubPs(Exception ex) => _throw = ex;

        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
        {
            LastScript = script;
            LastTimeout = timeout;
            LastThrowOnError = throwOnError;
            if (_throw is not null) throw _throw;
            return _result!;
        }
    }

    private static ExecRequest Req(string script = "Get-Date", int timeout = 30)
        => new("160306", "req-1", script, timeout);

    [Fact]
    public void Handle_ReturnsOutputAndExitCode()
    {
        var handler = new ExecCommandHandler(new StubPs(new PsResult(0, "28.07.2026", "")));

        var r = handler.Handle(Req());

        Assert.Equal("req-1", r.RequestId);
        Assert.Equal(0, r.ExitCode);
        Assert.Equal("28.07.2026", r.StdOut);
        Assert.False(r.TimedOut);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void Handle_NonZeroExitCode_IsResultNotFailure()
    {
        // Ненулевой код — валидный ответ скрипта, а не сбой транспорта: не должен бросать.
        var ps = new StubPs(new PsResult(3, "", "команда не найдена"));
        var handler = new ExecCommandHandler(ps);

        var r = handler.Handle(Req());

        Assert.Equal(3, r.ExitCode);
        Assert.Equal("команда не найдена", r.StdErr);
        Assert.False(ps.LastThrowOnError!.Value);
    }

    [Fact]
    public void Handle_PassesScriptAndTimeoutToPowerShell()
    {
        var ps = new StubPs(new PsResult(0, "ok", ""));

        new ExecCommandHandler(ps).Handle(Req("whoami", timeout: 45));

        Assert.Equal("whoami", ps.LastScript);
        Assert.Equal(TimeSpan.FromSeconds(45), ps.LastTimeout);
    }

    [Fact]
    public void Handle_ZeroTimeout_FallsBackToDefault()
    {
        var ps = new StubPs(new PsResult(0, "ok", ""));

        new ExecCommandHandler(ps).Handle(Req(timeout: 0));

        Assert.Equal(TimeSpan.FromSeconds(ExecLimits.DefaultTimeoutSeconds), ps.LastTimeout);
    }

    [Fact]
    public void Handle_HugeOutput_IsTruncatedAndFlagged()
    {
        var huge = new string('x', ExecLimits.MaxOutputChars + 5_000);
        var handler = new ExecCommandHandler(new StubPs(new PsResult(0, huge, "")));

        var r = handler.Handle(Req());

        Assert.True(r.Truncated);
        Assert.True(r.StdOut.Length < huge.Length);
        Assert.Contains("обрезан", r.StdOut);
    }

    [Fact]
    public void Handle_Timeout_ReportsTimedOutInsteadOfThrowing()
    {
        var handler = new ExecCommandHandler(new StubPs(new PowerShellTimeoutException("убит")));

        var r = handler.Handle(Req());

        Assert.True(r.TimedOut);
        Assert.Equal("req-1", r.RequestId);
        Assert.NotEqual(0, r.ExitCode);
    }

    [Fact]
    public void Handle_UnexpectedError_ReturnsAnswerNotSilence()
    {
        // Молчание агента выглядело бы как потеря связи, и вызывающий ждал бы впустую.
        var handler = new ExecCommandHandler(new StubPs(new InvalidOperationException("powershell.exe не найден")));

        var r = handler.Handle(Req());

        Assert.Equal("req-1", r.RequestId);
        Assert.Contains("powershell.exe не найден", r.StdErr);
    }
}
