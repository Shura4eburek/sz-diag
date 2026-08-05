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

    /// <summary>Папка, из которой hub раздаёт пакет агента апдейтеру
    /// (version.txt, package.zip, package.sha256). Кладётся build-dist.</summary>
    public string AgentDistRoot { get; set; } = "agent-dist";

    /// <summary>Липкая панель статуса в верхних строках консоли. false — обычный
    /// линейный вывод (рубильник на случай проблемного терминала).</summary>
    public bool StickyHeader { get; set; } = true;

    /// <summary>Оффсайт-бэкап базы знаний в git-remote.</summary>
    public KbBackupOptions KbBackup { get; set; } = new();
}

public sealed class KbBackupOptions
{
    /// <summary>Рубильник: false — сервис не стартует (vault не под git).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Период автоматического прогона.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Имя remote, куда пушим.</summary>
    public string Remote { get; set; } = "origin";

    /// <summary>Ветка, куда пушим.</summary>
    public string Branch { get; set; } = "main";

    /// <summary>Потолок на каждый вызов git: виснет сеть — процесс убивается.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
