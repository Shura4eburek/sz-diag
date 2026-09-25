using Microsoft.Extensions.Configuration;

namespace SzDiag.Desk.Services;

public sealed class DeskOptions
{
    public string HubBaseUrl { get; set; } = "http://localhost:5000";
    public string ManagementToken { get; set; } = "";
    public string KbRoot { get; set; } = "kb";

    /// <summary>Путь к claude.exe; пусто — поиск в PATH.</summary>
    public string ClaudePath { get; set; } = "";

    /// <summary>Рабочий каталог сессий — корень репозитория sz-diag (CLAUDE.md, скиллы, память).
    /// Пусто — вверх от exe до SzDiag.sln.</summary>
    public string ClaudeWorkDir { get; set; } = "";

    /// <summary>CLAUDE_CONFIG_DIR для claude. На боксе профиль задаёт обёртка claude2.cmd, а не
    /// переменная пользователя: без этого Desk из Проводника поднял бы claude с чужим профилем.</summary>
    public string ClaudeConfigDir { get; set; } = "";

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
