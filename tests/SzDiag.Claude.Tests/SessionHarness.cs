using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

internal sealed class SessionHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "szclaude-" + Guid.NewGuid().ToString("N"));
    public List<FakeClaudeProcess> Processes { get; } = new();
    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public TokenLedger Tokens { get; }
    public SessionIndex Index { get; }
    public TranscriptStore Transcripts { get; }
    public SessionManager Manager { get; }
    public bool ClaudeFound { get; set; } = true;

    /// <summary>Записи реестра, с которыми запрашивался запуск.</summary>
    public List<SessionRecord> Launched { get; } = new();

    public SessionHarness(SessionTimeouts? timeouts = null)
    {
        Directory.CreateDirectory(Dir);
        Tokens = new TokenLedger(Path.Combine(Dir, "desk-tokens.json"), TimeProvider.System);
        Index = SessionIndex.Load(Path.Combine(Dir, "desk-sessions.json"));
        Transcripts = new TranscriptStore(Path.Combine(Dir, "sessions"));
        Manager = New(timeouts);
    }

    /// <summary>Ещё один менеджер над теми же файлами — «Desk перезапустили».</summary>
    public SessionManager New(SessionTimeouts? timeouts = null) => new(new SessionDeps(
        Index, Transcripts, Tokens, Broker,
        rec =>
        {
            Launched.Add(rec);
            return ClaudeFound ? new ClaudeLaunch("claude.exe", Dir, null, rec.SessionId, "вводная " + rec.Key, "mcp.json") : null;
        },
        () =>
        {
            var p = new FakeClaudeProcess();
            Processes.Add(p);
            return p;
        },
        TimeProvider.System, timeouts ?? SessionTimeouts.Default));

    public FakeClaudeProcess Last => Processes[^1];

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
