using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>`exec --as-system`: часть операций (задачи `UpdateOrchestrator`, ключи COMPONENTS,
/// системные ACL) упирается в Access denied даже под админом — владелец SYSTEM/TrustedInstaller
/// (бэклог п.39, СЗ 160636). Агент уже умеет поднимать транзиентную scheduled task под SYSTEM
/// для sshd (`PortableSshServer`) и для изолированных фоновых задач (`BackgroundJobs`) —
/// `SystemExecRunner` переиспользует ровно тот же механизм, но синхронно: ждёт завершения,
/// читает stdout/stderr, затем снимает задачу-обёртку.
///
/// Как и изолированные задачи, проверяем через `IPowerShellRunner`-стаб: реальную регистрацию
/// scheduled task под SYSTEM в тестах не поднимаем (нужны права, которых у тестового раннера
/// может не быть, а остаточная задача на боксе — тот же мусор, с которым борется бэклог п.56/99).</summary>
public class SystemExecRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szassystem-{Guid.NewGuid():N}");

    private sealed class RecordingPs : IPowerShellRunner
    {
        public List<string> Scripts { get; } = new();
        public Func<string, PsResult>? Handler { get; set; }

        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
        {
            Scripts.Add(script);
            return Handler?.Invoke(script) ?? new PsResult(0, "", "");
        }
    }

    private static ExecRequest Req(string script = "'ok'", int timeout = 60)
        => new("160705", Guid.NewGuid().ToString("N"), script, timeout, AsSystem: true);

    [Fact]
    public void Run_RegistersIsolatedTaskUnderSystem()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Ready|0", "") };
        var runner = new SystemExecRunner(ps, _root);

        runner.Run(Req());

        Assert.Contains(ps.Scripts, s => s.Contains("Register-ScheduledTask") && s.Contains("'SYSTEM'"));
    }

    [Fact]
    public void Run_TaskWrapperIsUnregisteredAfterCompletion()
    {
        // Критерий готовности issue #21: задача-обёртка после прогона не должна оставаться.
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Ready|0", "") };
        var runner = new SystemExecRunner(ps, _root);

        runner.Run(Req());

        Assert.Contains(ps.Scripts, s => s.Contains("Unregister-ScheduledTask"));
    }

    [Fact]
    public void Run_ReadsStdoutWrittenByWrappedScript()
    {
        var ps = new RecordingPs();
        ps.Handler = script =>
        {
            // Регистрация задачи несёт путь к script.ps1 в кавычках после `-File `,
            // соседний out.txt — куда обёртка (BackgroundJobs.BuildWrappedScript) пишет
            // вывод пользовательского скрипта.
            var marker = "-File \"";
            var start = script.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0 && script.Contains("script.ps1"))
            {
                start += marker.Length;
                var end = script.IndexOf('"', start);
                var scriptPath = script[start..end];
                var outPath = Path.Combine(Path.GetDirectoryName(scriptPath)!, "out.txt");
                File.WriteAllText(outPath, "otkljucheno\r\n");
            }
            return new PsResult(0, "Ready|0", "");
        };
        var runner = new SystemExecRunner(ps, _root);

        var result = runner.Run(Req());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("otkljucheno", result.StdOut);
    }

    [Fact]
    public void Run_NonZeroExitCode_IsSurfaced()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Ready|5", "") };
        var runner = new SystemExecRunner(ps, _root);

        var result = runner.Run(Req());

        Assert.Equal(5, result.ExitCode);
    }

    [Fact]
    public void Run_StillRunningAtDeadline_TimesOutAndStopsTask()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Running|", "") };
        var runner = new SystemExecRunner(ps, _root);

        var result = runner.Run(Req(timeout: 1));

        Assert.True(result.TimedOut);
        Assert.Contains(ps.Scripts, s => s.Contains("Stop-ScheduledTask") || s.Contains("Unregister-ScheduledTask"));
    }

    // review W2 C-2: раньше результат регистрации не проверялся (throwOnError:false, без
    // таймаута) — при неудаче (нет прав, залипший планировщик) первый Poll видел "absent" и
    // наружу уходил пустой ExecResult без единого слова о причине.
    [Fact]
    public void Run_RegistrationFails_ReturnsDescriptiveError()
    {
        var ps = new RecordingPs
        {
            Handler = script => script.Contains("Register-ScheduledTask")
                ? new PsResult(1, "", "Access is denied")
                : new PsResult(0, "absent", "")
        };
        var runner = new SystemExecRunner(ps, _root);

        var result = runner.Run(Req());

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("регистрация", result.StdErr, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Access is denied", result.StdErr);
    }

    // review W2 C-2: только что запущенная задача в состоянии "absent"/не-Running неотличима
    // от уже завершившейся — короткий скрипт мог бы отдать пустой вывод на быстрой машине
    // просто потому, что первый опрос пришёлся раньше самого старта задачи.
    [Fact]
    public void Run_AbsentRightAfterStart_IsRetried_NotTreatedAsFinished()
    {
        var pollCount = 0;
        var ps = new RecordingPs
        {
            Handler = script =>
            {
                if (script.Contains("Register-ScheduledTask")) return new PsResult(0, "", "");
                if (script.Contains("Get-ScheduledTask"))
                {
                    pollCount++;
                    return pollCount <= 2 ? new PsResult(0, "absent", "") : new PsResult(0, "Ready|0", "");
                }
                return new PsResult(0, "", "");
            }
        };
        var runner = new SystemExecRunner(ps, _root);

        var result = runner.Run(Req(timeout: 5));

        Assert.Equal(0, result.ExitCode);
        Assert.True(pollCount > 2, "должен был переопросить после первых 'absent', а не сдаться сразу");
    }

    [Fact]
    public void Run_TaskDirectoryIsCleanedUpAfterward()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Ready|0", "") };
        var runner = new SystemExecRunner(ps, _root);

        runner.Run(Req());

        Assert.False(Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any(),
            "временный каталог задачи не должен оставаться после прогона");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
