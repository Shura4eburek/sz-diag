using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>`exec --as-system`: синхронный запуск скрипта под SYSTEM, а не под учёткой агента.
///
/// Боль (бэклог п.39, СЗ 160636): заморозка WU упирается в `Access is denied` на задачах
/// `\UpdateOrchestrator\Schedule Scan` и прочих объектах TrustedInstaller/SYSTEM — прав
/// администратора недостаточно. Ключ уже в руках: агент умеет поднимать транзиентную
/// scheduled task под SYSTEM (`PortableSshServer` — для sshd, `BackgroundJobs` — для
/// изолированных фоновых задач). Этот класс переиспользует ровно тот же механизм регистрации
/// (<see cref="BackgroundJobs.BuildRegisterIsolatedJobCommand"/>), но синхронно: кладёт скрипт
/// во временную папку, ждёт завершения задачи (поллингом состояния планировщика — тем же
/// способом, что и статус изолированной фоновой задачи), читает stdout/stderr из файлов и
/// сносит задачу-обёртку независимо от исхода.</summary>
public sealed class SystemExecRunner
{
    private readonly IPowerShellRunner _ps;
    private readonly string _root;

    /// <param name="root">Куда класть временные скрипты и вывод. Отдельно от
    /// <see cref="BackgroundJobs"/>: `--as-system` не оставляет следов в общем каталоге
    /// фоновых задач и подчищается сразу после прогона, а не хранится для последующего опроса.</param>
    public SystemExecRunner(IPowerShellRunner ps, string? root = null)
    {
        _ps = ps;
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "szdiag", "as-system");
    }

    /// <summary>Как часто опрашивать состояние scheduled task, пока ждём завершения.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);

    private static string TaskName(string sz, string jobId) => $"szdiag-assystem-{sz}-{jobId}";

    public ExecResult Run(ExecRequest request)
    {
        var jobId = "as-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(_root, jobId);
        var taskName = TaskName(request.Sz, jobId);
        try
        {
            Directory.CreateDirectory(dir);
            var userPath = Path.Combine(dir, "user.ps1");
            var scriptPath = Path.Combine(dir, "script.ps1");
            var outPath = Path.Combine(dir, "out.txt");
            var errPath = Path.Combine(dir, "err.txt");

            File.WriteAllText(userPath, request.Script, new UTF8Encoding(true));
            File.WriteAllText(scriptPath, BackgroundJobs.BuildWrappedScript(userPath, outPath, errPath),
                new UTF8Encoding(true));

            _ps.Run(BackgroundJobs.BuildRegisterIsolatedJobCommand(taskName, scriptPath, dir), throwOnError: false);

            var timeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : ExecLimits.DefaultTimeoutSeconds;
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            var (running, exitCode) = Poll(taskName);
            while (running && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(PollInterval);
                (running, exitCode) = Poll(taskName);
            }

            if (running)
            {
                return new ExecResult(request.RequestId, -1, "",
                    "скрипт под SYSTEM не уложился в таймаут и был остановлен", TimedOut: true);
            }

            var stdout = ReadIfExists(outPath);
            var stderr = ReadIfExists(errPath);
            return new ExecResult(request.RequestId, exitCode ?? -1, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ExecResult(request.RequestId, -1, "", ex.Message);
        }
        finally
        {
            // Задача-обёртка не должна остаться после прогона — ни в успехе, ни в таймауте,
            // ни при исключении (критерий готовности issue #21).
            try { _ps.Run(BackgroundJobs.BuildStopIsolatedJobCommand(taskName, jobId), throwOnError: false); }
            catch { /* планировщик недоступен — мусор минимален, задача транзиентная */ }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* занято антивирусом */ }
        }
    }

    /// <summary>(жива ли задача, код возврата если завершилась). «absent» (сняли/никогда не
    /// было) считаем завершённой с неизвестным кодом.</summary>
    private (bool Running, int? ExitCode) Poll(string taskName)
    {
        try
        {
            var r = _ps.Run(BackgroundJobs.BuildQueryIsolatedJobCommand(taskName), throwOnError: false);
            var text = (r.StdOut ?? "").Trim();
            if (text.Equals("absent", StringComparison.OrdinalIgnoreCase)) return (false, null);

            var parts = text.Split('|');
            var running = parts.Length > 0 && parts[0].Trim().Equals("Running", StringComparison.OrdinalIgnoreCase);
            if (running) return (true, null);
            var exitCode = parts.Length > 1 && int.TryParse(parts[1].Trim(), out var ec) ? ec : (int?)null;
            return (false, exitCode);
        }
        catch
        {
            // Планировщик временно недоступен — не считаем это концом, продолжаем ждать
            // до дедлайна, а не рапортуем ложное завершение.
            return (true, null);
        }
    }

    private static string ReadIfExists(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : ""; }
        catch { return ""; }
    }
}
