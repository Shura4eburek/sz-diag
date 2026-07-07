using System.Diagnostics;

namespace SzDiag.Agent;

/// <summary>sshd не удалось поднять — процесс упал сразу после старта.</summary>
public sealed class SshdStartException : Exception
{
    public SshdStartException(string message) : base(message) { }
}

/// <summary>
/// Жизненный цикл портативного sshd.exe как дочернего процесса агента: свой конфиг,
/// свои host-ключи (свежие каждую сессию), свой AuthorizedKeysFile. Не зависит от
/// системной службы OpenSSH и Windows Update. Умирает вместе с агентом (fail-closed).
/// </summary>
public sealed class PortableSshServer
{
    private readonly string _sshDir;
    private readonly string _workDir;
    private readonly PowerShellRunner _ps;
    private Process? _proc;

    public string HostKeyPath => Path.Combine(_workDir, "ssh_host_ed25519_key");
    public string ConfigPath => Path.Combine(_workDir, "sshd_config");
    public string LogPath => Path.Combine(_workDir, "sshd.log");
    public string AuthorizedKeysPath => Path.Combine(_workDir, "authorized_keys");
    public string WorkDir => _workDir;

    public PortableSshServer(string sshDir, string workDir, PowerShellRunner ps)
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

    /// <summary>Свежие host-ключи + конфиг + запуск sshd.exe дочерним процессом.
    /// Кидает SshdStartException, если sshd умер в первые ~1.5с (с причиной из лога).</summary>
    public void Start(int port, string authorizedKeyLine)
    {
        Directory.CreateDirectory(_workDir);

        // Свежий host-ключ каждую сессию — битый ключ невозможен.
        if (File.Exists(HostKeyPath)) File.Delete(HostKeyPath);
        if (File.Exists(HostKeyPath + ".pub")) File.Delete(HostKeyPath + ".pub");
        _ps.Run($"& '{Path.Combine(_sshDir, "ssh-keygen.exe")}' -t ed25519 -f '{HostKeyPath}' -N '\"\"' -q");

        File.WriteAllText(AuthorizedKeysPath, authorizedKeyLine.Trim() + Environment.NewLine);
        File.WriteAllText(ConfigPath, BuildConfig(port, HostKeyPath, AuthorizedKeysPath));

        // ACL на ключ и authorized_keys: только SYSTEM+Administrators, иначе sshd
        // отказывается их использовать ("bad permissions").
        foreach (var f in new[] { HostKeyPath, AuthorizedKeysPath })
            _ps.Run($"icacls '{f}' /inheritance:r /grant 'SYSTEM:F' /grant 'BUILTIN\\Administrators:F'",
                throwOnError: false);

        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_sshDir, "sshd.exe"),
            Arguments = $"-f \"{ConfigPath}\" -D -E \"{LogPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (File.Exists(LogPath)) File.Delete(LogPath);
        _proc = Process.Start(psi) ?? throw new SshdStartException("не удалось запустить sshd.exe");

        // Дать sshd мгновение подняться; если он сразу умер — вытащить причину из лога.
        if (_proc.WaitForExit(1500))
        {
            var log = File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";
            throw new SshdStartException($"sshd не стартовал: {DescribeFailure(log)}");
        }
    }

    /// <summary>Убить sshd (идемпотентно). Fail-closed при откате/краше агента.</summary>
    public void Stop()
    {
        try { if (_proc is { HasExited: false }) _proc.Kill(entireProcessTree: true); }
        catch { /* уже мог сам завершиться — гонка, не критично */ }
        _proc = null;
    }
}
