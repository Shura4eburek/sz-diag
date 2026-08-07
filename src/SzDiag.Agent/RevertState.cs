namespace SzDiag.Agent;

/// <summary>Что именно применено при открытии — чтобы откатить только это и идемпотентно.</summary>
public sealed class RevertState
{
    public string Sz { get; set; } = "";
    public string ServiceAccount { get; set; } = "svc-diag";
    public string FirewallRuleName { get; set; } = "";
    public string WatchdogTaskName { get; set; } = "";
    public string SshdTaskName { get; set; } = "";
    public string AuthorizedKeyComment { get; set; } = "";
    public string AutostartTaskName { get; set; } = "";

    /// <summary>Когда доступ был открыт. Нужен watchdog'у: даже при живом агенте доступ не
    /// должен висеть на клиентской машине бесконечно (бэклог п.85).</summary>
    public DateTimeOffset? OpenedAt { get; set; }

    /// <summary>Положен ли на общий рабочий стол ярлык «закрыть доступ». Нужен, потому что
    /// после ребута агент живёт headless (сессия 0, окна нет) и локально снять доступ было
    /// нечем (бэклог п.87).</summary>
    public bool CreatedDesktopShortcut { get; set; }

    public bool CreatedUser { get; set; }
    public bool StoppedSystemSshd { get; set; }
    public bool CreatedSshdTask { get; set; }
    public bool GeneratedHostKeys { get; set; }
    public bool AddedFirewallRule { get; set; }
    public bool WroteAuthorizedKey { get; set; }
    public bool CreatedAuthorizedKeysFile { get; set; }
    public bool SetTokenPolicy { get; set; }

    /// <summary>Прежнее значение LocalAccountTokenFilterPolicy: null = отсутствовало.</summary>
    public int? TokenPolicyPreviousValue { get; set; }
    public bool CreatedWatchdogTask { get; set; }
    public bool CreatedAutostartTask { get; set; }
}
