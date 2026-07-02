using SzDiag.Kb;

namespace SzDiag.Agent;

/// <summary>
/// Результат прогона: отчёт + байты скриншотов и артефактов по именам файлов.
/// Артефакты — произвольные файлы, которые произвёл тул (напр. HTML-отчёт OCCT):
/// заливаются на hub так же, как скриншоты, но в report.md идут ссылкой, а не встраиваются.
/// </summary>
public sealed record TestRunOutput(
    TestReport Report,
    IReadOnlyDictionary<string, byte[]> Screenshots,
    IReadOnlyDictionary<string, byte[]> Artifacts);

/// <summary>Выполняет шаги набора: команды и скриншоты. Падение шага фиксируется, прогон продолжается.</summary>
public sealed class TestRunner
{
    private readonly ICommandExecutor _exec;
    private readonly IScreenCapturer _capturer;
    private readonly int _initialGraceSeconds;

    /// <param name="initialGraceSeconds">
    /// Пауза после старта exe до первой проверки «жив ли процесс» — даёт стресс-тулам
    /// подняться, прежде чем считать ранний выход крэшем. В тестах ставится в 0.
    /// </param>
    public TestRunner(ICommandExecutor exec, IScreenCapturer capturer, int initialGraceSeconds = 8)
    {
        _exec = exec;
        _capturer = capturer;
        _initialGraceSeconds = initialGraceSeconds;
    }

    public TestRunOutput Run(TestSuite suite, string sz, string hostname, DateTimeOffset now)
    {
        var steps = new List<TestStepResult>();
        var shots = new Dictionary<string, byte[]>();
        var artifacts = new Dictionary<string, byte[]>();
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
                RunApp(step, steps, shots, artifacts, ref shotN);
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

        return new TestRunOutput(new TestReport(sz, hostname, now, steps), shots, artifacts);
    }

