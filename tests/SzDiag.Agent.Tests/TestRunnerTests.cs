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

    private sealed class RecordingExecutor : ICommandExecutor
    {
        public List<string> Commands { get; } = new();
        public CommandResult Run(string command)
        {
            Commands.Add(command);
            return new CommandResult(0, "", "");
        }
    }

    [Fact]
    public void Run_AppStep_MissingExe_RecordsErrorAndContinues()
    {
        var runner = new TestRunner(new RecordingExecutor(), new FakeCapturer(new ScreenCapture(null, "n/a")));
        var suite = new TestSuite { Steps = new[]
        {
            new TestStep("app", "TM5", Exe: "tools\\tm5\\нет-такого.exe", DurationSeconds: 1),
        } };

        var output = runner.Run(suite, "156864", "PC-1", At);

        var step = output.Report.Steps.Single();
        Assert.Equal(TestStepKind.App, step.Kind);
        Assert.Contains("не найден", step.Error);
    }

    [Fact]
    public void Run_AppStep_LaunchesCapturesAndKillsProcessTree()
    {
        var exe = Path.GetTempFileName();   // реальный файл → пройдёт File.Exists
        try
        {
            var png = new byte[] { 9, 8, 7 };
            var exec = new RecordingExecutor();
            var runner = new TestRunner(exec, new FakeCapturer(new ScreenCapture(png, null)));
            var suite = new TestSuite { Steps = new[]
            {
                new TestStep("app", "Стресс", Exe: exe, Args: "/run", DurationSeconds: 1, KillImage: "stress.exe"),
            } };

            var output = runner.Run(suite, "156864", "PC-1", At);

            var step = output.Report.Steps.Single();
            Assert.Equal(TestStepKind.App, step.Kind);
            Assert.Null(step.Error);
            Assert.Equal("screen-1.png", step.ScreenshotFile);
            Assert.Equal(png, output.Screenshots["screen-1.png"]);
            Assert.Contains(exec.Commands, c => c.StartsWith("Start-Process") && c.Contains("/run"));
            Assert.Contains(exec.Commands, c => c == "taskkill /IM stress.exe /T /F");
        }
        finally { File.Delete(exe); }
    }

    [Fact]
    public void Run_AppStep_RunToCompletion_CleanExit_NoErrorCapturesArtifact_NoKill()
    {
        var exe = Path.GetTempFileName();
        var artifact = Path.GetTempFileName();
        var artifactBytes = new byte[] { 4, 2, 4, 2 };
        File.WriteAllBytes(artifact, artifactBytes);
        try
        {
            var png = new byte[] { 5, 5 };
            var exec = new RecordingExecutor();   // IsProcessAlive -> "" -> процесс не жив (самозавершился)
            var runner = new TestRunner(exec, new FakeCapturer(new ScreenCapture(png, null)), initialGraceSeconds: 0);
            var suite = new TestSuite { Steps = new[]
            {
                new TestStep("app", "OCCT", Exe: exe, Args: "test", DurationSeconds: 5,
                    KillImage: "occtcmd.exe", RunToCompletion: true, ArtifactFile: artifact),
            } };

            var output = runner.Run(suite, "156864", "PC-1", At);

            var step = output.Report.Steps.Single();
            Assert.Null(step.Error);
            Assert.Null(step.Output);                                  // ранний выход = норма, без warning
            Assert.Equal(Path.GetFileName(artifact), step.ArtifactFile);
            Assert.Equal(artifactBytes, output.Artifacts[Path.GetFileName(artifact)]);
            Assert.Equal("screen-1.png", step.ScreenshotFile);
            Assert.DoesNotContain(exec.Commands, c => c.StartsWith("taskkill")); // сам закрылся — не убиваем
        }
        finally { File.Delete(exe); File.Delete(artifact); }
    }

    [Fact]
    public void Run_AppStep_SubstitutesWorkdirTokenInArgs()
    {
        var exe = Path.GetTempFileName();
        try
        {
            var workDir = Path.GetDirectoryName(exe)!;
            var exec = new RecordingExecutor();
            var runner = new TestRunner(exec, new FakeCapturer(new ScreenCapture(new byte[] { 1 }, null)),
                initialGraceSeconds: 0);
            var suite = new TestSuite { Steps = new[]
            {
                new TestStep("app", "OCCT", Exe: exe, Args: "--report=\"{workdir}\\r.html\"", DurationSeconds: 1),
            } };

            var output = runner.Run(suite, "156864", "PC-1", At);

            var step = output.Report.Steps.Single();
            Assert.DoesNotContain("{workdir}", step.Command);
            Assert.Contains(workDir, step.Command);
            Assert.Contains(exec.Commands, c => c.StartsWith("Start-Process") && c.Contains(workDir) && !c.Contains("{workdir}"));
        }
        finally { File.Delete(exe); }
    }
}
