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
