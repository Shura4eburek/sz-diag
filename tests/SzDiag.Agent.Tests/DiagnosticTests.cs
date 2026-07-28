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
            "temps", "drivers", "events", "reboots", "whea", "reliability", "battery"
        };
        Assert.Equal(expected, DiagnosticProbes.Sections);
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
