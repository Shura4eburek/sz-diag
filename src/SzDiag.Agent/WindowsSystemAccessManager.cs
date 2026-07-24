using System.Security.Cryptography;

namespace SzDiag.Agent;

/// <summary>
/// Реальная Windows-реализация. Open применяет шаги и прогрессивно пишет RevertState
/// в файл (переживает краш). Revert откатывает по флагам, обратный порядок, идемпотентно.
/// SSH-доступ поднимается портативным sshd (PortableSshServer) — системная служба
/// OpenSSH и Windows Update не задействуются; ключ идёт в собственный AuthorizedKeysFile.
/// </summary>
public sealed class WindowsSystemAccessManager : ISystemAccessManager
{
    private const string AdminsSid = "S-1-5-32-544";
    private const string TokenPolicyPath = @"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private readonly IPowerShellRunner _ps;
    private readonly ISshServer _sshd;
    private readonly string _statePath;

    public WindowsSystemAccessManager(IPowerShellRunner ps, ISshServer sshd, string statePath)
    {
        _ps = ps;
        _sshd = sshd;
        _statePath = statePath;
    }

    public RevertState Open(AccessSpec spec)
    {
        // Остаток от ДРУГОЙ незакрытой СЗ → откатить, иначе её задачи/автостарт повиснут.
        RevertStaleState(spec.Sz);

        var state = new RevertState
        {
            Sz = spec.Sz,
            ServiceAccount = spec.ServiceAccount,
            FirewallRuleName = $"szdiag-ssh-{spec.Sz}",
            WatchdogTaskName = $"szdiag-watchdog-{spec.Sz}",
            SshdTaskName = $"szdiag-sshd-{spec.Sz}",
            AuthorizedKeyComment = $"szdiag-{spec.Sz}",
            AutostartTaskName = $"szdiag-autostart-{spec.Sz}"
        };
        void Persist() => RevertStateStore.Save(_statePath, state);
        Persist();

        // 1. Если системный sshd запущен — он держит порт 22. Гасим на время сессии,
        // вернём при откате. Мы поднимаем СВОЙ sshd и не зависим от состояния системного.
        var systemSshdStatus = _ps.Run("(Get-Service sshd -ErrorAction SilentlyContinue).Status",
            throwOnError: false).StdOut.Trim();
        if (systemSshdStatus.Contains("Running"))
        {
            _ps.Run("Stop-Service sshd -Force -ErrorAction SilentlyContinue", throwOnError: false);
            state.StoppedSystemSshd = true;
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

        // 6. Поднять портативный sshd под SYSTEM (scheduled task) со свежими ключами и
        // нашим authorized_keys. Под SYSTEM у sshd есть SeTcbPrivilege для logon-токена.
        // Ключ пишется в рабочую папку sshd (PortableSshServer), не в системный
        // administrators_authorized_keys — мы полностью владеем своим sshd.
        var keyLine = $"{spec.ServicePublicKey.Trim()} {state.AuthorizedKeyComment}";
        _sshd.Start(spec.SshPort, keyLine, state.SshdTaskName);
        state.GeneratedHostKeys = true;
        state.WroteAuthorizedKey = true;
        state.CreatedSshdTask = true;
        Persist();

        // 7. Watchdog scheduled task (запускает этот exe с --revert по таймауту)
        var exe = Environment.ProcessPath!;
        _ps.Run(BuildWatchdogTaskCommand(state.WatchdogTaskName, exe, _statePath,
            DateTime.Now.Add(spec.WatchdogTimeout)));
        state.CreatedWatchdogTask = true;
        Persist();

        // 8. Автостарт после ребута: scheduled task -AtStartup → agent.exe --resume.
        //    Ставится последним (доступ уже поднят), снимается в Revert первым.
        _ps.Run(BuildAutostartTaskCommand(state.AutostartTaskName, exe, _statePath));
        state.CreatedAutostartTask = true;
        Persist();

        return state;
    }

    public void Revert(RevertState state)
    {
        // Автостарт снимаем ПЕРВЫМ: если откат упадёт на середине, агент не должен
        // воскреснуть при следующем ребуте.
        if (state.CreatedAutostartTask)
            _ps.Run($"Unregister-ScheduledTask -TaskName '{state.AutostartTaskName}' -Confirm:$false " +
                    "-ErrorAction SilentlyContinue", throwOnError: false);

        // Обратный порядок; каждый шаг под флагом → повторный вызов безопасен.
        if (state.CreatedWatchdogTask)
            _ps.Run($"Unregister-ScheduledTask -TaskName '{state.WatchdogTaskName}' -Confirm:$false " +
                    "-ErrorAction SilentlyContinue", throwOnError: false);

        // Наш sshd живёт под SYSTEM-задачей: снять задачу и добить процессы (идемпотентно,
        // работает и при watchdog-ревёрте, когда агента уже нет). Затем снять ключи/конфиг.
        if (state.CreatedSshdTask)
            _sshd.Stop(state.SshdTaskName);
        if (state.GeneratedHostKeys && Directory.Exists(_sshd.WorkDir))
        {
            try { Directory.Delete(_sshd.WorkDir, recursive: true); } catch { /* залочен — не критично */ }
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

        // Вернуть системный sshd, если гасили его на время сессии.
        if (state.StoppedSystemSshd)
            _ps.Run("Start-Service sshd -ErrorAction SilentlyContinue", throwOnError: false);

        RevertStateStore.Delete(_statePath);
    }

    /// <summary>Если на диске остался state.json от ДРУГОЙ (незакрытой) СЗ — откатить её,
    /// прежде чем открывать новую. Иначе задачи/автостарт прошлой СЗ повиснут = след.</summary>
    public void RevertStaleState(string currentSz)
    {
        var existing = RevertStateStore.Load(_statePath);
        if (existing is not null && existing.Sz != currentSz)
            Revert(existing);
    }

    /// <summary>PowerShell регистрации watchdog-задачи (-Once на runAt): по таймауту
    /// запускает этот exe с --revert. -Force перезаписывает существующую (для resume-сдвига).</summary>
    public static string BuildWatchdogTaskCommand(string taskName, string exePath, string statePath, DateTime runAt) =>
        $"$a = New-ScheduledTaskAction -Execute '{exePath}' -Argument '--revert \"{statePath}\"'; " +
        $"$t = New-ScheduledTaskTrigger -Once -At '{runAt:yyyy-MM-ddTHH:mm:ss}'; " +
        $"Register-ScheduledTask -TaskName '{taskName}' -Action $a -Trigger $t " +
        "-RunLevel Highest -User 'SYSTEM' -Force";

    /// <summary>PowerShell регистрации автостарт-задачи (-AtStartup): после ребута
    /// поднимает этот exe с --resume под SYSTEM (до логина, headless).</summary>
    public static string BuildAutostartTaskCommand(string taskName, string exePath, string statePath) =>
        $"$a = New-ScheduledTaskAction -Execute '{exePath}' -Argument '--resume \"{statePath}\"'; " +
        "$t = New-ScheduledTaskTrigger -AtStartup; " +
        $"Register-ScheduledTask -TaskName '{taskName}' -Action $a -Trigger $t " +
        "-RunLevel Highest -User 'SYSTEM' -Force";

    private static string GeneratePassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(18);
        return "Aa1!" + Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-");
    }
}
