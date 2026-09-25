using Microsoft.Extensions.Configuration;

namespace SzDiag.Desk.Services;

public sealed class DeskOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
    public string KbRoot { get; set; } = "kb";

    /// <summary>Конфиг рядом с exe, не от рабочего каталога (конвенция репо).</summary>
    public static DeskOptions Load()
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();
        var o = new DeskOptions();
        cfg.Bind(o);
        return o;
    }
}
