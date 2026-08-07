using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class TestReportRunnerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        public CommandResult Run(string command) => new(0, "OK", "");
    }
    private sealed class FakeCapturer : IScreenCapturer
    {
        public ScreenCapture Capture() => new(new byte[] { 9 }, null);
    }
    private sealed class CapturingLink : IHubLink
    {
        public List<UploadReportPart> Uploaded { get; } = new();
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null, string? lastShutdown = null, CancellationToken ct = default) => Task.CompletedTask;
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
        public List<(string sz, string activity, DateTimeOffset? since)> Activities { get; } = new();
        public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
        {
            Activities.Add((sz, activity, since));
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task RunAndUpload_UploadsReportMdAndScreenshotsWithSameTimestamp()
    {
        var suite = new TestSuite { Steps = new[]
        {
            new TestStep("command", "Система", "systeminfo"),
            new TestStep("screenshot", "Экран"),
        } };
        var runner = new TestRunner(new FakeExecutor(), new FakeCapturer());
        var link = new CapturingLink();
        var reportRunner = new TestReportRunner(runner, suite, link, "PC-1",
            () => new DateTimeOffset(2026, 7, 1, 12, 30, 0, TimeSpan.Zero));

        await reportRunner.RunAndUploadAsync("156864");

        Assert.Contains(link.Uploaded, u => u.FileName == "report.md");
        Assert.Contains(link.Uploaded, u => u.FileName == "screen-1.png");
        Assert.All(link.Uploaded, u => Assert.Equal("156864", u.Sz));
        var ts = link.Uploaded.Select(u => u.Timestamp).Distinct().Single();
        Assert.Equal("20260701-123000", ts);
    }

    private static TestStep App(string id) => new("app", id.ToUpperInvariant(), Id: id, Exe: "x");

    [Fact]
    public void FilterSteps_NullFilter_ReturnsAll()
    {
        var steps = new[] { App("occt"), App("tm5") };
        Assert.Equal(2, TestReportRunner.FilterSteps(steps, null).Count);
    }

    [Fact]
    public void FilterSteps_ById_CaseInsensitive()
    {
        var steps = new[] { App("occt"), App("tm5") };
        Assert.Equal("occt", Assert.Single(TestReportRunner.FilterSteps(steps, "OCCT")).Id);
    }

    [Fact]
    public void FilterSteps_Multiple_ReturnsSubset()
    {
        var steps = new[] { App("occt"), App("tm5"), App("furmark") };
        Assert.Equal(2, TestReportRunner.FilterSteps(steps, "tm5,furmark").Count);
    }

    [Fact]
    public void FilterSteps_UnknownId_Empty()
        => Assert.Empty(TestReportRunner.FilterSteps(new[] { App("occt") }, "nope"));

    [Fact]
    public void AvailableIds_ReturnsOnlyNonEmpty()
    {
        var steps = new TestStep[] { new("command", "Система", "systeminfo"), App("occt") };
        Assert.Equal(new[] { "occt" }, TestReportRunner.AvailableIds(steps));
    }

    [Fact]
    public async Task RunAndUpload_UnknownFilter_DoesNotRunOrUpload()
    {
        var suite = new TestSuite { Steps = new[] { App("occt") } };
        var link = new CapturingLink();
        var reportRunner = new TestReportRunner(
            new TestRunner(new FakeExecutor(), new FakeCapturer()), suite, link, "PC-1",
            () => new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero));

        var outcome = await reportRunner.RunAndUploadAsync("156864", "nope");

        Assert.False(outcome.Ran);
        Assert.Empty(link.Uploaded);
        Assert.Contains("occt", outcome.AvailableIds);
    }
}
