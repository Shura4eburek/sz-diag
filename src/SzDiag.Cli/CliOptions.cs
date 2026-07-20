namespace SzDiag.Cli;

public sealed class CliOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
    public string KbRoot { get; set; } = "kb";
    public string GpuDbPath { get; set; } = "gpu.db";
    public string PciIdsPath { get; set; } = "pci.ids";
}
