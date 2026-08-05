using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Выполняет присланный hub'ом PowerShell-скрипт локально и формирует ответ.
/// Работает вместо SSH: без ConPTY (нет лимита на длину и на ввод), с полными правами агента
/// и без network-токена, а главное — отвечает даже когда sshd задушен нагрузкой.</summary>
public sealed class ExecCommandHandler
{
    private readonly IPowerShellRunner _ps;
    private readonly BackgroundJobs _jobs;

    public ExecCommandHandler(IPowerShellRunner ps, BackgroundJobs? jobs = null)
    {
        _ps = ps;
        _jobs = jobs ?? new BackgroundJobs();
    }

    /// <summary>Где лежат выводы фоновых задач (для сообщений оператору).</summary>
    public string JobsRoot => _jobs.Root;

    /// <summary>Состояние фоновой задачи + хвост вывода.</summary>
    public ExecJobStatus Status(ExecStatusRequest request)
    {
        try { return _jobs.Status(request); }
        catch (Exception ex)
        {
            return new ExecJobStatus(request.RequestId, request.JobId, false, null, "",
                DateTimeOffset.MinValue, 0, ex.Message);
        }
    }

    public ExecResult Handle(ExecRequest request)
    {
        // Detached: под полной нагрузкой это единственный режим, который вообще проходит —
        // агент отвечает сразу, а вывод копится в файле (бэклог п.43/п.46/п.53).
        if (request.Detached) return _jobs.Start(request);

        var timeout = TimeSpan.FromSeconds(
            request.TimeoutSeconds > 0 ? request.TimeoutSeconds : ExecLimits.DefaultTimeoutSeconds);
        try
        {
            // throwOnError: false — ненулевой код это валидный результат, а не сбой транспорта:
            // пусть вызывающий сам решает, что значит exit code его скрипта.
            var r = _ps.Run(request.Script, throwOnError: false, timeout: timeout);
            var (stdout, cutOut) = Cap(r.StdOut);
            var (stderr, cutErr) = Cap(r.StdErr);
            return new ExecResult(request.RequestId, r.ExitCode, stdout, stderr,
                TimedOut: false, Truncated: cutOut || cutErr);
        }
        catch (PowerShellTimeoutException)
        {
            return new ExecResult(request.RequestId, -1, "", "скрипт не уложился в таймаут и был остановлен",
                TimedOut: true);
        }
        catch (Exception ex)
        {
            // Любая другая ошибка запуска — тоже ответ: молчание агента выглядело бы как
            // потеря связи, и вызывающий ждал бы впустую до таймаута hub.
            return new ExecResult(request.RequestId, -1, "", ex.Message);
        }
    }

    /// <summary>Обрезает вывод до лимита, помечая факт обрезки.</summary>
    private static (string Text, bool Truncated) Cap(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ("", false);
        if (text.Length <= ExecLimits.MaxOutputChars) return (text, false);
        return (text[..ExecLimits.MaxOutputChars] + "\n… вывод обрезан …", true);
    }
}