    /// <summary>
    /// App-шаг в двух режимах.
    /// Стресс (RunToCompletion=false): запуск exe → держим под нагрузкой до DurationSeconds
    /// → скриншот → kill дерева. Ранний выход процесса (крэш/лицензия) — сигнал в отчёт.
    /// До завершения (RunToCompletion=true): запуск → скриншот в процессе → ждём
    /// самозавершения до DurationSeconds (предохранитель) → опц. kill если завис. Ранний
    /// выход — норма (тул отработал расписание и закрылся), не помечаем ошибкой.
    /// Опц. ResultFile встраивается в отчёт; ArtifactFile прикладывается ссылкой + на hub.
    /// </summary>
    private void RunApp(TestStep step, List<TestStepResult> steps,
        Dictionary<string, byte[]> shots, Dictionary<string, byte[]> artifacts, ref int shotN)
    {
        var exeRel = step.Exe ?? "";
        var exe = Path.IsPathRooted(exeRel) ? exeRel : Path.Combine(AppContext.BaseDirectory, exeRel);
        var workDir = Path.GetDirectoryName(exe)!;
        // Подстановка {workdir} в аргументах → абсолютный каталог exe (пути к schedule/report).
        var args = (step.Args ?? "").Replace("{workdir}", workDir);
        var cmdLine = string.IsNullOrWhiteSpace(args) ? exeRel : $"{exeRel} {args}";

        if (string.IsNullOrWhiteSpace(exeRel) || !File.Exists(exe))
        {
            steps.Add(new TestStepResult(step.Name, TestStepKind.App, Command: cmdLine,
                Error: $"не найден exe: {exe}"));
            return;
        }

        var dur = step.DurationSeconds is > 0 ? step.DurationSeconds.Value : 60;
        var image = string.IsNullOrWhiteSpace(step.KillImage) ? Path.GetFileName(exe) : step.KillImage;
        var procName = Path.GetFileNameWithoutExtension(image);
        string? launchError = null;
        var earlyExit = false;
        var timedOut = false;
        var waited = 0;
        string? shotFile = null;

        try
        {
            var argPart = string.IsNullOrWhiteSpace(args) ? "" : $" -ArgumentList '{args}'";
            _exec.Run($"Start-Process -FilePath '{exe}' -WorkingDirectory '{workDir}'{argPart}");

            // Грейс на запуск/инициализацию.
            var grace = Math.Min(_initialGraceSeconds, dur);
            if (grace > 0) Thread.Sleep(TimeSpan.FromSeconds(grace));
            waited = grace;

            // В режиме до-завершения снимаем скриншот раньше (пока тул работает),
            // т.к. после самозакрытия на экране уже рабочий стол.
            if (step.RunToCompletion)
                shotFile = Capture(shots, ref shotN);

            while (waited < dur)
            {
                if (!IsProcessAlive(procName)) { earlyExit = true; break; }
                var chunk = Math.Min(15, dur - waited);
                Thread.Sleep(TimeSpan.FromSeconds(chunk));
                waited += chunk;
            }
            if (!earlyExit) timedOut = true;   // досидели до предела, процесс ещё жив
        }
        catch (Exception ex) { launchError = ex.Message; }

        // Скриншот под нагрузкой для стресс-режима (в конце окна ожидания).
        if (!step.RunToCompletion)
            shotFile = Capture(shots, ref shotN);

        // Стресс-режим всегда убивает дерево; режим до-завершения — только если завис (таймаут).
        if (!step.RunToCompletion || timedOut)
            _exec.Run($"taskkill /IM {image} /T /F");

        // Опц. текст-лог результата (встраивается в отчёт).
        string? output = null;
        var resolvedResult = Resolve(step.ResultFile);
        if (resolvedResult is not null)
            try { if (File.Exists(resolvedResult)) output = File.ReadAllText(resolvedResult); } catch { /* нет лога — не критично */ }

        // Опц. файл-артефакт (напр. HTML-отчёт OCCT): заливается на hub, в отчёте — ссылкой.
        string? artifactName = null;
        var resolvedArtifact = Resolve(step.ArtifactFile);
        if (resolvedArtifact is not null && File.Exists(resolvedArtifact))
        {
            try
            {
                artifactName = Path.GetFileName(resolvedArtifact);
                artifacts[artifactName] = File.ReadAllBytes(resolvedArtifact);
            }
            catch { artifactName = null; /* не смогли прочитать — не критично */ }
        }

        // Ранний выход: для стресс-режима это сигнал (крэш/лицензия); для до-завершения — норма.
        if (earlyExit && !step.RunToCompletion)
        {
            var note = $"⚠ процесс '{procName}' завершился раньше срока (~{waited}с) — " +
                       "вероятно крэш, лицензия или нет условий запуска";
            output = string.IsNullOrEmpty(output) ? note : note + "\n\n" + output;
        }
        // До-завершения, но упёрлись в предохранитель — тоже сигнал (тул не закрылся сам).
        if (timedOut && step.RunToCompletion)
        {
            var note = $"⚠ процесс '{procName}' не завершился за {dur}с — убит по таймауту";
            output = string.IsNullOrEmpty(output) ? note : note + "\n\n" + output;
        }

        steps.Add(new TestStepResult(step.Name, TestStepKind.App, Command: cmdLine,
            Output: output, ScreenshotFile: shotFile, ArtifactFile: artifactName, Error: launchError));
    }

    /// <summary>Снимок экрана в словарь скриншотов; возвращает имя файла или null.</summary>
    private string? Capture(Dictionary<string, byte[]> shots, ref int shotN)
    {
        var cap = _capturer.Capture();
        if (cap.Png is null) return null;
        shotN++;
        var fn = $"screen-{shotN}.png";
        shots[fn] = cap.Png;
        return fn;
    }

    /// <summary>Относительный путь резолвится рядом с exe агента; null → null.</summary>
    private static string? Resolve(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        return Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
    }

    /// <summary>Жив ли хоть один процесс с таким именем (без .exe).</summary>
    private bool IsProcessAlive(string procName)
    {
        var r = _exec.Run($"@(Get-Process -Name '{procName}' -ErrorAction SilentlyContinue).Count");
        return int.TryParse(r.StdOut.Trim(), out var n) && n > 0;
    }
}
