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
        public Task RegisterAsync(string sz, string hostname, CancellationToken ct = default) => Task.CompletedTask;
        public Task HeartbeatAsync(string sz, CancellationToken ct = default) => Task.CompletedTask;
        public void OnRevert(Func<string, Task> handler) { }
        public void OnRunTests(Func<string, Task> handler) { }
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
}
