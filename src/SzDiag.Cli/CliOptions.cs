namespace SzDiag.Cli;

public sealed class CliOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
}
