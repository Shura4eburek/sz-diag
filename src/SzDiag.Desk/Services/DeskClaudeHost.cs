using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <summary>Ядро SzDiag.Claude, собранное под конфиг Desk: MCP-сервер, брокер разрешений,
/// реестр и журналы сессий рядом с exe, конфиг MCP на каждый запуск процесса, профили Claude.</summary>
public sealed class DeskClaudeHost : IAsyncDisposable
{
    private readonly string _runDir;
    private readonly string _permissionMode;

    private DeskClaudeHost(DeskOptions o, string baseDir)
    {
        ClaudeExe = ClaudeLocator.Resolve(o.ClaudePath, Environment.GetEnvironmentVariable("PATH"));
        WorkDir = string.IsNullOrWhiteSpace(o.ClaudeWorkDir) ? FindRepoRoot(baseDir) : o.ClaudeWorkDir;
        Profiles = ClaudeProfiles.Discover(string.IsNullOrWhiteSpace(o.ClaudeHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : o.ClaudeHome);
        _permissionMode = string.IsNullOrWhiteSpace(o.PermissionMode) ? "auto" : o.PermissionMode;
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

    /// <summary>Профили Claude на машине (поиск, а не конфиг): первый — по умолчанию.</summary>
    public IReadOnlyList<ClaudeProfile> Profiles { get; }

    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public DeskMcpServer Mcp { get; } = new();
    public TokenLedger Tokens { get; }
    public SessionManager Sessions { get; }

    public static async Task<DeskClaudeHost> StartAsync(DeskOptions o, string baseDir)
    {
        var host = new DeskClaudeHost(o, baseDir);
        await host.Mcp.StartAsync(host.Broker).ConfigureAwait(false);
        var profiles = string.Join(", ", host.Profiles.Select(p => $"{p.Name} ({p.ConfigDir ?? "по умолчанию"})"));
        DeskLog.Write($"claude: {host.ClaudeExe ?? "не найден"}, каталог {host.WorkDir}, режим {host._permissionMode}, " +
                      $"профили: {(profiles.Length > 0 ? profiles : "не найдены")}, MCP {host.Mcp.BaseUrl}");
        return host;
    }

    /// <summary>Профиль сессии: записанный за ней, иначе по умолчанию. Записанный, но пропавший
    /// с машины — null: подставить другой нельзя, --resume в чужом каталоге разговор не найдёт.</summary>
    public ClaudeProfile? ProfileFor(SessionRecord r)
        => r.Profile is null
            ? Profiles.FirstOrDefault() ?? new ClaudeProfile("claude", null)
            : Profiles.FirstOrDefault(p => p.Name == r.Profile);

    /// <summary>Конфиг MCP пишется заново на каждый запуск: порт и токен сервера меняются с
    /// каждым запуском Desk.</summary>
    public ClaudeLaunch? LaunchFor(SessionRecord r)
    {
        if (ClaudeExe is null || ProfileFor(r) is not { } profile) return null;
        Directory.CreateDirectory(_runDir);
        var config = Path.Combine(_runDir, $"{r.Key}.mcp.json");
        File.WriteAllText(config, Mcp.McpConfigJson(r.Key));
        return new ClaudeLaunch(ClaudeExe, WorkDir, profile.ConfigDir, r.SessionId, SzBriefing.For(r.Key), config,
            _permissionMode);
    }

    public ChatServices Services(Action<Action> ui)
        => new(Sessions, Broker, Tokens,
            new TerminalLauncher(ClaudeExe, WorkDir,
                key => Sessions.Records.FirstOrDefault(x => x.Key == key) is { } r ? ProfileFor(r) : null,
                _runDir),
            Profiles.Select(p => p.Name).ToList(), ui);

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
