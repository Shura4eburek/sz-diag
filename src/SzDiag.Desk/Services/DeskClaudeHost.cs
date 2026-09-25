using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <summary>Ядро SzDiag.Claude, собранное под конфиг Desk: MCP-сервер, брокер разрешений,
/// реестр и журналы сессий рядом с exe, конфиг MCP на каждый запуск процесса.</summary>
public sealed class DeskClaudeHost : IAsyncDisposable
{
    private readonly string _runDir;

    private DeskClaudeHost(DeskOptions o, string baseDir)
    {
        ClaudeExe = ClaudeLocator.Resolve(o.ClaudePath, Environment.GetEnvironmentVariable("PATH"));
        WorkDir = string.IsNullOrWhiteSpace(o.ClaudeWorkDir) ? FindRepoRoot(baseDir) : o.ClaudeWorkDir;
        ConfigDir = string.IsNullOrWhiteSpace(o.ClaudeConfigDir) ? null : o.ClaudeConfigDir;
        _runDir = Path.Combine(baseDir, "run");
        Tokens = new TokenLedger(Path.Combine(baseDir, "desk-tokens.json"), TimeProvider.System);
        Sessions = new SessionManager(new SessionDeps(
            SessionIndex.Load(Path.Combine(baseDir, "desk-sessions.json")),
            new TranscriptStore(Path.Combine(baseDir, "sessions")),
            Tokens, Broker, LaunchFor, () => new ClaudeProcess(), TimeProvider.System,
            SessionTimeouts.Default, DeskLog.Write));
    }

    public string? ClaudeExe { get; }
    public string WorkDir { get; }
    public string? ConfigDir { get; }
    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public DeskMcpServer Mcp { get; } = new();
    public TokenLedger Tokens { get; }
    public SessionManager Sessions { get; }

    public static async Task<DeskClaudeHost> StartAsync(DeskOptions o, string baseDir)
    {
        var host = new DeskClaudeHost(o, baseDir);
        await host.Mcp.StartAsync(host.Broker).ConfigureAwait(false);
        DeskLog.Write($"claude: {host.ClaudeExe ?? "не найден"}, каталог {host.WorkDir}, профиль {host.ConfigDir ?? "унаследован"}, MCP {host.Mcp.BaseUrl}");
        return host;
    }

    /// <summary>Конфиг MCP пишется заново на каждый запуск: порт и токен сервера меняются с
    /// каждым запуском Desk.</summary>
    public ClaudeLaunch? LaunchFor(string key, string? resumeId)
    {
        if (ClaudeExe is null) return null;
        Directory.CreateDirectory(_runDir);
        var config = Path.Combine(_runDir, $"{key}.mcp.json");
        File.WriteAllText(config, Mcp.McpConfigJson(key));
        return new ClaudeLaunch(ClaudeExe, WorkDir, ConfigDir, resumeId, SzBriefing.For(key), config);
    }

    public ChatServices Services(Action<Action> ui)
        => new(Sessions, Broker, Tokens, new TerminalLauncher(ClaudeExe, WorkDir, ConfigDir, _runDir), ui);

    public static string FindRepoRoot(string start)
    {
        for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SzDiag.sln"))) return d.FullName;
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        await Sessions.StopAllAsync().ConfigureAwait(false);
        await Mcp.DisposeAsync().ConfigureAwait(false);
    }
}
