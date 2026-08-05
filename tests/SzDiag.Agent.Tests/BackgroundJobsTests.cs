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
