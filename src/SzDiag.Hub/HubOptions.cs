namespace SzDiag.Hub;

public sealed class HubOptions
{
    /// <summary>Pre-shared токен, который агент шлёт в заголовке при коннекте.</summary>
    public string AgentToken { get; set; } = "";

    /// <summary>Токен для management-API (CLI). Заголовок X-SzDiag-Mgmt-Token.</summary>
    public string ManagementToken { get; set; } = "";

    /// <summary>Логин сервисной учётки на клиенте (для target).</summary>
    public string ServiceAccount { get; set; } = "svc-diag";

    /// <summary>Строка подключения SQLite.</summary>
    public string SqliteConnectionString { get; set; } = "Data Source=szdiag.db";

    /// <summary>Корень базы знаний (Obsidian-vault).</summary>
    public string KnowledgeBaseRoot { get; set; } = "kb";

    /// <summary>Сессия помечается офлайн, если heartbeat старше этого порога.</summary>
    public TimeSpan HeartbeatTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Период проверки sweeper'ом.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>TCP-порт, на котором слушает hub (для UDP-автообнаружения — что отдавать агенту).</summary>
    public int Port { get; set; } = 5099;
}
