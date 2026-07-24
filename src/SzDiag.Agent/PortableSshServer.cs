using System.Net.Sockets;

namespace SzDiag.Agent;

/// <summary>sshd не удалось поднять — процесс упал сразу после старта.</summary>
public sealed class SshdStartException : Exception
{
    public SshdStartException(string message) : base(message) { }
}

/// <summary>
/// Жизненный цикл портативного sshd.exe под LocalSystem: свой конфиг, свои host-ключи
/// (свежие каждую сессию), свой AuthorizedKeysFile. Не зависит от системной службы
/// OpenSSH и Windows Update. sshd запускается транзиентной scheduled task под SYSTEM
/// (а не дочерним процессом агента) — иначе нет SeTcbPrivilege и sshd не может создать
/// logon-токен при publickey-логине («unable to create logon token» → Connection reset).
/// Задача снимается на откате (fail-closed через watchdog/close/клавишу C).
/// </summary>
public sealed class PortableSshServer : ISshServer
{
    private readonly string _sshDir;
    private readonly string _workDir;
    private readonly IPowerShellRunner _ps;

    public string HostKeyPath => Path.Combine(_workDir, "ssh_host_ed25519_key");
    public string ConfigPath => Path.Combine(_workDir, "sshd_config");
    public string LogPath => Path.Combine(_workDir, "sshd.log");
    public string AuthorizedKeysPath => Path.Combine(_workDir, "authorized_keys");
    public string SshdExePath => Path.Combine(_sshDir, "sshd.exe");
    public string WorkDir => _workDir;

    public PortableSshServer(string sshDir, string workDir, IPowerShellRunner ps)
    {
        _sshDir = sshDir;
        _workDir = workDir;
        _ps = ps;
    }

    /// <summary>Текст sshd_config под нашу папку. Match-override нужен, т.к. для членов
    /// Administrators Windows OpenSSH по умолчанию форсит administrators_authorized_keys
    /// и игнорирует per-user AuthorizedKeysFile.</summary>
    public static string BuildConfig(int port, string hostKeyPath, string authorizedKeysPath) =>
        $"""
        Port {port}
        HostKey {hostKeyPath}
        LogLevel VERBOSE
        PasswordAuthentication no
        PubkeyAuthentication yes
        Subsystem sftp sftp-server.exe
        Match Group administrators
            AuthorizedKeysFile {authorizedKeysPath}
        """;

