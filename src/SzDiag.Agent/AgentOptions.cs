namespace SzDiag.Agent;

public sealed class AgentOptions
{
    /// <summary>Адрес hub'а. Пусто — автообнаружение по UDP-broadcast (см. HubDiscovery).
    /// Непустое значение — явный override, автообнаружение не запускается.</summary>
    public string HubUrl { get; set; } = "";
    public string AgentToken { get; set; } = "";

    /// <summary>Service token приложения Cloudflare Access перед hub (пара заголовков
    /// CF-Access-Client-Id / CF-Access-Client-Secret). Пусто — Access не используется.
    /// Секрет доступа к нашему hub, общий на всех агентов, — как и AgentToken.</summary>
    public string AccessClientId { get; set; } = "";
    public string AccessClientSecret { get; set; } = "";

    /// <summary>Путь к cloudflared.exe на клиенте для публикации sshd quick tunnel'ом.
    /// Пусто — туннель не поднимается (прямой режим). Бинарь приезжает через `szcli push`.</summary>
    public string CloudflaredPath { get; set; } = "";

    public string ServiceAccount { get; set; } = "svc-diag";
    public string ServicePublicKeyPath { get; set; } = "service_key.pub";
    public int SshPort { get; set; } = 22;
    public double WatchdogHours { get; set; } = 6;
    public double HeartbeatSeconds { get; set; } = 20;
    public string StatePath { get; set; } = @"C:\ProgramData\szdiag\state.json";
    public string TestSuitePath { get; set; } = "testsuite.json";
    public string LogPath { get; set; } = @"logs\agent.log";
    /// <summary>Папка с портативным sshd.exe/ssh-keygen.exe (рядом с exe: dist\client\ssh).</summary>
    public string SshBinDir { get; set; } = "ssh";
    /// <summary>Рабочая папка sshd на клиенте: host-ключи, конфиг, лог, authorized_keys.</summary>
    public string SshWorkDir { get; set; } = @"C:\ProgramData\szdiag\ssh";

    /// <summary>Липкая панель статуса в верхних строках консоли. false — обычный
    /// линейный вывод. Переопределяется через SZAGENT_StickyHeader.</summary>
    public bool StickyHeader { get; set; } = true;
}
