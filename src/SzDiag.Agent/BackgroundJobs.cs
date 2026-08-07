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
public sealed class BackgroundJobs
{
    private sealed record Job(string Id, Process Process, string OutPath, DateTimeOffset StartedAt);

    private readonly string _root;
    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="root">Куда складывать вывод задач. По умолчанию — вне папки агента,
    /// чтобы логи прогонов не уезжали в OneDrive клиента (см. ToolsDirectory / п.63).</param>
    public BackgroundJobs(string? root = null)
        => _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "szdiag", "jobs");

    public string Root => _root;

    /// <summary>Запускает скрипт в фоне и сразу возвращает идентификатор задачи.</summary>
    public ExecResult Start(ExecRequest request)
    {
        var jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var dir = Path.Combine(_root, jobId);
        try
        {
            Directory.CreateDirectory(dir);
            var scriptPath = Path.Combine(dir, "script.ps1");
            var outPath = Path.Combine(dir, "out.txt");

            // Скрипт кладём файлом и просим PowerShell дописывать вывод построчно: при
            // вырубоне посреди прогона всё уже на диске (буферизованный stdout теряется —
            // ровно так `nvidia-smi -f` оставлял пустой файл, п.20).
            var wrapped = new StringBuilder()
                .AppendLine("$ErrorActionPreference='Continue'")
                .AppendLine("$ProgressPreference='SilentlyContinue'")
                .AppendLine("& {")
                .AppendLine(request.Script)
                .AppendLine("} *>&1 | ForEach-Object { $_ | Out-File -FilePath '" + outPath.Replace("'", "''") + "' -Append -Encoding utf8 }")
                .ToString();
            File.WriteAllText(scriptPath, wrapped, new UTF8Encoding(true));

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
                $"задача запущена в фоне: {jobId}\nвывод: {outPath}", "", JobId: jobId);
        }
        catch (Exception ex)
        {
            return new ExecResult(request.RequestId, -1, "", ex.Message);
        }
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
        if (job is not null)
        {
            try
            {
                running = !job.Process.HasExited;
                if (!running) exitCode = job.Process.ExitCode;
            }
            catch { running = false; }
        }

        var (tail, size) = ReadTail(outPath, request.TailLines);
        var started = job?.StartedAt
            ?? (Directory.Exists(dir) ? new DirectoryInfo(dir).CreationTime : DateTime.Now);
        return new ExecJobStatus(request.RequestId, request.JobId, running, exitCode, tail,
            started, size);
    }

    /// <summary>Сколько задач сейчас реально выполняется. Нужно колонке активности: «была
    /// занята» должна отвечать по текущему состоянию, а не по последней команде (бэклог п.73).</summary>
    public int RunningCount()
    {
        var n = 0;
        foreach (var job in _jobs.Values)
        {
            try { if (!job.Process.HasExited) n++; }
            catch { /* процесс умер между проверками */ }
        }
        return n;
    }

    /// <summary>Убить фоновую задачу (и её дерево процессов).</summary>
    public bool Stop(string jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return false;
        try { job.Process.Kill(entireProcessTree: true); return true; }
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
