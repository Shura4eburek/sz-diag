using SzDiag.Claude;

namespace SzDiag.Desk.Services;

/// <summary>Ядро SzDiag.Claude, собранное под конфиг Desk: MCP-сервер, брокер разрешений,
/// реестр и журналы сессий рядом с exe, конфиг MCP на каждый запуск процесса, профили Claude.</summary>
public sealed class DeskClaudeHost : IAsyncDisposable
{
    private readonly string _runDir;
    private readonly string _baseDir;
    private readonly string _permissionMode;

    private DeskClaudeHost(DeskOptions o, string baseDir)
    {
        ClaudeExe = ClaudeLocator.Resolve(o.ClaudePath, Environment.GetEnvironmentVariable("PATH"));
        WorkDir = string.IsNullOrWhiteSpace(o.ClaudeWorkDir) ? FindRepoRoot(baseDir) : o.ClaudeWorkDir;
        SessionWorkDir = o.ResolveSessionWorkDir();
        KbRoot = o.ResolveKbRoot(baseDir);
        Profiles = ClaudeProfiles.Discover(string.IsNullOrWhiteSpace(o.ClaudeHome)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : o.ClaudeHome);
        _permissionMode = string.IsNullOrWhiteSpace(o.PermissionMode) ? "auto" : o.PermissionMode;
        _baseDir = baseDir;
        _runDir = Path.Combine(baseDir, "run");
        Tokens = new TokenLedger(Path.Combine(baseDir, "desk-tokens.json"), TimeProvider.System);
        Sessions = new SessionManager(new SessionDeps(
            SessionIndex.Load(Path.Combine(baseDir, "desk-sessions.json")),
            new TranscriptStore(Path.Combine(baseDir, "sessions")),
            Tokens, Broker, LaunchFor, () => new ClaudeProcess(), TimeProvider.System,
            SessionTimeouts.Default, DeskLog.Write));
    }

    public string? ClaudeExe { get; }

    /// <summary>Корень репозитория: szcli, скрипты, и рабочий каталог старых разговоров.</summary>
    public string WorkDir { get; }

    /// <summary>Каталог новых сессий заявок с лёгким CLAUDE.md (вне репозитория).</summary>
    public string SessionWorkDir { get; }

    public string KbRoot { get; }

    /// <summary>Полный путь к szcli для вводной; null — не найден, в вводной просто «szcli».
    /// Ищется на каждый запуск: dist могут пересобрать, пока Desk открыт.</summary>
    public string? Szcli => FindSzcli(_baseDir, WorkDir);

    /// <summary>Профили Claude на машине (поиск, а не конфиг): первый — по умолчанию.</summary>
    public IReadOnlyList<ClaudeProfile> Profiles { get; }

    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public DeskMcpServer Mcp { get; } = new();
    public TokenLedger Tokens { get; }
    public SessionManager Sessions { get; }

    /// <summary>Обмен между сессиями (`peers`/`ask_peer`); null — Desk запущен без каталога соседей.</summary>
    public PeerExchange? Peers { get; private set; }

    /// <param name="peers">Каталог соседей поверх менеджера сессий: знает про СЗ, kb и железо — Desk, не ядро.</param>
    public static async Task<DeskClaudeHost> StartAsync(DeskOptions o, string baseDir,
        Func<SessionManager, IPeerDirectory>? peers = null)
    {
        var host = new DeskClaudeHost(o, baseDir);
        SessionWorkspace.Write(host.SessionWorkDir, host.WorkDir, host.KbRoot, host.Szcli);
        if (peers is not null)
            host.Peers = new PeerExchange(host.Sessions, peers(host.Sessions),
                new PeerLimits(o.PeerLivePerHour, TimeSpan.FromMinutes(o.PeerTimeoutMinutes)), TimeProvider.System);
        await host.Mcp.StartAsync(host.Broker, host.Peers).ConfigureAwait(false);
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
        // Новый разговор — в каталоге с лёгким CLAUDE.md, kb и репозиторий — через --add-dir; старый
        // остаётся в репозитории: --resume ищет разговор по каталогу.
        var lean = r.WorkDir is not null;
        return new ClaudeLaunch(ClaudeExe, r.WorkDir ?? WorkDir, profile.ConfigDir, r.SessionId, SzBriefing.For(r.Key, Szcli), config,
            _permissionMode, lean ? new[] { KbRoot, WorkDir } : null);
    }

    public ChatServices Services(Action<Action> ui)
        => new(Sessions, Broker, Tokens,
            new TerminalLauncher(ClaudeExe,
                key => Sessions.Records.FirstOrDefault(x => x.Key == key)?.WorkDir ?? WorkDir,
                key => Sessions.Records.FirstOrDefault(x => x.Key == key) is { } r ? ProfileFor(r) : null,
                _runDir),
            Profiles.Select(p => p.Name).ToList(), ui, Peers, SessionWorkDir);

    /// <summary>szcli рядом с Desk в dist (`dist\host\desk` → `dist\host\szcli.cmd`), иначе в dist
    /// репозитория сессий; ни там ни там — null.</summary>
    public static string? FindSzcli(string baseDir, string workDir)
    {
        var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(baseDir))?.FullName;
        foreach (var candidate in new[]
                 {
                     parent is null ? null : Path.Combine(parent, "szcli.cmd"),
                     Path.Combine(workDir, "dist", "host", "szcli.cmd"),
                 })
            if (candidate is not null && File.Exists(candidate)) return candidate;
        return null;
    }

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
