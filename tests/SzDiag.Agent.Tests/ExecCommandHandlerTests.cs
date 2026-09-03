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
    public void Handle_HugeOutput_KeepsHeadAndTail()
    {
        // Регрессия (бэклог п.181): chkdsk кладёт вердикт в НАЧАЛО вывода, а статистику —
        // в конец. Обрезка только с одной стороны теряет либо то, либо другое.
        var huge = "ВЕРДИКТ-В-НАЧАЛЕ\n" + new string('x', ExecLimits.MaxOutputChars + 5_000)
            + "\nИТОГ-В-КОНЦЕ";
        var handler = new ExecCommandHandler(new StubPs(new PsResult(0, huge, "")));

        var r = handler.Handle(Req());

        Assert.True(r.Truncated);
        Assert.Contains("ВЕРДИКТ-В-НАЧАЛЕ", r.StdOut);
        Assert.Contains("ИТОГ-В-КОНЦЕ", r.StdOut);
        Assert.Contains("пропущено", r.StdOut);
    }

    [Fact]
    public async Task Status_WithCancel_KillsRunningJob()
    {
        // Снять улетевшую задачу — одной командой, по тому же короткому каналу, который
        // проходит под нагрузкой (бэклог п.134/172/176).
        var root = Path.Combine(Path.GetTempPath(), $"szexec-{Guid.NewGuid():N}");
        try
        {
            var jobs = new BackgroundJobs(root);
            var handler = new ExecCommandHandler(new StubPs(new PsResult(0, "", "")), jobs);
            var started = jobs.Start(new ExecRequest("160306", "r1", "Start-Sleep -Seconds 120", 60,
                Detached: true));

            var st = handler.Status(new ExecStatusRequest("160306", "r2", started.JobId!, 10,
                Cancel: true));
            Assert.True(st.Cancelled, "ответ обязан подтверждать отмену");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            ExecJobStatus after;
            do
            {
                after = handler.Status(new ExecStatusRequest("160306", "r3", started.JobId!, 10));
                if (!after.Running) break;
                await Task.Delay(200);
            } while (DateTime.UtcNow < deadline);
            Assert.False(after.Running, "процесс задачи должен быть убит");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Status_CancelUnknownJob_FallsBackToProcessSearch()
    {
        // Агент мог перезапуститься — задача не в памяти, но её процесс жив. Ищем по jobId
        // в командной строке процесса и валим дерево.
        var ps = new StubPs(new PsResult(0, "", ""));
        var handler = new ExecCommandHandler(ps,
            new BackgroundJobs(Path.Combine(Path.GetTempPath(), $"szexec-{Guid.NewGuid():N}")));

        handler.Status(new ExecStatusRequest("160306", "r", "20260801-000000-abcdef", 10,
            Cancel: true));

        Assert.NotNull(ps.LastScript);
        Assert.Contains("20260801-000000-abcdef", ps.LastScript);
    }

    [Fact]
    public void Status_JobsListRequest_ReturnsSummaryOfJobs()
    {
        // JobId «*» — список задач: сейчас, чтобы узнать, что крутится на машине, надо
        // помнить jobId из прошлой сессии (бэклог п.134).
        var root = Path.Combine(Path.GetTempPath(), $"szexec-{Guid.NewGuid():N}");
        try
        {
            var jobs = new BackgroundJobs(root);
            var handler = new ExecCommandHandler(new StubPs(new PsResult(0, "", "")), jobs);
            var started = jobs.Start(new ExecRequest("160306", "r1", "Start-Sleep -Seconds 60", 60,
                Detached: true));

            var st = handler.Status(new ExecStatusRequest("160306", "r2", "*", 10));

            Assert.Null(st.Error);
            Assert.Contains(started.JobId!, st.Tail);
            jobs.Stop(started.JobId!);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Handle_ProgressBarCarriageReturns_CollapsedToFinalState()
    {
        // Регрессия (бэклог п.181): `chkdsk`/`robocopy` дописывают одну строку через голый
        // `\r` без `\n` — раньше все промежуточные проценты съедали лимит обрезки раньше,
        // чем до неё доходили осмысленные строки. Реальный перевод строки трогать нельзя.
        var progress = "0 percent complete.\r10 percent complete.\r100 percent complete.\nВЕРДИКТ\n";
        var handler = new ExecCommandHandler(new StubPs(new PsResult(0, progress, "")));

        var r = handler.Handle(Req());

        Assert.DoesNotContain("0 percent complete.\r10", r.StdOut);
        Assert.Contains("100 percent complete.", r.StdOut);
        Assert.Contains("ВЕРДИКТ", r.StdOut);
    }

    [Fact]
    public void Handle_ProgressBarCarriageReturns_DoesNotBreakRealNewlines()
    {
        var handler = new ExecCommandHandler(new StubPs(new PsResult(0, "line1\r\nline2\r\nline3", "")));

        var r = handler.Handle(Req());

        Assert.Equal("line1\nline2\nline3", r.StdOut);
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
