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

    /// <summary>Где искать профили Claude (каталоги `.claude*` со входом); пусто — домашняя папка.
    /// Профиль выбирается на заявку при «Начать сессию».</summary>
    public string ClaudeHome { get; set; } = "";

    /// <summary>Режим разрешений сессий: `auto` — как терминальный Claude (спрашивает только то,
    /// что не пропустил классификатор), `default` — карточка на каждый инструмент.</summary>
    public string PermissionMode { get; set; } = "auto";

    /// <summary>Живых вопросов соседям в час на одну сессию (спека, «ask_peer»).</summary>
    public int PeerLivePerHour { get; set; } = 20;

    /// <summary>Сколько ждать ответа живой сессии соседа.</summary>
    public int PeerTimeoutMinutes { get; set; } = 5;

    /// <summary>Поднять свой hub (localhost), если он не отвечает: `start-hub.cmd` отдельным окном —
    /// hub остаётся независимым процессом и переживает закрытие Desk.</summary>
    public bool AutostartHub { get; set; } = true;

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
