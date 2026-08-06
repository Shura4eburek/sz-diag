using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class DiagnosticProbesTests
{
    [Fact]
    public void Suite_HasExpectedSections_ExceptNetworkAndSecurity()
    {
        var expected = new[]
        {
            "system", "cpu", "memory", "gpu", "storage",
            "temps", "drivers", "events", "reboots", "whea", "livekernel", "reliability", "battery"
        };
        Assert.Equal(expected, DiagnosticProbes.Sections);
        // Каталог проб и словарь для валидации в CLI обязаны совпадать: иначе szcli либо
        // отвергнет живую секцию, либо пропустит несуществующую (бэклог п.6).
        Assert.Equal(DiagSections.All, DiagnosticProbes.Sections);
        Assert.DoesNotContain("network", DiagnosticProbes.Sections);
        Assert.DoesNotContain("security", DiagnosticProbes.Sections);
    }

    [Fact]
    public void Suite_AllStepsAreCommandProbes_WithIdAndRun()
    {
        Assert.All(DiagnosticProbes.Suite.Steps, s =>
        {
            Assert.Equal("command", s.Type);
            Assert.False(string.IsNullOrWhiteSpace(s.Id));
            Assert.False(string.IsNullOrWhiteSpace(s.Run));
        });
    }

    [Fact]
    public void RebootsProbe_ReadsFullHistoryAndDecodesBugchecks()
    {
        // Регрессия (бэклог п.61/13): секция резала Kernel-Power 41 до 20 записей и печатала
        // BugcheckCode десятичным. Итог, дата установки ОС и hex-имена — обязательны.
        var run = DiagnosticProbes.Suite.Steps.Single(s => s.Id == "reboots").Run!;

        Assert.DoesNotContain("Id=41 } -MaxEvents", run);   // историю берём целиком
        Assert.Contains("TOTAL:", run);                      // общее число событий
        Assert.Contains("per-day histogram", run);           // деградация видна по дням
        Assert.Contains("OS installed", run);                // «дефект с первого дня»
        Assert.Contains("Fmt-Bug", run);                     // hex + имя стоп-кода
        Assert.Contains("'190'='ATTEMPTED_WRITE_TO_READONLY_MEMORY'", run);
    }

    private static string Body(string section)
        => DiagnosticProbes.Suite.Steps.Single(s => s.Id == section).Run!;

    [Fact]
    public void WheaProbe_AggregatesByApicBankAndDate()
    {
        // Регрессия (п.44): 276 событий печатались 40 строками без APIC ID и без общего
        // числа — локализация дефекта на одном ядре была невидима.
        var run = Body("whea");

        Assert.DoesNotContain("-MaxEvents 40", run);
        Assert.Contains("TOTAL:", run);
        Assert.Contains("by APIC ID", run);
        Assert.Contains("odnom fizicheskom yadre", run);   // маркер «все ошибки на одном ядре»
        Assert.Contains("by MCA bank", run);
        Assert.Contains("by date", run);
    }

    [Fact]
    public void WheaProbe_DecodesMcaFieldsAndFlags()
    {
        // Регрессия (п.18): MciStat/ErrorType приходилось доставать отдельным exec из XML.
        var run = Body("whea");

        Assert.Contains("MciStat", run);
        Assert.Contains("Cache Hierarchy Error", run);      // ErrorType=9, а не 8 (см. п.18)
        Assert.Contains("'10'='Bus/Interconnect Error'", run);
        Assert.Contains("PCC", run);
        Assert.Contains("UC(neispravimaya)", run);
    }

    [Fact]
    public void LiveKernelProbe_LooksWhereTdrDumpsActuallyLand()
    {
        // Регрессия (п.37): по заявке «вылетает игра» отчёт говорил «всё чисто», а рядом
        // лежали 14 WATCHDOG-дампов и LiveKernelEvent 0x141.
        var run = Body("livekernel");

        Assert.Contains(@"C:\Windows\LiveKernelReports", run);
        Assert.Contains("LiveKernelEvent", run);
        Assert.Contains("VIDEO_ENGINE_TIMEOUT_DETECTED", run);   // P1=141
        Assert.Contains("VIDEO_TDR_TIMEOUT_DETECTED", run);      // P1=117
        Assert.Contains("SOVPADAET s LiveKernelEvent", run);     // сшивка с крашем приложения
    }

    [Fact]
    public void ReliabilityProbe_SaysNoMinidumpsIsNotNoKernelCrashes()
    {
        var run = Body("reliability");

        Assert.Contains("!= 'net sboev yadra'", run);
        Assert.Contains("LiveKernelReports", run);
        Assert.Contains("volmgr", run);   // дампы могут не писаться в принципе (п.14)
    }

    [Fact]
    public void ReliabilityProbe_ReportsCrashControlAndSurvivesMissingSources()
    {
        // Регрессия (п.74): секция валилась целиком с `ошибка: код 1:` без подробностей, а
        // без CrashDumpEnabled ответ «дампов нет» неинтерпретируем — не пишутся вовсе или
        // BSOD не было.
        var run = Body("reliability");

        Assert.Contains("CrashDumpEnabled", run);
        Assert.Contains("AutoReboot", run);
        Assert.Contains("MEMORY.DMP", run);
        Assert.Contains("Win32_ReliabilityRecords -ErrorAction Stop", run);   // с обработкой, а не молча
        Assert.Contains("exit 0", run);                                       // «дампов нет» — не ошибка
    }

    [Fact]
    public void StorageProbe_MapsPagefileToPhysicalDiskAndSplitsUncorrectable()
    {
        // Регрессия (п.27): «ReadErrors: 393» выглядело как шум, хотя все 393 неисправимы,
        // а связь «pagefile на умирающем HDD → 0x154» не собиралась вовсе.
        var run = Body("storage");

        Assert.Contains("Win32_PageFileUsage", run);
        Assert.Contains("ReadErrorsUncorrected", run);
        Assert.Contains("WriteErrorsUncorrected", run);
        Assert.Contains("VERDICT", run);
        Assert.Contains("SUSPECT", run);
        Assert.Contains("Bukva -> fizicheskiy disk", run);
        Assert.Contains("UNEXPECTED_STORE_EXCEPTION", run);
        Assert.Contains("NE 'diski zdorovy'", run);   // пустые счётчики ≠ здоровые диски
    }

    [Fact]
    public void EventsProbe_CountsPerIdAndReadsRareIdsWithoutLimit()
    {
        // Регрессия (п.31): общий MaxEvents на смеси шумных и редких Id съел Kernel-Power 41.
        var run = Body("events");

        Assert.Contains("Schetchiki po Id", run);
        Assert.Contains("Redkie kritichnye Id - BEZ limita", run);
        Assert.Contains("Kernel-Power 41", run);
        Assert.Contains("yavnyy nol", run);   // отсутствие событий печатается явным нулём
    }

    [Fact]
    public void ProbesQueryingOptionalProviders_WrapCallsInTryCatch()
    {
        // Живая грабля: незарегистрированный ProviderName валит Get-WinEvent с
        // 'The parameter is incorrect' ДАЖЕ при -ErrorAction SilentlyContinue.
        foreach (var section in new[] { "livekernel", "storage" })
        {
            var run = Body(section);
            Assert.DoesNotContain("ProviderName=$p } -ErrorAction SilentlyContinue", run);
            Assert.DoesNotContain("ProviderName=$prov } -ErrorAction SilentlyContinue", run);
            Assert.Contains("catch { }", run);
        }
    }

    [Fact]
    public void AllProbeBodies_AreAscii()
    {
        // Тела проб — строго ASCII (см. комментарий класса): русские заголовки живут в Name.
        foreach (var step in DiagnosticProbes.Suite.Steps)
            Assert.All(step.Run!, c => Assert.True(c < 128, $"не-ASCII в секции {step.Id}: {c}"));
    }

    [Fact]
    public void Suite_SectionIdsAreUnique()
    {
        var ids = DiagnosticProbes.Suite.Steps.Select(s => s.Id!).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}

public class DiagReportRunnerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        public CommandResult Run(string command) => new(0, "OK", "");
    }
    private sealed class FakeCapturer : IScreenCapturer
    {
        public ScreenCapture Capture() => new(new byte[] { 1 }, null);
    }
    private sealed class CapturingLink : IHubLink
    {
        public List<UploadReportPart> Uploaded { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) => Task.CompletedTask;
        public void OnRevert(Func<string, Task> handler) { }
        public void OnRunTests(Func<string, string?, Task> handler) { }
        public void OnRunDiag(Func<string, string?, Task> handler) { }
        public Func<SzDiag.Contracts.ExecRequest, Task>? ExecHandler { get; private set; }
        public List<SzDiag.Contracts.ExecResult> ExecResults { get; } = new();
        public void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler) => ExecHandler = handler;
        public Task SendExecResultAsync(SzDiag.Contracts.ExecResult result, CancellationToken ct = default) { ExecResults.Add(result); return Task.CompletedTask; }
        public Task SendExecAckAsync(SzDiag.Contracts.ExecAck ack, CancellationToken ct = default) => Task.CompletedTask;
        public void OnExecStatus(Func<SzDiag.Contracts.ExecStatusRequest, Task> handler) { }
        public Task SendExecJobStatusAsync(SzDiag.Contracts.ExecJobStatus status, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPush(Func<SzDiag.Contracts.PushRequest, Task> handler) { }
        public Task SendPushResultAsync(SzDiag.Contracts.PushResult result, CancellationToken ct = default) => Task.CompletedTask;
        public void OnPull(Func<SzDiag.Contracts.PullRequest, Task> handler) { }
        public Task SendPullChunkAsync(SzDiag.Contracts.PullChunk chunk, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPullResultAsync(SzDiag.Contracts.PullResult result, CancellationToken ct = default) => Task.CompletedTask;
        public Task UploadReportFileAsync(UploadReportPart part, CancellationToken ct = default)
        {
            Uploaded.Add(part);
            return Task.CompletedTask;
        }
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
            => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DiagReportRunner Make(CapturingLink link) => new(
        new TestRunner(new FakeExecutor(), new FakeCapturer()),
        DiagnosticProbes.Suite, link, "PC-1",
        () => new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task RunAndUpload_Full_UploadsSingleDiagMd()
    {
        var link = new CapturingLink();
        var outcome = await Make(link).RunAndUploadAsync("156864");

        Assert.True(outcome.Ran);
        var part = Assert.Single(link.Uploaded);
        Assert.Equal("diag.md", part.FileName);
        Assert.Equal("156864", part.Sz);
        Assert.Equal("20260721-100000", part.Timestamp);
    }

    [Fact]
    public async Task RunAndUpload_Sections_ReportContainsOnlySelected()
    {
        var link = new CapturingLink();
        await Make(link).RunAndUploadAsync("156864", "storage,gpu");

        var md = System.Text.Encoding.UTF8.GetString(Assert.Single(link.Uploaded).Content);
        Assert.Contains("Диски", md);        // storage
        Assert.Contains("Видеокарта", md);   // gpu
        Assert.DoesNotContain("Процессор", md); // cpu не выбран
    }

    [Fact]
    public async Task RunAndUpload_UnknownSection_DoesNotRunOrUpload()
    {
        var link = new CapturingLink();
        var outcome = await Make(link).RunAndUploadAsync("156864", "nope");

        Assert.False(outcome.Ran);
        Assert.Empty(link.Uploaded);
        Assert.Contains("storage", outcome.AvailableSections);
    }
}
