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

            if (step.Type.Equals("app", StringComparison.OrdinalIgnoreCase))
            {
                RunApp(step, steps, shots, ref shotN);
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

    /// <summary>
    /// Стресс-тест: запуск exe → держим под нагрузкой до DurationSeconds → скриншот →
    /// убить дерево процессов → опц. лог результата. Если процесс не удержался (крэш,
    /// лицензия, нет условий) — не спим таймер вхолостую, а выходим сразу и помечаем это.
    /// </summary>
    private void RunApp(TestStep step, List<TestStepResult> steps,
        Dictionary<string, byte[]> shots, ref int shotN)
    {
        var exeRel = step.Exe ?? "";
        var exe = Path.IsPathRooted(exeRel) ? exeRel : Path.Combine(AppContext.BaseDirectory, exeRel);
        var cmdLine = string.IsNullOrWhiteSpace(step.Args) ? exeRel : $"{exeRel} {step.Args}";

        if (string.IsNullOrWhiteSpace(exeRel) || !File.Exists(exe))
        {
            steps.Add(new TestStepResult(step.Name, TestStepKind.App, Command: cmdLine,
                Error: $"не найден exe: {exe}"));
            return;
        }

        var dur = step.DurationSeconds is > 0 ? step.DurationSeconds.Value : 60;
        var workDir = Path.GetDirectoryName(exe)!;
        var image = string.IsNullOrWhiteSpace(step.KillImage) ? Path.GetFileName(exe) : step.KillImage;
        var procName = Path.GetFileNameWithoutExtension(image);
        string? launchError = null;
        var earlyExit = false;
        var waited = 0;

        try
        {
            var argPart = string.IsNullOrWhiteSpace(step.Args) ? "" : $" -ArgumentList '{step.Args}'";
            _exec.Run($"Start-Process -FilePath '{exe}' -WorkingDirectory '{workDir}'{argPart}");

            // Грейс на запуск/инициализацию, дальше следим, что процесс жив.
            var grace = Math.Min(8, dur);
            Thread.Sleep(TimeSpan.FromSeconds(grace));
            waited = grace;
            while (waited < dur)
            {
                if (!IsProcessAlive(procName)) { earlyExit = true; break; }
                var chunk = Math.Min(15, dur - waited);
                Thread.Sleep(TimeSpan.FromSeconds(chunk));
                waited += chunk;
            }
        }
        catch (Exception ex) { launchError = ex.Message; }

        // Скриншот (под нагрузкой, если процесс ещё жив).
        string? shotFile = null;
        var cap = _capturer.Capture();
        if (cap.Png is not null)
        {
            shotN++;
            shotFile = $"screen-{shotN}.png";
            shots[shotFile] = cap.Png;
        }

        // Убить дерево процессов (по имени образа — переживает лаунчеры).
        _exec.Run($"taskkill /IM {image} /T /F");

        // Опц. лог результата.
        string? output = null;
        if (!string.IsNullOrWhiteSpace(step.ResultFile))
        {
            var rf = Path.IsPathRooted(step.ResultFile)
                ? step.ResultFile
                : Path.Combine(AppContext.BaseDirectory, step.ResultFile);
            try { if (File.Exists(rf)) output = File.ReadAllText(rf); } catch { /* нет лога — не критично */ }
        }

        // Ранний выход процесса — важный сигнал (крэш/лицензия), выносим в отчёт.
        if (earlyExit)
        {
            var note = $"⚠ процесс '{procName}' завершился раньше срока (~{waited}с) — " +
                       "вероятно крэш, лицензия или нет условий запуска";
            output = string.IsNullOrEmpty(output) ? note : note + "\n\n" + output;
        }

        steps.Add(new TestStepResult(step.Name, TestStepKind.App, Command: cmdLine,
            Output: output, ScreenshotFile: shotFile, Error: launchError));
    }

    /// <summary>Жив ли хоть один процесс с таким именем (без .exe).</summary>
    private bool IsProcessAlive(string procName)
    {
        var r = _exec.Run($"@(Get-Process -Name '{procName}' -ErrorAction SilentlyContinue).Count");
        return int.TryParse(r.StdOut.Trim(), out var n) && n > 0;
    }
}
