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

    public TestRunOutput Run(TestSuite suite, string sz, string hostname, DateTimeOffset now,
        Action<TestStep>? onStep = null)
    {
        var steps = new List<TestStepResult>();
        var shots = new Dictionary<string, byte[]>();
        var artifacts = new Dictionary<string, byte[]>();
        var shotN = 0;

        foreach (var step in suite.Steps)
        {
            onStep?.Invoke(step);

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
                // Ненулевой код НЕ повод выбрасывать stdout: на 161312 секция «История сбоев»
                // вернула `код 1:` с пустым stderr — и весь собранный вывод пропал, хотя ответ
                // «дампов нет» в нём был (бэклог п.74). Печатаем и то, и другое.
                steps.Add(r.ExitCode == 0
                    ? new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run, Output: r.StdOut, ExitCode: 0)
                    : new TestStepResult(step.Name, TestStepKind.Command, Command: step.Run,
                        Output: r.StdOut, ExitCode: r.ExitCode,
                        Error: $"код {r.ExitCode}: {DescribeStdErr(r.StdErr)}"));
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

            // В режиме до-завершения без детектора окна снимаем скриншот раньше (пока тул
            // работает) — с детектором скриншот снимаем ровно в момент появления окна.
            if (step.RunToCompletion && step.CompletionWindowClass is null)
                shotFile = Capture(shots, ref shotN);

            while (waited < dur)
            {
                var done = step.CompletionWindowClass is not null
                    ? HasCompletionWindow(procName, step.CompletionWindowClass)
                    : !IsProcessAlive(procName);
                if (done)
                {
                    earlyExit = true;
                    if (step.CompletionWindowClass is not null)
                        shotFile = Capture(shots, ref shotN);
                    break;
                }
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

        // Самозакрывшийся тул (RunToCompletion без детектора окна, чистый ранний выход) —
        // единственный случай, когда процесса уже нет и убивать нечего. Во всех остальных
        // (стресс, таймаут, детектор окна — тул сам не закрылся, просто показал модалку) — kill.
        var selfClosed = earlyExit && step.CompletionWindowClass is null;
        if (!step.RunToCompletion || !selfClosed)
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

    /// <summary>Максимум stderr в отчёте: полный текст CLIXML-простыни читать невозможно,
    /// а первые пара тысяч символов почти всегда содержат сам диагноз.</summary>
    private const int StdErrLimit = 2000;

    /// <summary>Человеческая расшифровка stderr упавшей секции. Пустой stderr — тоже факт:
    /// «код 1 и молчание» означает, что упала не команда, а сам PowerShell (бэклог п.74).</summary>
    public static string DescribeStdErr(string? stderr)
    {
        var text = (stderr ?? "").Trim();
        if (text.Length == 0)
            return "stderr пуст (PowerShell вернул ненулевой код без сообщения — смотри вывод ниже)";
        return text.Length <= StdErrLimit ? text : text[..StdErrLimit] + $"… (ещё {text.Length - StdErrLimit} символов)";
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

    /// <summary>
    /// Есть ли у процесса procName видимое top-level окно с Win32-классом windowClass
    /// (напр. "#32770" — стандартный MessageBox). Нужно тулам, которые не завершают
    /// процесс сами, а виснут на модалке "готово" (TM5) — EnumWindows точнее заголовка
    /// окна, т.к. у модалки он совпадает с заголовком главного окна.
    /// </summary>
    private bool HasCompletionWindow(string procName, string windowClass)
    {
        var script = $$"""
            $sig = @'
            using System;
            using System.Runtime.InteropServices;
            using System.Text;
            public class SzDiagWin32 {
                public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
                [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
                [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
                [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
                [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
            }
            '@
            Add-Type -TypeDefinition $sig -ErrorAction SilentlyContinue
            $targetPids = @((Get-Process -Name '{{procName}}' -ErrorAction SilentlyContinue).Id)
            $found = $false
            $cb = {
                param($hWnd, $lParam)
                $procId = 0
                [SzDiagWin32]::GetWindowThreadProcessId($hWnd, [ref]$procId) | Out-Null
                if (($targetPids -contains $procId) -and [SzDiagWin32]::IsWindowVisible($hWnd)) {
                    $sb = New-Object System.Text.StringBuilder 256
                    [SzDiagWin32]::GetClassName($hWnd, $sb, 256) | Out-Null
                    if ($sb.ToString() -eq '{{windowClass}}') { $script:found = $true }
                }
                return $true
            }
            [SzDiagWin32]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
            $found
            """;
        var r = _exec.Run(script);
        return r.StdOut.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }
}
