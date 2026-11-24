namespace SzDiag.Hub;

public sealed class HubOptions
{
    /// <summary>Pre-shared токен, который агент шлёт в заголовке при коннекте.</summary>
    public string AgentToken { get; set; } = "";

    /// <summary>Токены агентов других hub (MacBook, рабочий ноут…): апдейтер и агент, собранные
    /// на соседнем хосте, принимаются и здесь — без пересборки апдейтера под каждый hub.</summary>
    public string[] ExtraAgentTokens { get; set; } = [];

    /// <summary>Свой или один из соседских токенов. Пустой не принимается никогда.</summary>
    public bool IsAgentToken(string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        if (!string.IsNullOrEmpty(AgentToken) && provided == AgentToken) return true;
        return ExtraAgentTokens.Any(t => !string.IsNullOrEmpty(t) && t == provided);
    }

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

    /// <summary>Папка с инструментами, которые hub раздаёт агентам (`szcli push`).
    /// Относительный путь — от папки exe; build-dist кладёт сюда копию `client-tools`.</summary>
    public string ToolsRoot { get; set; } = "tools";

    /// <summary>Куда складывать файлы, забранные с клиентов (`szcli pull`). Относительный —
    /// от папки exe. Намеренно НЕ в vault: дампы и CSV раздувают git-историю базы знаний.</summary>
    public string PullRoot { get; set; } = "pulled";

    /// <summary>Куда складывать known_hosts по СЗ (относительный — от папки exe). Нужен,
    /// чтобы `szcli target` ходил со строгой проверкой host-ключа: имя quick tunnel'а
    /// публично и не аутентифицировано, без пиннинга подмену не отличить.</summary>
    public string KnownHostsRoot { get; set; } = "known_hosts";

    /// <summary>Каталог лог-файлов hub (относительный — от папки exe). Пусто — не писать в файл.</summary>
    public string LogDir { get; set; } = "logs";

    /// <summary>Сколько дней хранить лог-файлы hub. 0 — не чистить.</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Липкая панель статуса в верхних строках консоли. false — обычный
    /// линейный вывод (рубильник на случай проблемного терминала).</summary>
    public bool StickyHeader { get; set; } = true;

    /// <summary>Оффсайт-бэкап базы знаний в git-remote.</summary>
    public KbBackupOptions KbBackup { get; set; } = new();

    /// <summary>Публикация hub наружу именованным Cloudflare Tunnel.</summary>
    public HubTunnelOptions Tunnel { get; set; } = new();

    /// <summary>Публикация «где сейчас hub» в подписанном указателе (specs/002).</summary>
    public HubSignpostOptions Signpost { get; set; } = new();

    /// <summary>Подсети, на адреса которых hub не отвечает вовсе (<see cref="LocalAddressFilter"/>):
    /// личная ZT-сеть владельца с его устройствами — hub слушает 0.0.0.0, и без этого фильтра
    /// был бы виден всем её участникам. Фильтр по локальному адресу запроса, а не bind на
    /// конкретные IP: у ноута LAN по DHCP.</summary>
    public string[] DenySubnets { get; set; } = ["10.199.89.0/24"];

    /// <summary>Сервисная ZT-сеть (US3, specs/002).</summary>
    public HubZeroTierOptions ZeroTier { get; set; } = new();

    /// <summary>Порог числа потоков процесса, после которого <see cref="ThreadPoolWatchdog"/>
    /// пишет «THREAD POOL STARVATION» в лог. На живой заявке (СЗ 160306, бэклог п.50) здоровый
    /// hub сразу после рестарта держал 26 потоков, залипший — 3674. 0 — сторож выключен.</summary>
    public int ThreadPoolWarnThreshold { get; set; } = 300;

    /// <summary>Рабочие часы сервиса («08:00») — начало окна, вне которого ребут метится
    /// плановым обесточиванием, а не дефектом (бэклог п.130, СЗ 161346: рубильник на ночь
    /// искажал счётчик ⚡). Оба конца (`ServiceHoursStart`/`ServiceHoursEnd`) должны быть
    /// заданы, иначе классификация по часам выключена.</summary>
    public string? ServiceHoursStart { get; set; }

