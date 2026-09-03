using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Фоновые exec-задачи: под полной нагрузкой это единственный режим, который
/// проходит, а длинный прогон перестаёт быть чёрным ящиком (бэклог п.43/п.46/п.53).</summary>
public class BackgroundJobsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szjobs-{Guid.NewGuid():N}");

    private BackgroundJobs Jobs => new(_root);

    private static ExecRequest Req(string script)
        => new("160705", Guid.NewGuid().ToString("N"), script, 60, Detached: true);

    private static async Task<ExecJobStatus> WaitUntilAsync(BackgroundJobs jobs, string jobId,
        Func<ExecJobStatus, bool> until, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        ExecJobStatus status;
        do
        {
            status = jobs.Status(new ExecStatusRequest("160705", "r", jobId, 20));
            if (until(status)) return status;
            await Task.Delay(200);
        } while (DateTime.UtcNow < deadline);
        return status;
    }

    [Fact]
    public void Start_ReturnsImmediatelyWithJobId()
    {
        // Смысл режима: ответ приходит сразу, не дожидаясь скрипта.
        var jobs = Jobs;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var result = jobs.Start(Req("Start-Sleep -Seconds 30"));

        sw.Stop();
        Assert.NotNull(result.JobId);
        Assert.Equal(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"должен вернуться сразу, а не ждать скрипт ({sw.Elapsed})");
        jobs.Stop(result.JobId!);
    }

    [Fact]
    public async Task Status_WhileRunning_ReportsRunningAndTail()
    {
        // «Шо там» во время часового прогона: хвост читается, пока задача идёт.
        var jobs = Jobs;
        var job = jobs.Start(Req("1..20 | ForEach-Object { \"tick $_\"; Start-Sleep -Milliseconds 300 }"));

        var status = await WaitUntilAsync(jobs, job.JobId!, s => s.Tail.Contains("tick 1"));

        Assert.True(status.Running, "задача должна ещё выполняться");
        Assert.Contains("tick 1", status.Tail);
        Assert.True(status.OutputBytes > 0);
        jobs.Stop(job.JobId!);
    }

    [Fact]
    public async Task Status_AfterCompletion_ReportsExitCodeAndFullTail()
    {
        var jobs = Jobs;
        var job = jobs.Start(Req("'gotovo'; exit 3"));

        var status = await WaitUntilAsync(jobs, job.JobId!, s => !s.Running);

        Assert.False(status.Running);
        Assert.Equal(3, status.ExitCode);
        Assert.Contains("gotovo", status.Tail);
    }

    [Fact]
    public async Task Output_IsWrittenLineByLine_SoItSurvivesHardPowerLoss()
    {
        // Буферизованный stdout при вырубоне теряется целиком — ровно так `nvidia-smi -f`
        // оставлял пустой файл (п.20). Проверяем, что строки на диске ДО завершения задачи.
        var jobs = Jobs;
        var job = jobs.Start(Req("'pervaya'; Start-Sleep -Seconds 20"));

        await WaitUntilAsync(jobs, job.JobId!, s => s.Tail.Contains("pervaya"));
        var onDisk = File.ReadAllText(Path.Combine(_root, job.JobId!, "out.txt"));

        Assert.Contains("pervaya", onDisk);
        jobs.Stop(job.JobId!);
    }

    [Fact]
    public async Task Start_ParseErrorInScript_SurfacesErrorText()
    {
        // Регрессия (бэклог п.177): опечатка в рецепте валила скрипт ДО первой строки,
        // задача «завершалась» с exit 1 и нулём байт вывода — неотличимо от упавшего агента.
        // Parse-ошибка обязана доехать до оператора текстом.
        var jobs = Jobs;
        var job = jobs.Start(Req("$key: = 'x'\n'never-runs'"));

        var status = await WaitUntilAsync(jobs, job.JobId!,
            s => !s.Running && !string.IsNullOrEmpty(s.Error));

        Assert.False(status.Running);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        Assert.NotEqual(0, status.ExitCode);
        Assert.DoesNotContain("never-runs", status.Tail);
    }

    [Fact]
    public async Task List_ShowsRunningAndFinishedJobs()
    {
        // «Что вообще крутится на машине» — без запоминания jobId из прошлой сессии (п.134/176).
        var jobs = Jobs;
        var running = jobs.Start(Req("Start-Sleep -Seconds 60"));
        var finished = jobs.Start(Req("'done'"));
        await WaitUntilAsync(jobs, finished.JobId!, s => !s.Running);

        var list = jobs.List();

        Assert.Contains(list, j => j.JobId == running.JobId && j.Running);
        Assert.Contains(list, j => j.JobId == finished.JobId && !j.Running);
        jobs.Stop(running.JobId!);
    }

    [Fact]
    public void Status_UnknownJob_ReturnsErrorNotThrow()
    {
        var status = Jobs.Status(new ExecStatusRequest("160705", "r", "нет-такой-задачи", 10));

        Assert.NotNull(status.Error);
        Assert.False(status.Running);
    }

    [Fact]
    public async Task Stop_KillsRunningJob()
    {
        var jobs = Jobs;
        var job = jobs.Start(Req("Start-Sleep -Seconds 120"));

        Assert.True(jobs.Stop(job.JobId!));

        var status = await WaitUntilAsync(jobs, job.JobId!, s => !s.Running, seconds: 15);
        Assert.False(status.Running);
    }

    [Fact]
    public async Task Tail_LimitsLinesButKeepsLatest()
    {
        var jobs = Jobs;
        var job = jobs.Start(Req("1..50 | ForEach-Object { \"line $_\" }"));

        await WaitUntilAsync(jobs, job.JobId!, s => !s.Running);
        var status = jobs.Status(new ExecStatusRequest("160705", "r", job.JobId!, 5));

        var lines = status.Tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length <= 5, $"хвост должен быть урезан, а не {lines.Length} строк");
        Assert.Contains("line 50", status.Tail);
        Assert.DoesNotContain("line 1\n", status.Tail);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}

