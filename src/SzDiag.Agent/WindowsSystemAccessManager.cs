using System.Security.Cryptography;

namespace SzDiag.Agent;

/// <summary>OpenSSH не удалось поставить (нет доступа к Windows Update) — истёк таймаут.</summary>
public sealed class OpenSshUnavailableException : Exception
{
    public OpenSshUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Реальная Windows-реализация. Open применяет шаги и прогрессивно пишет RevertState
/// в файл (переживает краш). Revert откатывает по флагам, обратный порядок, идемпотентно.
/// Ключ администратора кладётся в administrators_authorized_keys (OpenSSH на Windows
/// игнорирует per-user authorized_keys для членов Administrators).
/// </summary>
public sealed class WindowsSystemAccessManager : ISystemAccessManager
{
    private const string AdminsSid = "S-1-5-32-544";
    private const string TokenPolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private static readonly TimeSpan OpenSshInstallTimeout = TimeSpan.FromMinutes(2);
    private static readonly string AdminAuthKeys =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "administrators_authorized_keys");

    private readonly PowerShellRunner _ps;
    private readonly string _statePath;

    public WindowsSystemAccessManager(PowerShellRunner ps, string statePath)
    {
        _ps = ps;
        _statePath = statePath;
    }

    public RevertState Open(AccessSpec spec)
    {
        var state = new RevertState
        {
            Sz = spec.Sz,
            ServiceAccount = spec.ServiceAccount,
            FirewallRuleName = $"szdiag-ssh-{spec.Sz}",
            WatchdogTaskName = $"szdiag-watchdog-{spec.Sz}",
            AuthorizedKeyComment = $"szdiag-{spec.Sz}"
        };
        void Persist() => RevertStateStore.Save(_statePath, state);
        Persist();

        // 1. OpenSSH Server: сначала быстрая проверка службы. Медленный Get/Add-WindowsCapability
        // (DISM/Windows Update) вызываем ТОЛЬКО если службы sshd вообще нет.
        var sshdStatus = _ps.Run("(Get-Service sshd -ErrorAction SilentlyContinue).Status",
            throwOnError: false).StdOut.Trim();
        var sshdExists = sshdStatus.Length > 0;
        if (!sshdExists)
        {
            try
            {
                _ps.Run("Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0",
                    timeout: OpenSshInstallTimeout);
            }
            catch (PowerShellTimeoutException)
            {
                throw new OpenSshUnavailableException(
                    "OpenSSH не ставится — нет доступа к Windows Update (истёк таймаут " +
                    $"{OpenSshInstallTimeout.TotalMinutes:0} мин). Проверьте интернет на клиенте и запустите агента заново.");
            }
            state.InstalledOpenSsh = true;
            Persist();
        }

        // 2. Запустить службу, если ещё не Running (иначе не трогаем — значит стояла до нас).
        if (!sshdStatus.Contains("Running"))
        {
            _ps.Run("Set-Service sshd -StartupType Automatic; Start-Service sshd");
            state.StartedSshService = true;
            Persist();
        }

        // 3. Firewall (идемпотентно: снести остаток одноимённого правила, затем создать).
        _ps.Run($"Remove-NetFirewallRule -Name '{state.FirewallRuleName}' -ErrorAction SilentlyContinue; " +
                $"New-NetFirewallRule -Name '{state.FirewallRuleName}' " +
                $"-DisplayName '{state.FirewallRuleName}' -Enabled True -Direction Inbound " +
                $"-Protocol TCP -Action Allow -LocalPort {spec.SshPort} | Out-Null");
        state.AddedFirewallRule = true;
        Persist();

        // 4. LocalAccountTokenFilterPolicy
        var prev = _ps.Run(
            $"(Get-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
            "-ErrorAction SilentlyContinue).LocalAccountTokenFilterPolicy").StdOut.Trim();
        state.TokenPolicyPreviousValue = int.TryParse(prev, out var pv) ? pv : null;
        if (state.TokenPolicyPreviousValue != 1)
        {
            _ps.Run($"New-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
                    "-Value 1 -PropertyType DWord -Force");
            state.SetTokenPolicy = true;
            Persist();
        }

        // 5. Учётка svc-diag (админ), идемпотентно: снести остаток, затем создать.
        // New-LocalUser (а не `net user /add`): последний при пароле >14 символов
        // требует интерактивного «продолжить? (Y/N)» и падает без stdin.
        var password = GeneratePassword();
        _ps.Run($"Remove-LocalUser -Name '{spec.ServiceAccount}' -ErrorAction SilentlyContinue", throwOnError: false);
        _ps.Run($"New-LocalUser -Name '{spec.ServiceAccount}' " +
                $"-Password (ConvertTo-SecureString '{password}' -AsPlainText -Force) " +
                "-AccountNeverExpires -PasswordNeverExpires | Out-Null");
        _ps.Run($"Add-LocalGroupMember -SID {AdminsSid} -Member {spec.ServiceAccount} -ErrorAction SilentlyContinue",
            throwOnError: false);
        state.CreatedUser = true;
        Persist();

        // 6. administrators_authorized_keys + ACL
        var keyLine = $"{spec.ServicePublicKey.Trim()} {state.AuthorizedKeyComment}";
        if (!File.Exists(AdminAuthKeys))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AdminAuthKeys)!);
            File.WriteAllText(AdminAuthKeys, keyLine + Environment.NewLine);
            state.CreatedAuthorizedKeysFile = true;
            _ps.Run($"icacls '{AdminAuthKeys}' /inheritance:r " +
                    "/grant 'SYSTEM:F' /grant 'BUILTIN\\Administrators:F'");
        }
        else
        {
            // Идемпотентно: убрать прежние строки с нашей меткой, затем дописать свежую.
            var kept = File.ReadAllLines(AdminAuthKeys)
                .Where(l => !l.Contains(state.AuthorizedKeyComment)).ToList();
            kept.Add(keyLine);
            File.WriteAllLines(AdminAuthKeys, kept);
        }
        state.WroteAuthorizedKey = true;
        Persist();

        // 7. Watchdog scheduled task (запускает этот exe с --revert по таймауту)
        var exe = Environment.ProcessPath!;
        var runAt = DateTime.Now.Add(spec.WatchdogTimeout).ToString("yyyy-MM-ddTHH:mm:ss");
        _ps.Run(
            $"$a = New-ScheduledTaskAction -Execute '{exe}' -Argument '--revert \"{_statePath}\"'; " +
            $"$t = New-ScheduledTaskTrigger -Once -At '{runAt}'; " +
            $"Register-ScheduledTask -TaskName '{state.WatchdogTaskName}' -Action $a -Trigger $t " +
            "-RunLevel Highest -User 'SYSTEM' -Force");
        state.CreatedWatchdogTask = true;
        Persist();

        return state;
    }

    public void Revert(RevertState state)
    {
        // Обратный порядок; каждый шаг под флагом → повторный вызов безопасен.
        if (state.CreatedWatchdogTask)
            _ps.Run($"Unregister-ScheduledTask -TaskName '{state.WatchdogTaskName}' -Confirm:$false " +
                    "-ErrorAction SilentlyContinue", throwOnError: false);

        if (state.WroteAuthorizedKey && File.Exists(AdminAuthKeys))
        {
            if (state.CreatedAuthorizedKeysFile)
                File.Delete(AdminAuthKeys);
            else
            {
                var kept = File.ReadAllLines(AdminAuthKeys)
                    .Where(l => !l.Contains(state.AuthorizedKeyComment));
                File.WriteAllLines(AdminAuthKeys, kept);
            }
        }

        if (state.AddedFirewallRule)
            _ps.Run($"Remove-NetFirewallRule -Name '{state.FirewallRuleName}' -ErrorAction SilentlyContinue",
                    throwOnError: false);

        if (state.SetTokenPolicy)
        {
            if (state.TokenPolicyPreviousValue is null)
                _ps.Run($"Remove-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
                        "-ErrorAction SilentlyContinue", throwOnError: false);
            else
                _ps.Run($"Set-ItemProperty -Path '{TokenPolicyPath}' -Name LocalAccountTokenFilterPolicy " +
                        $"-Value {state.TokenPolicyPreviousValue}", throwOnError: false);
        }

        if (state.CreatedUser)
            _ps.Run($"Remove-LocalUser -Name '{state.ServiceAccount}' -ErrorAction SilentlyContinue", throwOnError: false);

        if (state.StartedSshService)
            _ps.Run("Stop-Service sshd -ErrorAction SilentlyContinue; " +
                    "Set-Service sshd -StartupType Disabled -ErrorAction SilentlyContinue", throwOnError: false);

        if (state.InstalledOpenSsh)
            _ps.Run("Remove-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0", throwOnError: false);

        RevertStateStore.Delete(_statePath);
    }

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return "Aa1!" + Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-");
    }
}