    /// <summary>Конец рабочих часов сервиса («19:00»). См. <see cref="ServiceHoursStart"/>.</summary>
    public string? ServiceHoursEnd { get; set; }

    /// <summary>В пределах какого окна после массовой одновременной пропажи heartbeat у
    /// нескольких СЗ разом ребут ещё считается тем же плановым обесточиванием (бэклог п.130) —
    /// признак, не зависящий от расписания: если свет пропал у всех разом, это не дефект
    /// конкретной машины.</summary>
    public TimeSpan MassOfflineWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Сколько СЗ должны потерять heartbeat в одном цикле <see cref="OfflineSweeper"/>,
    /// чтобы это засчиталось массовой пропажей (а не одна машина сдохла сама).</summary>
    public int MassOfflineMinSessions { get; set; } = 2;
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

public sealed class HubZeroTierOptions
{
    /// <summary>Включает работу с сервисной сетью (сторож членов, публикация в указателе).
    /// Запрет management-API из <see cref="Subnet"/> действует и при выключенном — он ничего не стоит.</summary>
    public bool Enabled { get; set; }

    /// <summary>Id сервисной сети, 16 hex (из флота).</summary>
    public string Network { get; set; } = "";

    /// <summary>Подсеть сервисной сети: клиенты оттуда ходят к hub, но не в `/api`.</summary>
    public string Subnet { get; set; } = "10.88.0.0/24";

    /// <summary>Член сети без активной СЗ старше этого срока удаляется сторожем.</summary>
    public TimeSpan SweepAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Прокси своего контроллера (сервис szdiag-zt-controller) — из флота.</summary>
    public string ControllerUrl { get; set; } = "";

    /// <summary>Bearer прокси. Только hub: агенту не уходит никогда (FR-023).</summary>
    public string ControllerToken { get; set; } = "";

    /// <summary>Адреса hub-машин в сети — их узлы сторож не трогает.</summary>
    public string[] HubAddresses { get; set; } = ["10.88.0.1", "10.88.0.2"];

    public bool ControllerConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(ControllerUrl) && !string.IsNullOrWhiteSpace(ControllerToken)
        && Network.Length == 16;
}

public sealed class HubSignpostOptions
{
    /// <summary>Рубильник. Выключен или пустой <see cref="Url"/> — hub в указатель не пишет.</summary>
    public bool Enabled { get; set; }

    /// <summary>База своего сервиса указателя (Railway оператора).</summary>
    public string Url { get; set; } = "";

    /// <summary>Период продления аренды. Аренда в указателе живёт 90 с — три пропуска.</summary>
    public TimeSpan PublishInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Имя для людей («ПК», «ноут»). Пусто — имя машины.</summary>
    public string HubName { get; set; } = "";

    /// <summary>Перехватить живую аренду другого hub (`start-hub.cmd --takeover`).</summary>
    public bool Takeover { get; set; }

    /// <summary>Приватный ключ подписи флота (secrets\fleet-sign.key).</summary>
    public string SignKeyPath { get; set; } = "";

    /// <summary>Адреса hub сверх LAN, заданные руками. build-dist сюда ничего не пишет: домены
    /// в публичный указатель не попадают.</summary>
    public List<HubSignpostEndpoint> ExtraEndpoints { get; set; } = [];
}

public sealed class HubSignpostEndpoint
{
    public string Mode { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class HubTunnelOptions
{
    /// <summary>Рубильник: false — hub доступен только по локальному адресу/LAN.</summary>
    public bool Enabled { get; set; }

    /// <summary>Путь к cloudflared.exe. Пусто — ищем в PATH и штатных каталогах установки.</summary>
    public string ExecutablePath { get; set; } = "";

    /// <summary>Файл конфигурации туннеля (ingress). Пусто — cloudflared берёт свой дефолтный.</summary>
    public string ConfigPath { get; set; } = "";

    /// <summary>Имя (или UUID) именованного туннеля. Пусто — берётся из файла конфигурации.</summary>
    public string Name { get; set; } = "";

    /// <summary>Пауза перед перезапуском, если cloudflared упал сам (сеть отвалилась).</summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Файл с pid запущенного cloudflared (относительный — от папки exe): по нему
    /// добивается процесс, переживший убитый kill'ом hub.</summary>
    public string PidFile { get; set; } = "cloudflared.pid";
}
