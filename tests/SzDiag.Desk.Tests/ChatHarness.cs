using SzDiag.Claude;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public sealed class FakeTerminal : ITerminalLauncher
{
    public List<(string Key, string SessionId)> Opened { get; } = new();

    public bool Open(string key, string sessionId)
    {
        Opened.Add((key, sessionId));
        return true;
    }
}

/// <summary>Ядро сессий на фейковых процессах; UI-маршалинг — синхронный.</summary>
internal sealed class ChatHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "szdesk-" + Guid.NewGuid().ToString("N"));
    public List<FakeClaudeProcess> Processes { get; } = new();
    public PermissionBroker Broker { get; } = new(TimeProvider.System);
    public TokenLedger Tokens { get; }
    public TranscriptStore Transcripts { get; }
    public FakeTerminal Terminal { get; } = new();
    public FakePeerDirectory PeerDir { get; } = new();
    public PeerExchange Peers { get; }
    public ChatServices Services { get; }

    public ChatHarness()
    {
        Directory.CreateDirectory(Dir);
        Tokens = new TokenLedger(Path.Combine(Dir, "desk-tokens.json"), TimeProvider.System);
        Transcripts = new TranscriptStore(Path.Combine(Dir, "sessions"));
        var sessions = new SessionManager(new SessionDeps(
            SessionIndex.Load(Path.Combine(Dir, "desk-sessions.json")), Transcripts, Tokens, Broker,
            rec => new ClaudeLaunch("claude.exe", Dir, null, rec.SessionId, "вводная " + rec.Key, "mcp.json"),
            () =>
            {
                var p = new FakeClaudeProcess();
                Processes.Add(p);
                return p;
            },
            TimeProvider.System, SessionTimeouts.Default));
        Peers = new PeerExchange(sessions, PeerDir, PeerLimits.Default, TimeProvider.System);
        Services = new ChatServices(sessions, Broker, Tokens, Terminal, new[] { "claude", "claude2" }, a => a(), Peers);
    }

    public FakeClaudeProcess Last => Processes[^1];

    public ChatViewModel Chat(string key, TimeProvider? time = null)
        => new(Services.Sessions.Create(key), Broker, Terminal, a => a(), time);

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}
