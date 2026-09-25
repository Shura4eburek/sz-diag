using System.Diagnostics;
using System.Text;

namespace SzDiag.Claude;

/// <summary>Всё, с чем запускается один процесс `claude` сессии.</summary>
/// <param name="WorkDir">Корень репозитория sz-diag: там CLAUDE.md, .claude/settings.json, скиллы.</param>
/// <param name="ConfigDir">CLAUDE_CONFIG_DIR — профиль (логин, память); null — профиль по умолчанию
/// (~/.claude): переменная у процесса снимается, даже если она была у запустившего Desk.</param>
/// <param name="PermissionMode">`auto` — как терминальный Claude, разрешения спрашиваются только на
/// то, что классификатор не пропустил; `default` — карточка на каждый инструмент.</param>
public sealed record ClaudeLaunch(string Executable, string WorkDir, string? ConfigDir, string? ResumeSessionId,
    string AppendSystemPrompt, string McpConfigPath, string PermissionMode = "auto")
{
    public const string PermissionTool = "mcp__desk__permission_prompt";

    /// <summary>Инструменты обмена между сессиями — без карточки разрешения: они ничего не меняют,
    /// а лимиты живых вопросов держит Desk (решение плана части 4). Одной строкой через запятую:
    /// флаг вариадический и иначе проглотил бы следующие аргументы.</summary>
    public const string PeerTools = "mcp__desk__peers,mcp__desk__ask_peer";

    /// <summary>Фиксированный бюджет thinking. С настройкой по умолчанию `claude -p` на Opus [1m]
    /// между ходами меняет параметры thinking, и API выбрасывает кэш сообщений: каждый ход заново
    /// пишет ~33k токенов ($0.27 за «ОК»). С фиксированным бюджетом ход — $0.01–0.02, thinking
    /// работает (эксперимент бэклога п.266, 25.09).</summary>
    public const string MaxThinkingTokens = "16000";

    /// <summary>Переменные, которыми Claude Code помечает свою сессию. Desk, запущенный из сессии
    /// Claude (dotnet run под Claude), иначе передал бы их детям: CHILD_SESSION выключает запись
    /// транскрипта (и --resume потом нечего продолжать), MESSAGING_SOCKET/TOKEN ведут в канал
    /// чужой сессии. Профиль (CLAUDE_CONFIG_DIR) маркером не считается.</summary>
    public static readonly IReadOnlyList<string> InheritedSessionMarkers = new[]
    {
        "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH",
        "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_EFFORT",
    };

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<string> Arguments()
    {
        var a = new List<string>
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            // Режим задаётся явно: в auto permission_prompt зовётся только на отказ классификатора,
            // в default — на каждый инструмент (спайк).
            "--permission-mode", PermissionMode,
            "--permission-prompt-tool", PermissionTool,
            "--allowedTools", PeerTools,
            "--mcp-config", McpConfigPath,
            // Только MCP-сервер Desk: серверы и коннекторы профиля (Supabase, Drive, IBKR…) заявке
            // не нужны, а их списки инструментов между ходами «плавали» — кэш промпта терялся, и
            // любое сообщение стоило ~$0.29 (бэклог п.266, СЗ 160176).
            "--strict-mcp-config",
            "--append-system-prompt", AppendSystemPrompt,
        };
        if (ResumeSessionId is { Length: > 0 } id)
        {
            a.Add("--resume");
            a.Add(id);
        }
        return a;
    }

    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        foreach (var arg in Arguments()) psi.ArgumentList.Add(arg);
        foreach (var name in InheritedSessionMarkers) psi.Environment.Remove(name);
        // claude.ai-коннекторы приходят мимо --mcp-config — выключаются отдельно (бэклог п.266).
        psi.Environment["ENABLE_CLAUDEAI_MCP_SERVERS"] = "false";
        psi.Environment["MAX_THINKING_TOKENS"] = MaxThinkingTokens;
        if (ConfigDir is { Length: > 0 } dir) psi.Environment["CLAUDE_CONFIG_DIR"] = dir;
        else psi.Environment.Remove("CLAUDE_CONFIG_DIR");
        return psi;
    }
}
