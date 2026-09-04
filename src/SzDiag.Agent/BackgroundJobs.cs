using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Фоновые exec-задачи на клиенте.
///
/// Пять заявок подряд во время самого главного — идёт ли нагрузка, что с железом —
/// наблюдать было нечем: синхронный exec под OCCT не проходит вообще (три попытки подряд
/// в таймаут при живом heartbeat), а долгий exec держит единственный канал и не отдаёт
/// вывод до самого конца (бэклог п.43/п.46/п.25).
///
/// Здесь задача запускается **detached**, вывод пишется **в файл построчно** — то есть
/// переживает жёсткий вырубон, ровно как самодельный наблюдатель-CSV, который приходилось
/// городить руками (п.64). Хвост читается коротким запросом в любой момент.</summary>
/// <summary>Краткая сводка по фоновой задаче для списка `szcli exec --jobs`.</summary>
/// <param name="ScriptPreview">Первая непустая строка присланного скрипта — раньше `list`/
/// `exec --jobs` показывал только счётчик («фоновых задач: 2») без единого намёка, ЧТО
/// именно грузит машину: на 161346 остановленный оператором OCCT молчал, а фоновая задача
/// с диск-стрессом продолжала давить систему ещё 180 минут никем не опознанной (бэклог
/// п.126/183).</param>
public sealed record ExecJobSummary(string JobId, bool Running, int? ExitCode,
    DateTimeOffset StartedAt, long OutputBytes, string? ScriptPreview = null);

public sealed class BackgroundJobs
{
    /// <summary>Process — обычный (детский) фон; TaskName — изолированная задача под SYSTEM
    /// (см. Start(ExecRequest) с Isolated: true) — дерево процессов не привязано к агенту.</summary>
    private sealed record Job(string Id, Process? Process, string OutPath, DateTimeOffset StartedAt,
        string? TaskName = null);

    private readonly string _root;
    private readonly IPowerShellRunner? _ps;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="root">Куда складывать вывод задач. По умолчанию — вне папки агента,
    /// чтобы логи прогонов не уезжали в OneDrive клиента (см. ToolsDirectory / п.63).</param>
    /// <param name="ps">Нужен только для изолированных (scheduled-task) задач: регистрация,
    /// опрос состояния и снятие идут через PowerShell, а не через .NET Process. Без него
    /// запрос с Isolated: true молча откатывается в обычный дочерний процесс.</param>
    public BackgroundJobs(string? root = null, IPowerShellRunner? ps = null)
    {
        _root = root ?? ClientTraces.JobsRoot;
        _ps = ps;
    }

    public string Root => _root;

    /// <summary>Имя транзиентной scheduled task для изолированной фоновой задачи — по тому же
    /// правилу `szdiag-<роль>-<СЗ>`, что и sshd/watchdog: `client cleanup` находит и снимает
    /// её как любую другую нашу задачу без отдельного кода (бэклог п.99).</summary>
    private static string IsolatedTaskName(string sz, string jobId) => $"szdiag-job-{sz}-{jobId}";

