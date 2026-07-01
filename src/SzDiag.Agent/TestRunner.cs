using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>Результат прогона: отчёт + байты скриншотов по именам файлов.</summary>
public sealed record TestRunOutput(TestReport Report, IReadOnlyDictionary<string, byte[]> Screenshots);

/// <summary>Выполняет шаги набора: команды и скриншоты. Падение шага фиксируется, прогон продолжается.</summary>
public sealed class TestRunner
{
    private readonly ICommandExecutor _exec;
    private readonly IScreenCapturer _capturer;

    public TestRunner(ICommandExecutor exec, IScreenCapturer capturer)
    {
        _exec = exec;
        _capturer = capturer;
    }

    public TestRunOutput Run(TestSuite suite, string sz, string hostname, DateTimeOffset now)
    {
        var steps = new List<TestStepResult>();
        var shots = new Dictionary<string, byte[]>();
        var shotN = 0;

        foreach (var step in suite.Steps)
        {
            if (step.Type.Equals("screenshot", StringComparison.OrdinalIgnoreCase))
            {
                var cap = _capturer.Capture();
                if (cap.Png is not null)
                {
                    shotN++;
                    var fn = $"screen-{shotN}.png";
                    shots[fn] = cap.Png;
                    steps.Add(new TestStepResult(step.Name, TestStepKind.Screenshot, ScreenshotFile: fn));
                }
                else
                {
                    steps.Add(new TestStepResult(step.Name, TestStepKind.Screenshot, Error: cap.Error ?? "неизвестно"));
                }
                continue;
            }

            // command
            try
            {
                var r = _exec.Run(step.Run ?? "");
                steps.Add(r.ExitCode == 0
                    ? new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Output: r.StdOut, ExitCode: 0)
                    : new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Error: $"код {r.ExitCode}: {r.StdErr}"));
            }
            catch (Exception ex)
            {
                steps.Add(new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Error: ex.Message));
            }
        }

        return new TestRunOutput(new TestReport(sz, hostname, now, steps), shots);
    }
}
