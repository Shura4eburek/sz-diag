namespace SzDiag.Cli;

public sealed class CliOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
    public string KbRoot { get; set; } = "kb";
    public string GpuDbPath { get; set; } = "gpu.db";
    public string PciIdsPath { get; set; } = "pci.ids";

    /// <summary>Путь к приватному ключу svc-diag для `szcli target` (пишет build-dist).
    /// Пусто — ключ ищется в `secrets\svc_diag_key` вверх от папки CLI (п.118).</summary>
    public string SshKeyPath { get; set; } = "";
}
