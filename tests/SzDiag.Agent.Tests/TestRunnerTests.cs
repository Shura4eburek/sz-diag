using SzDiag.Agent;
using SzDiag.Kb;
using Xunit;

namespace SzDiag.Agent.Tests;

public class TestRunnerTests
{
    private sealed class FakeExecutor : ICommandExecutor
    {
        private readonly Dictionary<string, CommandResult> _map;
        public FakeExecutor(Dictionary<string, CommandResult> map) => _map = map;
        public CommandResult Run(string command)
            => _map.TryGetValue(command, out var r) ? r : new CommandResult(0, "", "");
    }

    private sealed class FakeCapturer : IScreenCapturer
    {
        private readonly ScreenCapture _result;
        public FakeCapturer(ScreenCapture result) => _result = result;
        public ScreenCapture Capture() => _result;
    }

    private static readonly DateTimeOffset At = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Run_CommandStep_CapturesOutput()
    {
        var runner = new TestRunner(
            new FakeExecutor(new() { ["systeminfo"] = new CommandResult(0, "OS: Win", "") }),
            new FakeCapturer(new ScreenCapture(null, "n/a")));
        var suite = new TestSuite { Steps = new[] { new TestStep("command", "Система", "systeminfo") } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        var step = output.Report.Steps.Single();
        Assert.Equal(TestStepKind.Command, step.Kind);
        Assert.Equal("OS: Win", step.Output);
        Assert.Equal(0, step.ExitCode);
    }

    [Fact]
    public void Run_FailedCommand_RecordsErrorAndContinues()
    {
        var runner = new TestRunner(
            new FakeExecutor(new() { ["bad"] = new CommandResult(1, "", "не найдено") }),
            new FakeCapturer(new ScreenCapture(null, "n/a")));
        var suite = new TestSuite { Steps = new[]
        {
            new TestStep("command", "Плохая", "bad"),
            new TestStep("command", "Хорошая", "ok"),
        } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        Assert.Equal(2, output.Report.Steps.Count);
        Assert.Contains("не найдено", output.Report.Steps[0].Error);
        Assert.Null(output.Report.Steps[1].Error);
    }

    [Fact]
    public void Run_ScreenshotStep_StoresPngAndAssignsFileName()
    {
        var png = new byte[] { 1, 2, 3 };
        var runner = new TestRunner(
            new FakeExecutor(new()),
            new FakeCapturer(new ScreenCapture(png, null)));
        var suite = new TestSuite { Steps = new[] { new TestStep("screenshot", "Экран") } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        var step = output.Report.Steps.Single();
        Assert.Equal("screen-1.png", step.ScreenshotFile);
        Assert.Equal(png, output.Screenshots["screen-1.png"]);
    }

    [Fact]
    public void Run_ScreenshotUnavailable_RecordsError()
    {
        var runner = new TestRunner(
            new FakeExecutor(new()),
            new FakeCapturer(new ScreenCapture(null, "нет сессии")));
        var suite = new TestSuite { Steps = new[] { new TestStep("screenshot", "Экран") } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        Assert.Equal("нет сессии", output.Report.Steps.Single().Error);
        Assert.Empty(output.Screenshots);
    }
}
