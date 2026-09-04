using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Выполняет присланный hub'ом PowerShell-скрипт локально и формирует ответ.
/// Работает вместо SSH: без ConPTY (нет лимита на длину и на ввод), с полными правами агента
/// и без network-токена, а главное — отвечает даже когда sshd задушен нагрузкой.</summary>
public sealed class ExecCommandHandler
{
    private readonly IPowerShellRunner _ps;
    private readonly BackgroundJobs _jobs;
    private readonly SystemExecRunner _systemExec;

    public ExecCommandHandler(IPowerShellRunner ps, BackgroundJobs? jobs = null, SystemExecRunner? systemExec = null)
    {
        _ps = ps;
        // ps прокидываем и в BackgroundJobs: изолированным (scheduled-task) фоновым задачам
        // он нужен, чтобы регистрировать/опрашивать/снимать саму задачу (бэклог п.53).
        _jobs = jobs ?? new BackgroundJobs(ps: ps);
        _systemExec = systemExec ?? new SystemExecRunner(ps);
    }

    /// <summary>Где лежат выводы фоновых задач (для сообщений оператору).</summary>
    public string JobsRoot => _jobs.Root;

    /// <summary>Сколько фоновых задач сейчас выполняется — для колонки активности (п.73).</summary>
    public int RunningJobs() => _jobs.RunningCount();

    /// <summary>Состояние фоновой задачи + хвост вывода. Этот же короткий канал везёт
    /// отмену (Cancel) и список задач (JobId «*») — он единственный, который проверенно
    /// проходит под полной нагрузкой (бэклог п.134/172/176).</summary>
    public ExecJobStatus Status(ExecStatusRequest request)
    {
        try
        {
            if (request.JobId == "*") return ListJobs(request);
            if (request.Cancel) return CancelJob(request);
            return _jobs.Status(request);
        }
        catch (Exception ex)
        {
            return new ExecJobStatus(request.RequestId, request.JobId, false, null, "",
                DateTimeOffset.MinValue, 0, ex.Message);
        }
    }

    private ExecJobStatus ListJobs(ExecStatusRequest request)
    {
        var jobs = _jobs.List();
        var text = jobs.Count == 0
            ? "фоновых задач нет"
            : string.Join("\n", jobs.Select(j =>
            {
                var state = j.Running
                    ? "выполняется"
                    : j.ExitCode is { } c ? $"завершена (exit {c})" : "не из этой жизни агента";
                // Скрипт видно СРАЗУ в списке — раньше «фоновых задач: 2» ничего не говорило о
                // том, что именно грузит машину (бэклог п.126/183, СЗ 161346).
                var script = string.IsNullOrEmpty(j.ScriptPreview) ? "" : $"\n    {j.ScriptPreview}";
                return $"{j.JobId}  {state}, старт {j.StartedAt:dd.MM HH:mm:ss}, вывода {j.OutputBytes} б{script}";
            }));
        return new ExecJobStatus(request.RequestId, "*", false, null, text, DateTimeOffset.Now, 0);
    }

    private ExecJobStatus CancelJob(ExecStatusRequest request)
    {
        var killed = _jobs.Stop(request.JobId);
        if (!killed)
        {
            // Агент мог перезапуститься — задача не в памяти, но её процесс жив: ищем
            // powershell по jobId в командной строке (путь скрипта содержит его) и валим
            // дерево процессов, иначе дочерние (OCCT, robocopy) переживут родителя.
            var script =
                "$procs = Get-CimInstance Win32_Process -Filter \"Name like '%powershell%'\" | " +
                $"Where-Object {{ $_.CommandLine -like '*{request.JobId}*' -and $_.ProcessId -ne $PID }}\n" +
                "foreach ($p in $procs) { taskkill /PID $p.ProcessId /T /F | Out-Null }\n" +
                "@($procs).Count";
            try
            {
                var r = _ps.Run(script, throwOnError: false, timeout: TimeSpan.FromSeconds(45));
                killed = int.TryParse(r.StdOut.Trim(), out var n) && n > 0;
            }
            catch { /* не нашли — статус ниже скажет, что задачи нет */ }
        }

        var status = _jobs.Status(request with { Cancel = false });
        return status with { Cancelled = killed };
    }

    public ExecResult Handle(ExecRequest request)
    {
        // Detached: под полной нагрузкой это единственный режим, который вообще проходит —
        // агент отвечает сразу, а вывод копится в файле (бэклог п.43/п.46/п.53).
        if (request.Detached) return _jobs.Start(request);

        // AsSystem (без Detached — для фона уже есть Isolated): часть операций упирается в
        // Access denied даже под админом — задачи UpdateOrchestrator, объекты
        // SYSTEM/TrustedInstaller (бэклог п.39). Гоняем синхронно транзиентной scheduled task
        // под SYSTEM и ждём результат тем же путём, что и статус изолированной фоновой задачи.
        if (request.AsSystem) return _systemExec.Run(request);

        var timeout = TimeSpan.FromSeconds(
            request.TimeoutSeconds > 0 ? request.TimeoutSeconds : ExecLimits.DefaultTimeoutSeconds);
        try
        {
            // throwOnError: false — ненулевой код это валидный результат, а не сбой транспорта:
            // пусть вызывающий сам решает, что значит exit code его скрипта.
            var r = _ps.Run(request.Script, throwOnError: false, timeout: timeout);
            var (stdout, cutOut) = Cap(SuppressCarriageReturnProgress(r.StdOut));
            var (stderr, cutErr) = Cap(SuppressCarriageReturnProgress(r.StdErr));
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

    /// <summary>Схлопывает прогресс-бары консольных утилит (`chkdsk`, `robocopy`, `xcopy` —
    /// «12 percent complete»): они пишут одну и ту же строку заново через одиночный `\r` без
    /// `\n`, и .NET читает каждую перезапись как отдельную «строку». Сотни таких перезаписей
    /// съедали лимит обрезки раньше, чем до него доходили осмысленные строки (бэклог п.181).
    /// Настоящие переводы строки (`\n`/`\r\n`) не трогаем — режем только внутристрочные `\r`,
    /// оставляя последнее состояние строки.</summary>
    private static string SuppressCarriageReturnProgress(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('\r')) return text ?? "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var idx = lines[i].LastIndexOf('\r');
            if (idx >= 0) lines[i] = lines[i][(idx + 1)..];
        }
        return string.Join("\n", lines);
    }

    /// <summary>Обрезает вывод до лимита, сохраняя голову И хвост: chkdsk кладёт вердикт в
    /// начало, а статистику — в конец; односторонняя обрезка теряла то или другое (п.181).</summary>
    private static (string Text, bool Truncated) Cap(string? text)
    {
        if (string.IsNullOrEmpty(text)) return ("", false);
        if (text.Length <= ExecLimits.MaxOutputChars) return (text, false);
        var head = ExecLimits.MaxOutputChars / 2;
        var tail = ExecLimits.MaxOutputChars - head;
        var skipped = text.Length - ExecLimits.MaxOutputChars;
        return (text[..head] + $"\n… вывод обрезан: пропущено {skipped} символов …\n" + text[^tail..],
            true);
    }
}
