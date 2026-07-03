namespace SzDiag.Agent;

public sealed class AgentOptions
{
    /// <summary>Адрес hub'а. Пусто — автообнаружение по UDP-broadcast (см. HubDiscovery).
    /// Непустое значение — явный override, автообнаружение не запускается.</summary>
    public string HubUrl { get; set; } = "";
    public string AgentToken { get; set; } = "";
    public string ServiceAccount { get; set; } = "svc-diag";
    public string ServicePublicKeyPath { get; set; } = "service_key.pub";
    public int SshPort { get; set; } = 22;
    public double WatchdogHours { get; set; } = 6;
    public double HeartbeatSeconds { get; set; } = 20;
    public string StatePath { get; set; } = @"C:\ProgramData\szdiag\state.json";
    public string TestSuitePath { get; set; } = "testsuite.json";
    public string LogPath { get; set; } = @"logs\agent.log";
}