    /// <summary>Запускает скрипт в фоне и сразу возвращает идентификатор задачи.</summary>
    public ExecResult Start(ExecRequest request)
    {
        var jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_root, jobId);
        try
        {
            Directory.CreateDirectory(dir);
            var scriptPath = Path.Combine(dir, "script.ps1");
            var userPath = Path.Combine(dir, "user.ps1");
            var outPath = Path.Combine(dir, "out.txt");
            var errPath = Path.Combine(dir, "err.txt");

            // Скрипт кладём файлом и просим PowerShell дописывать вывод построчно: при
            // вырубоне посреди прогона всё уже на диске (буферизованный stdout теряется —
            // ровно так `nvidia-smi -f` оставлял пустой файл, п.20).
            //
            // Пользовательский скрипт — ОТДЕЛЬНЫМ файлом: parse-ошибка в нём валит скрипт
            // до первой строки и не ловится try/catch внутри него самого, но вызов файла
            // через & превращает её в перехватываемое исключение обёртки — текст ошибки
            // уезжает в err.txt, а не теряется вместе со stderr процесса (бэклог п.177).
            File.WriteAllText(userPath, request.Script, new UTF8Encoding(true));
            File.WriteAllText(scriptPath, BuildWrappedScript(userPath, outPath, errPath), new UTF8Encoding(true));

            // Isolated: задача уходит транзиентной scheduled task под SYSTEM (как sshd) —
            // дерево процессов не дочернее агенту, падение/закрытие агента его не утащит
            // (на живой заявке TM5 пропал вместе с упавшим агентом, не досчитав ни одного
            // цикла — бэклог п.53). Без `_ps` откатываемся в обычный дочерний процесс: он
            // всё ещё переживает штатное завершение агента (родитель не убивает детей сам
            // по себе), просто не переживает крах консоли/сессии целиком.
            string isolationNote = "";
            if (request.Isolated && _ps is not null)
            {
                var taskName = IsolatedTaskName(request.Sz, jobId);
                string? registerError = null;
                try
                {
                    // throwOnError:false + явный таймаут (Important-6, ревью волны 1): без
                    // прав на регистрацию `Register-ScheduledTask` кидает исключение, и без
                    // таймаута регистрация может залипнуть, держа единственный exec-канал,
                    // который проходит под нагрузкой. task.txt пишем ТОЛЬКО после успеха —
                    // раньше маркер ложился на диск заранее, и Status/Stop по фантомной
                    // задаче уходили в ветку изолированной вместо честного «нет такой».
                    var reg = _ps.Run(BuildRegisterIsolatedJobCommand(taskName, scriptPath, dir),
                        throwOnError: false, timeout: TimeSpan.FromSeconds(60));
                    if (reg.ExitCode == 0)
                    {
                        File.WriteAllText(Path.Combine(dir, "task.txt"), taskName, new UTF8Encoding(false));
                        _jobs[jobId] = new Job(jobId, null, outPath, DateTimeOffset.Now, taskName);
                        return new ExecResult(request.RequestId, 0,
                            $"задача запущена изолированно (task {taskName}): {jobId}\nвывод: {outPath}", "", JobId: jobId);
                    }
                    registerError = $"код {reg.ExitCode}" + (string.IsNullOrWhiteSpace(reg.StdErr) ? "" : $": {reg.StdErr.Trim()}");
                }
                catch (Exception ex)
                {
                    registerError = ex.Message;
                }
                isolationNote = $" (изоляция не удалась ({registerError}) — обычный дочерний процесс)";
            }
            else if (request.Isolated)
            {
                isolationNote = " (изоляция недоступна: агент не передал IPowerShellRunner — обычный дочерний процесс)";
            }

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = dir,
            };
            var process = Process.Start(psi)!;
            // Под 100% CPU дочерний процесс не получает квантов — на приоритете ниже обычного
            // мониторинг просто не идёт (п.43).
            try { process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

            _jobs[jobId] = new Job(jobId, process, outPath, DateTimeOffset.Now);
            return new ExecResult(request.RequestId, 0,
                $"задача запущена в фоне: {jobId}\nвывод: {outPath}{isolationNote}", "", JobId: jobId);
        }
        catch (Exception ex)
        {
            return new ExecResult(request.RequestId, -1, "", ex.Message);
        }
    }

    /// <summary>Оборачивает пользовательский скрипт (лежит отдельным файлом в <paramref
    /// name="userPath"/>) так, чтобы вывод писался построчно на диск и parse-ошибка не терялась
    /// вместе со stderr процесса (бэклог п.177/п.20). Общий и для обычных фоновых задач, и для
    /// синхронного запуска под SYSTEM (<see cref="SystemExecRunner"/>) — оба живут в одном и том
    /// же временном каталоге и опрашиваются одинаково.</summary>
    public static string BuildWrappedScript(string userPath, string outPath, string errPath) =>
        new StringBuilder()
            .AppendLine("$ErrorActionPreference='Continue'")
            .AppendLine("$ProgressPreference='SilentlyContinue'")
            .AppendLine("try {")
            .AppendLine("  & '" + userPath.Replace("'", "''") + "' *>&1 | ForEach-Object { $_ | Out-File -FilePath '" + outPath.Replace("'", "''") + "' -Append -Encoding utf8 }")
            .AppendLine("  exit $LASTEXITCODE")
            .AppendLine("} catch {")
            .AppendLine("  $_ | Out-String | Out-File -FilePath '" + errPath.Replace("'", "''") + "' -Encoding utf8")
            .AppendLine("  exit 199")
            .AppendLine("}")
            .ToString();

    /// <summary>PowerShell для регистрации+запуска изолированной фоновой задачи транзиентной
    /// scheduled task под SYSTEM. Тот же паттерн, что у sshd (<see cref="PortableSshServer"/>):
    /// `-MultipleInstances IgnoreNew` и безлимитный `ExecutionTimeLimit` — тайминг решает вызывающий
    /// уровень (hub/пользователь), не сама задача.</summary>
    public static string BuildRegisterIsolatedJobCommand(string taskName, string scriptPath, string workDir)
    {
        // Апостроф в пути (`C:\Users\O'Brien\...`) без удвоения обрывает PS-литерал —
        // тот же приём, что уже стоит в BuildStopIsolatedJobCommand (Important-7, ревью
        // волны 1).
        var script = scriptPath.Replace("'", "''");
        var dir = workDir.Replace("'", "''");
        var task = taskName.Replace("'", "''");
        return "$a = New-ScheduledTaskAction -Execute 'powershell.exe' " +
            $"-Argument '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"' " +
            $"-WorkingDirectory '{dir}'; " +
            "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
            "-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
            $"Register-ScheduledTask -TaskName '{task}' -Action $a -Settings $s " +
            "-RunLevel Highest -User 'SYSTEM' -Force | Out-Null; " +
            $"Start-ScheduledTask -TaskName '{task}'";
    }

    /// <summary>PowerShell для опроса состояния изолированной задачи: `State` («Running»/«Ready»
    /// после однократного прогона) + `LastTaskResult` (код возврата, когда уже не Running).
    /// «absent» — задачу сняли (ручной cancel, ребут) или её никогда не было.</summary>
    public static string BuildQueryIsolatedJobCommand(string taskName)
    {
        var task = taskName.Replace("'", "''");
        return $"$t = Get-ScheduledTask -TaskName '{task}' -ErrorAction SilentlyContinue; " +
            "if (-not $t) { 'absent' } else { " +
            $"$i = Get-ScheduledTaskInfo -TaskName '{task}' -ErrorAction SilentlyContinue; " +
            "$t.State.ToString() + '|' + $i.LastTaskResult }";
    }

    /// <summary>PowerShell для снятия изолированной задачи: остановить + разрегистрировать
    /// саму scheduled task и добить дерево процессов по jobId в командной строке (задача не
    /// убивает дочерние процессы автоматически при Stop-ScheduledTask).</summary>
    public static string BuildStopIsolatedJobCommand(string taskName, string jobId)
    {
        var jid = jobId.Replace("'", "''");
        return $"Stop-ScheduledTask -TaskName '{taskName}' -ErrorAction SilentlyContinue; " +
               $"Unregister-ScheduledTask -TaskName '{taskName}' -Confirm:$false -ErrorAction SilentlyContinue; " +
               "Get-CimInstance Win32_Process -Filter \"Name like '%powershell%'\" -ErrorAction SilentlyContinue | " +
               $"Where-Object {{ $_.CommandLine -like '*{jid}*' }} | " +
               "ForEach-Object { taskkill /PID $_.ProcessId /T /F | Out-Null }";
    }

    /// <summary>Имя scheduled task изолированной задачи, если она была отмечена таковой при
    /// старте (в памяти) или маркер сохранился на диске (агент мог перезапуститься с тех пор —
    /// задача от этого не пропадает, она живёт в планировщике независимо от агента).</summary>
    private string? ResolveIsolatedTaskName(string jobId, Job? job)
    {
        if (job?.TaskName is { } tn) return tn;
        var marker = Path.Combine(_root, jobId, "task.txt");
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    /// <summary>Состояние задачи + хвост вывода.</summary>
    public ExecJobStatus Status(ExecStatusRequest request)
    {
        var dir = Path.Combine(_root, request.JobId);
        var outPath = Path.Combine(dir, "out.txt");
        // Задача может пережить перезапуск агента (процесс независимый), поэтому опираемся
        // на файлы, а не только на список в памяти.
        _jobs.TryGetValue(request.JobId, out var job);

        if (job is null && !Directory.Exists(dir))
            return new ExecJobStatus(request.RequestId, request.JobId, false, null, "",
                DateTimeOffset.MinValue, 0, $"нет такой задачи: {request.JobId}");

        var running = false;
        int? exitCode = null;
        var taskName = ResolveIsolatedTaskName(request.JobId, job);
        if (taskName is not null && _ps is not null)
        {
            // Изолированная задача: состояние процесса Windows знает сама, через планировщик —
            // переживает и рестарт агента (маркер task.txt это и обеспечивает).
            try
            {
                var r = _ps.Run(BuildQueryIsolatedJobCommand(taskName), throwOnError: false);
                var text = (r.StdOut ?? "").Trim();
                if (!text.Equals("absent", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = text.Split('|');
                    running = parts.Length > 0 && parts[0].Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
                    if (!running && parts.Length > 1 && int.TryParse(parts[1].Trim(), out var er)) exitCode = er;
                }
                // "absent" — задачу сняли (cancel/ребут): считаем завершённой, exitCode неизвестен.
            }
            catch { /* планировщик недоступен прямо сейчас — вывод из файла всё равно покажем */ }
        }
        else if (job?.Process is { } proc)
        {
            try
            {
                running = !proc.HasExited;
                if (!running) exitCode = proc.ExitCode;
            }
            catch { running = false; }
        }

        var (tail, size) = ReadTail(outPath, request.TailLines);
        var started = job?.StartedAt
            ?? (Directory.Exists(dir) ? new DirectoryInfo(dir).CreationTime : DateTime.Now);

        // Когда out.txt последний раз дописывался: молчащий файл во время «выполняется»
        // неотличим на глаз от зависшего скрипта — «работает медленно» от «встало намертво»
        // отличить нечем было, пока не появился этот таймстамп (бэклог п.208).
        DateTimeOffset? lastOutputAt = null;
        try { if (File.Exists(outPath)) lastOutputAt = new DateTimeOffset(File.GetLastWriteTimeUtc(outPath), TimeSpan.Zero); }
        catch { /* пишется прямо сейчас — покажем в следующий раз */ }

        // Parse-ошибка скрипта лежит в err.txt (см. Start): без неё «завершена (exit 199),
        // вывода 0 б» неотличима от упавшего агента или задавленной машины (п.177).
        string? error = null;
        var errPath = Path.Combine(dir, "err.txt");
        if (File.Exists(errPath))
        {
            try
            {
                error = File.ReadAllText(errPath, Encoding.UTF8).Trim();
                if (error.Length > 4000) error = error[..4000] + "\n… обрезано …";
                if (error.Length == 0) error = null;
            }
            catch { /* пишется прямо сейчас — покажем в следующий раз */ }
        }

        return new ExecJobStatus(request.RequestId, request.JobId, running, exitCode, tail,
            started, size, error, LastOutputAt: lastOutputAt);
    }

    /// <summary>Список всех фоновых задач: живые из памяти + завершённые/осиротевшие с диска.
    /// Чтобы узнать, что крутится на машине, не нужно помнить jobId из прошлой сессии (п.134/176).</summary>
    public IReadOnlyList<ExecJobSummary> List()
    {
        var result = new Dictionary<string, ExecJobSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in _jobs.Values)
        {
            var running = false;
            int? exitCode = null;
            if (job.Process is { } proc)
            {
                try
                {
                    running = !proc.HasExited;
                    if (!running) exitCode = proc.ExitCode;
                }
                catch { /* процесс уже недоступен */ }
            }
            else if (job.TaskName is { } tn && _ps is not null)
            {
                try
                {
                    var r = _ps.Run(BuildQueryIsolatedJobCommand(tn), throwOnError: false);
                    var text = (r.StdOut ?? "").Trim();
                    if (!text.Equals("absent", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = text.Split('|');
                        running = parts.Length > 0 && parts[0].Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
                        if (!running && parts.Length > 1 && int.TryParse(parts[1].Trim(), out var er)) exitCode = er;
                    }
                }
                catch { /* планировщик недоступен прямо сейчас */ }
            }
            var size = 0L;
            try { size = new FileInfo(job.OutPath).Length; } catch { }
            result[job.Id] = new ExecJobSummary(job.Id, running, exitCode, job.StartedAt, size,
                ReadScriptPreview(Path.Combine(_root, job.Id)));
        }

        // Задачи с диска (агент мог перезапуститься): состояние процесса неизвестно —
        // показываем как незапущенные в этой жизни агента, но с датой и объёмом вывода.
        if (Directory.Exists(_root))
        {
            foreach (var dir in Directory.GetDirectories(_root))
            {
                var id = Path.GetFileName(dir);
                if (result.ContainsKey(id)) continue;
                var size = 0L;
                try { size = new FileInfo(Path.Combine(dir, "out.txt")).Length; } catch { }
                result[id] = new ExecJobSummary(id, false, null,
                    new DirectoryInfo(dir).CreationTime, size, ReadScriptPreview(dir));
            }
        }

        return result.Values.OrderByDescending(j => j.StartedAt).ToList();
    }

    /// <summary>Сколько по времени можно доверять последнему опросу планировщика для
    /// изолированной задачи из <see cref="RunningCount"/>, прежде чем спросить его заново.</summary>
    private static readonly TimeSpan IsolatedStateCacheTtl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (bool Running, DateTime At)> _isolatedRunningCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Первая непустая строка `user.ps1` этой задачи, обрезанная до разумной длины —
    /// именно ПО НЕЙ на живой заявке узнают, что за скрипт крутится, не вспоминая jobId.</summary>
    private static string? ReadScriptPreview(string jobDir)
    {
        try
        {
            var path = Path.Combine(jobDir, "user.ps1");
            if (!File.Exists(path)) return null;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                return trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Сколько задач сейчас реально выполняется. Нужно колонке активности: «была
    /// занята» должна отвечать по текущему состоянию, а не по последней команде (бэклог п.73).
    ///
    /// Вызывается из колбэка активности **на каждом heartbeat** (Critical-3, ревью волны 1):
    /// без кэша изолированная задача гоняла бы `Get-ScheduledTask` через дочерний powershell.exe
    /// на каждый тик — ровно тот антипаттерн, против которого написан
    /// <see cref="ActivityProbe"/> (опрос в heartbeat-цикле обязан быть дешёвым, иначе сам
    /// запуск powershell.exe становится узким местом под 100% нагрузкой, п.64). Точный опрос
    /// планировщика по требованию остаётся в <see cref="Status"/>/<see cref="List"/>.</summary>
    public int RunningCount()
    {
        var n = 0;
        foreach (var job in _jobs.Values)
        {
            if (job.Process is { } proc)
            {
                try { if (!proc.HasExited) n++; }
                catch { /* процесс умер между проверками */ }
                continue;
            }
            if (job.TaskName is { } tn && _ps is not null && IsIsolatedTaskRunningCached(tn)) n++;
        }
        return n;
    }

    private bool IsIsolatedTaskRunningCached(string taskName)
    {
        var now = DateTime.UtcNow;
        if (_isolatedRunningCache.TryGetValue(taskName, out var cached) && now - cached.At < IsolatedStateCacheTtl)
            return cached.Running;

        try
        {
            var r = _ps!.Run(BuildQueryIsolatedJobCommand(taskName), throwOnError: false);
            var running = (r.StdOut ?? "").Trim().StartsWith("Running", StringComparison.OrdinalIgnoreCase);
            _isolatedRunningCache[taskName] = (running, now);
            return running;
        }
        catch
        {
            // Планировщик недоступен прямо сейчас — оставляем прошлое (или false) значение,
            // а не затираем его новым false (review W2 Minor: комментарий обещал это, а код
            // до правки клал false безусловно, стирая прошлое true).
            _isolatedRunningCache[taskName] = (cached.Running, now);
            return cached.Running;
        }
    }

    /// <summary>Убить фоновую задачу (и её дерево процессов). Для изолированной задачи —
    /// остановить и разрегистрировать саму scheduled task, не только процесс; переживает
    /// рестарт агента через маркер task.txt, как и Status.</summary>
    public bool Stop(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        var taskName = ResolveIsolatedTaskName(jobId, job);
        if (taskName is not null)
        {
            if (_ps is null) return false;
            try
            {
                _ps.Run(BuildStopIsolatedJobCommand(taskName, jobId), throwOnError: false);
                _jobs.TryRemove(jobId, out _);
                return true;
            }
            catch { return false; }
        }

        if (job?.Process is not { } process) return false;
        try { process.Kill(entireProcessTree: true); return true; }
        catch { return false; }
    }

    /// <summary>Читает последние N строк, не затягивая в память весь файл и не мешая писателю.</summary>
    private static (string Tail, long Size) ReadTail(string path, int lines)
    {
        if (!File.Exists(path)) return ("", 0);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var size = fs.Length;
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var buffer = new Queue<string>(Math.Max(lines, 1));
            while (reader.ReadLine() is { } line)
            {
                buffer.Enqueue(line);
                if (buffer.Count > lines) buffer.Dequeue();
            }
            return (string.Join("\n", buffer), size);
        }
        catch (Exception ex)
        {
            return ($"(не смог прочитать вывод: {ex.Message})", 0);
        }
    }
}