/// <summary>Изолированные (scheduled-task) фоновые задачи — бэклог п.53: `szcli exec --detach`
/// раньше оборачивал долгий прогон в дочерний процесс агента, и он пропадал вместе с ним
/// (на живой заявке TM5 исчез посреди прогона без единой строки результата). Проверяем чисто
/// через `IPowerShellRunner`-стаб: реальную регистрацию scheduled task под SYSTEM в тестах не
/// поднимаем — она требует прав, которых у тестового раннера может не быть, а остаточная
/// задача на боксе — ровно тот мусор, с которым борется п.56/99.</summary>
public class IsolatedBackgroundJobsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"szjobs-iso-{Guid.NewGuid():N}");

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

    private static ExecRequest Req(string sz = "160705", string script = "'ok'")
        => new(sz, Guid.NewGuid().ToString("N"), script, 60, Detached: true, Isolated: true);

    [Fact]
    public void Start_Isolated_RegistersScheduledTaskAndWritesMarker()
    {
        var ps = new RecordingPs();
        var jobs = new BackgroundJobs(_root, ps);

        var result = jobs.Start(Req());

        Assert.NotNull(result.JobId);
        Assert.Contains(ps.Scripts, s => s.Contains("Register-ScheduledTask") && s.Contains("Start-ScheduledTask"));
        var marker = Path.Combine(_root, result.JobId!, "task.txt");
        Assert.True(File.Exists(marker), "маркер задачи должен лечь на диск — иначе после рестарта агента статус не найти");
        Assert.Contains("160705", File.ReadAllText(marker));
        Assert.Contains("изолированно", result.StdOut);
    }

    [Fact]
    public void Start_IsolatedWithoutRunner_FallsBackToChildProcess()
    {
        // Без IPowerShellRunner изолировать нечем — откатываемся к обычному дочернему
        // процессу вместо падения задачи целиком, но честно предупреждаем в выводе.
        var jobs = new BackgroundJobs(_root);

        var result = jobs.Start(Req());

        Assert.NotNull(result.JobId);
        Assert.Contains("изоляция недоступна", result.StdOut);
        jobs.Stop(result.JobId!);
    }

    [Fact]
    public void Status_IsolatedJob_Running_ReportsRunningTrue()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Running|", "") };
        var jobs = new BackgroundJobs(_root, ps);
        var started = jobs.Start(Req());

        var status = jobs.Status(new ExecStatusRequest("160705", "r", started.JobId!, 10));

        Assert.True(status.Running);
        Assert.Null(status.ExitCode);
    }

    [Fact]
    public void Status_IsolatedJob_Finished_ReportsExitCode()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Ready|3", "") };
        var jobs = new BackgroundJobs(_root, ps);
        var started = jobs.Start(Req());

        var status = jobs.Status(new ExecStatusRequest("160705", "r", started.JobId!, 10));

        Assert.False(status.Running);
        Assert.Equal(3, status.ExitCode);
    }

    [Fact]
    public void Status_IsolatedJob_TaskAbsent_ReportsNotRunning()
    {
        // Задачу сняли (ручной cancel, ребут) — не должно выглядеть как «ещё выполняется».
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "absent", "") };
        var jobs = new BackgroundJobs(_root, ps);
        var started = jobs.Start(Req());

        var status = jobs.Status(new ExecStatusRequest("160705", "r", started.JobId!, 10));

        Assert.False(status.Running);
    }

    [Fact]
    public void Status_IsolatedJob_SurvivesAgentRestart_ViaTaskMarker()
    {
        // Агент перезапустился: задача не в памяти, но task.txt на диске находит её снова —
        // ровно то, чего не хватало у обычных (дочерних) фоновых задач для реального
        // переживания краха агента.
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Running|", "") };
        var started = new BackgroundJobs(_root, ps).Start(Req());

        var reopened = new BackgroundJobs(_root, ps);
        var status = reopened.Status(new ExecStatusRequest("160705", "r", started.JobId!, 10));

        Assert.True(status.Running);
    }

    [Fact]
    public void Stop_IsolatedJob_StopsAndUnregistersTask()
    {
        var ps = new RecordingPs { Handler = _ => new PsResult(0, "Ready|0", "") };
        var jobs = new BackgroundJobs(_root, ps);
        var started = jobs.Start(Req());

        var stopped = jobs.Stop(started.JobId!);

        Assert.True(stopped);
        Assert.Contains(ps.Scripts, s => s.Contains("Unregister-ScheduledTask") && s.Contains("Stop-ScheduledTask"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }
}
