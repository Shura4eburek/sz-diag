namespace SzDiag.Agent;

/// <summary>Quick tunnel к нашему sshd через внешний cloudflared.exe.
///
/// Запуск — транзиентной scheduled task под SYSTEM, тем же паттерном, что sshd: обычный
/// дочерний процесс умирает вместе с SSH-сессией, а под нагрузкой (OCCT) сессия рвётся —
/// это уже проходили с `lhmmon`. Имя туннеля печатается только в лог, поэтому вывод
/// перенаправляется в файл и опрашивается.
///
/// Quick tunnel (а не именованный) выбран сознательно: он не требует токена вообще, и
/// инвариант «рабочий токен на клиентскую машину не заезжает» соблюдается буквально.</summary>
public sealed class CloudflaredTunnel : IAccessTunnel
{
    /// <summary>Как часто перечитываем лог в ожидании имени.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly string _exePath;
    private readonly string _workDir;
    private readonly IPowerShellRunner _ps;

    public string LogPath => Path.Combine(_workDir, "cloudflared.log");

    public CloudflaredTunnel(string exePath, string workDir, IPowerShellRunner ps)
    {
        _exePath = exePath;
        _workDir = workDir;
        _ps = ps;
    }

    /// <summary>PowerShell регистрации+запуска cloudflared транзиентной задачей под SYSTEM.
    /// Через cmd.exe, потому что имя туннеля cloudflared печатает в поток, а не в файл —
    /// перенаправление нужно на уровне оболочки (`&gt; log 2&gt;&amp;1`).</summary>
    public static string BuildRegisterTaskCommand(string taskName, string exePath, int sshPort,
        string logPath)
    {
        var task = taskName.Replace("'", "''");
        // Аргумент задачи — одна строка для cmd. Внутренние кавычки вокруг путей обязательны:
        // на живых заявках уже ломались на пробелах в пути (см. NativeAgentRestart).
        var inner = $"/c \"\"{exePath}\" tunnel --url ssh://127.0.0.1:{sshPort} " +
                    $"--no-autoupdate > \"{logPath}\" 2>&1\"";
        var argument = inner.Replace("'", "''");
        return "$a = New-ScheduledTaskAction -Execute 'cmd.exe' " +
               $"-Argument '{argument}'; " +
               "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
               "-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
               $"Register-ScheduledTask -TaskName '{task}' -Action $a -Settings $s " +
               "-RunLevel Highest -User 'SYSTEM' -Force | Out-Null; " +
               $"Start-ScheduledTask -TaskName '{task}'";
    }

    /// <summary>PowerShell снятия задачи + добивания НАШЕГО cloudflared (по пути exe в
    /// командной строке — чужой cloudflared на машине не трогаем). Идемпотентно, работает
    /// и когда агент уже мёртв.</summary>
    public static string BuildStopCommand(string taskName, string exePath)
    {
        var task = taskName.Replace("'", "''");
        var exe = exePath.Replace("'", "''").Replace("\\", "\\\\");
        return $"Stop-ScheduledTask -TaskName '{task}' -ErrorAction SilentlyContinue; " +
               $"Unregister-ScheduledTask -TaskName '{task}' -Confirm:$false -ErrorAction SilentlyContinue; " +
               "Get-CimInstance Win32_Process -Filter \"Name='cloudflared.exe'\" -ErrorAction SilentlyContinue | " +
               $"Where-Object {{ $_.CommandLine -match '{exe}' }} | " +
               "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
    }

    public string? Start(int sshPort, string taskName, TimeSpan timeout)
    {
        Directory.CreateDirectory(_workDir);

        // Прежний лог сносим: иначе разбор подберёт имя от прошлого запуска, и hub получит
        // адрес мёртвого туннеля — хуже, чем честное «туннель не поднялся».
        try { if (File.Exists(LogPath)) File.Delete(LogPath); }
        catch { /* занят прошлым процессом — переживём, имя ищем по последнему вхождению */ }

        _ps.Run(BuildRegisterTaskCommand(taskName, _exePath, sshPort, LogPath), throwOnError: false);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (TryReadHostname(out var host)) return host;
            Thread.Sleep(PollInterval);
        }

        // Имени нет — снимаем задачу, чтобы не осталась висеть пустышка. Доступ при этом
        // остаётся открытым: без SSH заявка работает через exec-канал.
        Stop(taskName);
        return null;
    }

    public void Stop(string taskName)
        => _ps.Run(BuildStopCommand(taskName, _exePath), throwOnError: false);

    /// <summary>Лог читается с FileShare.ReadWrite: его прямо сейчас пишет cloudflared под
    /// SYSTEM, и обычное чтение упёрлось бы в блокировку.</summary>
    private bool TryReadHostname(out string? hostname)
    {
        hostname = null;
        try
        {
            if (!File.Exists(LogPath)) return false;
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return CloudflaredOutputParser.TryFindHostname(reader.ReadToEnd(), out hostname);
        }
        catch
        {
            return false;   // файл ещё не создан или занят — повторим на следующем круге
        }
    }
}