    /// <summary>Достаёт из лога sshd осмысленные строки (fatal/error/exiting/Unable),
    /// отбрасывая debug-шум, — для внятного сообщения оператору вместо сырого дампа.</summary>
    public static string DescribeFailure(string log)
    {
        if (string.IsNullOrWhiteSpace(log))
            return "sshd упал без вывода в лог.";

        var meaningful = log.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("debug", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tail = meaningful.Count > 0 ? meaningful : new List<string> { "sshd упал без вывода в лог." };
        return string.Join("; ", tail.TakeLast(3));
    }

    /// <summary>PowerShell для регистрации+запуска sshd транзиентной scheduled task под
    /// SYSTEM. Даёт sshd SeTcbPrivilege (нужен для logon-токена при publickey-логине),
    /// которого нет у админ-агента. Тот же паттерн, что у watchdog-задачи.</summary>
    public static string BuildRegisterTaskCommand(
        string taskName, string sshdExePath, string configPath, string logPath) =>
        $"$a = New-ScheduledTaskAction -Execute '{sshdExePath}' " +
        $"-Argument '-f \"{configPath}\" -D -E \"{logPath}\"'; " +
        "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries " +
        "-ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew; " +
        $"Register-ScheduledTask -TaskName '{taskName}' -Action $a -Settings $s " +
        "-RunLevel Highest -User 'SYSTEM' -Force | Out-Null; " +
        $"Start-ScheduledTask -TaskName '{taskName}'";

    /// <summary>PowerShell снятия sshd-задачи + добивания процессов НАШЕГО sshd
    /// (по ConfigPath в командной строке — системный sshd ссылается на чужой конфиг,
    /// его не трогаем). Идемпотентно, работает и когда агент уже мёртв.</summary>
    public static string BuildStopCommand(string taskName, string configPath)
    {
        var cfg = configPath.Replace("'", "''").Replace("\\", "\\\\");
        return $"Stop-ScheduledTask -TaskName '{taskName}' -ErrorAction SilentlyContinue; " +
               $"Unregister-ScheduledTask -TaskName '{taskName}' -Confirm:$false -ErrorAction SilentlyContinue; " +
               "Get-CimInstance Win32_Process -Filter \"Name='sshd.exe'\" -ErrorAction SilentlyContinue | " +
               $"Where-Object {{ $_.CommandLine -match '{cfg}' }} | " +
               "ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }";
    }

    /// <summary>PowerShell для жёсткого ACL под требования sshd. ssh-keygen ставит явную
    /// ACE на юзера-создателя и делает его владельцем файла; sshd под SYSTEM такой
    /// ключ/authorized_keys отвергает ("bad permissions"). Снимаем наследование, убираем
    /// ACE создателя, оставляем только SYSTEM+Administrators (Full), владелец —
    /// Administrators. Well-known SID (S-1-5-18 / S-1-5-32-544) — локале-независимо (клиент
    /// может быть с любой локалью Windows). /inheritance:r чистит лишь унаследованные ACE,
    /// поэтому явную ACE создателя убираем отдельным /remove:g.</summary>
    public static string BuildHardenAclCommand(string path, string ownerAccount)
    {
        var p = path.Replace("'", "''");
        var o = ownerAccount.Replace("'", "''");
        return $"icacls '{p}' /setowner '*S-1-5-32-544'; " +
               $"icacls '{p}' /inheritance:r /remove:g '{o}' /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F'";
    }

    /// <summary>Свежие host-ключи + конфиг + запуск sshd под SYSTEM (scheduled task).
    /// Ждёт, пока порт начнёт слушаться; если sshd не поднялся — кидает
    /// SshdStartException с причиной из лога.</summary>
    public void Start(int port, string authorizedKeyLine, string taskName)
    {
        Directory.CreateDirectory(_workDir);

        // Старый экземпляр НАШЕГО sshd мог пережить ребут/краш агента (задача осталась,
        // процесс висит) и держать host-ключ открытым — тогда File.Delete ниже упадёт
        // "файл занят". Превентивно снимаем задачу и добиваем наш sshd (идемпотентно,
        // по ConfigPath — системный sshd не трогаем), затем ждём освобождения файла.
        _ps.Run(BuildStopCommand(taskName, ConfigPath), throwOnError: false);

        // Свежий host-ключ каждую сессию — битый ключ невозможен. Удаляем с ретраем:
        // после kill старый sshd отпускает файл не мгновенно.
        DeleteWithRetry(HostKeyPath);
        DeleteWithRetry(HostKeyPath + ".pub");
        _ps.Run($"& '{Path.Combine(_sshDir, "ssh-keygen.exe")}' -t ed25519 -f '{HostKeyPath}' -N '\"\"' -q");

        File.WriteAllText(AuthorizedKeysPath, authorizedKeyLine.Trim() + Environment.NewLine);
        File.WriteAllText(ConfigPath, BuildConfig(port, HostKeyPath, AuthorizedKeysPath));

        // ACL на ключ и authorized_keys: только SYSTEM+Administrators, владелец —
        // Administrators, иначе sshd под SYSTEM их отвергает ("bad permissions").
        // ssh-keygen ставит явную ACE на юзера-создателя (агента) и делает его владельцем
        // файла — оба факта sshd не принимает, поэтому убираем и то, и другое.
        var owner = _ps.Run("[System.Security.Principal.WindowsIdentity]::GetCurrent().Name")
            .StdOut.Trim();
        foreach (var f in new[] { HostKeyPath, AuthorizedKeysPath })
            _ps.Run(BuildHardenAclCommand(f, owner), throwOnError: false);

        // sshd.log старый экземпляр держит открытым (-E logPath) — тоже с ретраем, т.к.
        // после preemptive-kill дескриптор закрывается не мгновенно.
        DeleteWithRetry(LogPath);
        _ps.Run(BuildRegisterTaskCommand(taskName, SshdExePath, ConfigPath, LogPath));

        // Процесс не дочерний (под задачей SYSTEM) — ждём готовности по порту, а не по
        // хендлу. Успешный connect на 127.0.0.1:port = sshd слушает. Не поднялся за
        // тайм-аут → причина из лога.
        if (!WaitForPort(port, TimeSpan.FromSeconds(5)))
        {
            var log = File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";
            throw new SshdStartException($"sshd не стартовал: {DescribeFailure(log)}");
        }
    }

    /// <summary>Снять sshd-задачу и добить наш sshd (идемпотентно). Fail-closed при
    /// откате/краше агента; работает и когда агента уже нет (watchdog-ревёрт).</summary>
    public void Stop(string taskName)
    {
        _ps.Run(BuildStopCommand(taskName, ConfigPath), throwOnError: false);
    }

    /// <summary>Удаление файла с ретраем: после снятия старого sshd host-ключ
    /// освобождается не мгновенно (kill асинхронный, дескриптор закрывается с задержкой).
    /// Ловим IOException/Access и повторяем; исчерпав попытки — пробрасываем реальную ошибку.</summary>
    private static void DeleteWithRetry(string path, int attempts = 15, int stepMs = 200)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && i < attempts - 1)
            {
                Thread.Sleep(stepMs);
            }
        }
    }

    /// <summary>Поллинг TCP-порта до готовности sshd (по умолчанию каждые 200 мс).</summary>
    private static bool WaitForPort(int port, TimeSpan timeout, int stepMs = 200)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                using var c = new TcpClient();
                var connect = c.BeginConnect("127.0.0.1", port, null, null);
                if (connect.AsyncWaitHandle.WaitOne(stepMs) && c.Connected)
                    return true;
            }
            catch { /* порт ещё не слушается — повторим */ }
            Thread.Sleep(stepMs);
        } while (DateTime.UtcNow < deadline);
        return false;
    }
}
