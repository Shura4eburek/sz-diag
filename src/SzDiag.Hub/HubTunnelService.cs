using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>
/// Именованный Cloudflare Tunnel, публикующий hub наружу (<c>hub.&lt;домен&gt;</c>).
/// Живёт ровно столько, сколько живёт hub: поднимается дочерним процессом на старте и
/// убивается при остановке. Служба/автозапуск по входу в систему сознательно не используются —
/// туннель без hub за ним отдаёт 502, а поднятый hub без туннеля недоступен снаружи
/// (16.09.2026 так и было: hub слушал, домен отвечал 530/1033).
///
/// Осиротевший процесс от прошлого запуска (hub убили kill'ом, а не Ctrl+C) добивается по
/// pid-файлу: иначе второй cloudflared на тот же туннель поднимет параллельные коннекты
/// к Cloudflare, и запросы будут случайно ходить в мёртвый.
/// </summary>
public sealed class HubTunnelService : BackgroundService
{
    private readonly HubTunnelOptions _options;
    private readonly ILogger<HubTunnelService> _logger;
    private readonly string _baseDir;
    private readonly HubStatusTracker? _status;
    private KillOnCloseJob? _job;

    public HubTunnelService(IOptions<HubOptions> options, ILogger<HubTunnelService> logger,
        HubStatusTracker? status = null)
        : this(options.Value.Tunnel, logger, AppContext.BaseDirectory, status)
    {
    }

    public HubTunnelService(HubTunnelOptions options, ILogger<HubTunnelService> logger, string baseDir,
        HubStatusTracker? status = null)
    {
        _options = options;
        _logger = logger;
        _baseDir = baseDir;
        _status = status;
    }

    /// <summary>Путь pid-файла: относительный резолвится от папки exe (как остальные пути hub).</summary>
    public string PidFilePath => Path.IsPathRooted(_options.PidFile)
        ? _options.PidFile
        : Path.Combine(_baseDir, _options.PidFile);

    /// <summary>Аргументы cloudflared. Порядок важен: глобальные флаги идут ДО подкоманды,
    /// иначе cloudflared их не видит. Имя туннеля опционально — без него берётся из конфига.</summary>
    public static string[] BuildArguments(HubTunnelOptions options)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(options.ConfigPath))
        {
            args.Add("--config");
            args.Add(options.ConfigPath);
        }
        args.Add("--no-autoupdate");
        args.Add("tunnel");
        args.Add("run");
        if (!string.IsNullOrWhiteSpace(options.Name)) args.Add(options.Name);
        return args.ToArray();
    }

    /// <summary>Где искать cloudflared.exe, если путь не задан явно: сначала PATH, затем
    /// штатные каталоги установки (пакет ставится в Program Files (x86) даже на x64).</summary>
    public static string? ResolveExecutable(string? configured, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return exists(configured) ? configured : null;

        foreach (var candidate in DefaultExecutableCandidates())
            if (exists(candidate)) return candidate;

        return null;
    }

    private static IEnumerable<string> DefaultExecutableCandidates()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir, "cloudflared.exe"); }
            catch (ArgumentException) { continue; }   // мусорный элемент PATH — не повод падать
            yield return candidate;
        }

        yield return @"C:\Program Files (x86)\cloudflared\cloudflared.exe";
        yield return @"C:\Program Files\cloudflared\cloudflared.exe";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _status?.Tunnel(TunnelStates.Off);
            return;
        }

        var exe = ResolveExecutable(_options.ExecutablePath, File.Exists);
        if (exe is null)
        {
            _logger.LogWarning(
                "туннель: cloudflared.exe не найден (Hub:Tunnel:ExecutablePath) — hub доступен только локально");
            _status?.Tunnel(TunnelStates.NotFound);
            return;
        }

        KillOrphan();

        // Страховка на случай, когда hub убивают жёстко: StopAsync тогда не вызывается,
        // а job закрывается вместе с процессом и уносит cloudflared за собой.
        if (OperatingSystem.IsWindows()) _job = KillOnCloseJob.TryCreate();

        while (!stoppingToken.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            try
            {
                await RunOnceAsync(exe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "туннель: запуск cloudflared не удался");
            }

            if (stoppingToken.IsCancellationRequested) break;

            _status?.Tunnel(TunnelStates.Restarting);
            _logger.LogWarning("туннель: cloudflared завершился (прожил {Seconds:F0} с), перезапуск через {Delay}",
                (DateTime.UtcNow - started).TotalSeconds, _options.RestartDelay);
            try { await Task.Delay(_options.RestartDelay, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceAsync(string exe, CancellationToken stoppingToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = _baseDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(_options)) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => LogLine(e.Data);
        process.ErrorDataReceived += (_, e) => LogLine(e.Data);

        if (!process.Start()) throw new InvalidOperationException("Process.Start вернул false");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (_job is not null && !_job.TryAssign(process.Handle))
            _logger.LogDebug("туннель: процесс не назначен в job — откат держится на pid-файле");
        WritePid(process.Id);
        _status?.Tunnel(TunnelStates.Running);
        _logger.LogInformation("туннель: cloudflared запущен (pid {Pid}){Name}", process.Id,
            string.IsNullOrWhiteSpace(_options.Name) ? "" : $", туннель {_options.Name}");

        try
        {
            await process.WaitForExitAsync(stoppingToken);
        }
        finally
        {
            StopProcess(process);
            ClearPid();
        }
    }

    public override void Dispose()
    {
        _job?.Dispose();
        base.Dispose();
    }

    private void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
                _logger.LogInformation("туннель: cloudflared остановлен вместе с hub");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "туннель: не удалось остановить cloudflared");
        }
    }

    private void LogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // cloudflared пишет весь свой лог в stderr, поэтому уровень берём из самой строки,
        // иначе штатный старт выглядел бы как поток ошибок.
        if (line.Contains("ERR ", StringComparison.Ordinal))
            _logger.LogWarning("туннель: {Line}", line);
        else
            _logger.LogDebug("туннель: {Line}", line);
    }

    private void WritePid(int pid)
    {
        try
        {
            var path = PidFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, pid.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "туннель: не удалось записать pid-файл");
        }
    }

    private void ClearPid()
    {
        try { File.Delete(PidFilePath); }
        catch { /* уже нет или занят — на следующем старте pid просто не совпадёт */ }
    }

    /// <summary>Добить cloudflared, оставшийся от прошлого запуска hub. Проверяем имя
    /// процесса: pid переиспользуется системой, и убить по одному только числу — способ
    /// прибить чужой процесс.</summary>
    private void KillOrphan()
    {
        int pid;
        try
        {
            if (!File.Exists(PidFilePath)) return;
            if (!int.TryParse(File.ReadAllText(PidFilePath).Trim(), out pid)) return;
        }
        catch
        {
            return;
        }

        try
        {
            using var orphan = Process.GetProcessById(pid);
            if (!orphan.ProcessName.Equals("cloudflared", StringComparison.OrdinalIgnoreCase)) return;
            orphan.Kill(entireProcessTree: true);
            orphan.WaitForExit(5000);
            _logger.LogInformation("туннель: добит cloudflared от прошлого запуска (pid {Pid})", pid);
        }
        catch (ArgumentException)
        {
            // процесса с таким pid нет — обычный случай после чистой остановки
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "туннель: не удалось добить cloudflared от прошлого запуска (pid {Pid})", pid);
        }
        finally
        {
            ClearPid();
        }
    }
}
