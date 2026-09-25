using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class ClaudeSessionTests : IDisposable
{
    private const string SpikeSession = "8c8e3bf5-ea25-4879-a3dc-566095eba936";
    private readonly SessionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Init => Fixture.Line("simple-turn.jsonl", e => e is SystemInit);
    private static string Text => Fixture.Line("simple-turn.jsonl", e => e is AssistantText);
    private static string Result => Fixture.Line("simple-turn.jsonl", e => e is TurnResult);
    private static string Hook => Fixture.Line("simple-turn.jsonl", e => e is ServiceEvent { Subtype: "hook_response" });
    private static string Aborted => Fixture.Line("interrupt-turn.jsonl", e => e is TurnResult { Interrupted: true });

    private static string UserText(string line)
        => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString()!;

    private static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Send_StartsProcess_WritesUserMessage()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("проверь диски");

        Assert.Equal(SessionState.Working, s.State);
        Assert.Null(_h.Last.Launch!.ResumeSessionId);
        Assert.Equal("проверь диски", UserText(Assert.Single(_h.Last.Written)));
        Assert.IsType<DeskUserMessage>(Assert.Single(s.History));
    }

    [Fact]
    public async Task Init_StoresSessionId_NextLaunchResumes()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Init);
        _h.Last.Emit(Result);
        Assert.Equal(SpikeSession, s.SessionId);

        await s.StopAsync();
        Assert.Equal(SessionState.Stopped, s.State);
        await s.SendAsync("ещё");

        Assert.Equal(2, _h.Processes.Count);
        Assert.Equal(SpikeSession, _h.Last.Launch!.ResumeSessionId);
    }

    [Fact]
    public async Task SendWhileWorking_Queued_SentAfterResult()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("первое");
        await s.SendAsync("второе");
        Assert.Single(_h.Last.Written);
        Assert.Equal(new[] { "второе" }, s.Queued);

        _h.Last.Emit(Result);
        await WaitUntil(() => _h.Last.Written.Count == 2);
        Assert.Equal("второе", UserText(_h.Last.Written[1]));
        Assert.Empty(s.Queued);
        Assert.Equal(SessionState.Working, s.State);
    }

    [Fact]
    public async Task Result_CountsTokens_AndGoesIdle()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Result);

        Assert.Equal(5, s.Usage.Output);
        Assert.Equal(5, _h.Tokens.Today.Output);
        Assert.Equal(SessionState.Idle, s.State);
    }

    private static string ResultWithCost(decimal cost) =>
        $$$"""{"type":"result","subtype":"success","is_error":false,"session_id":"s","result":"ok","total_cost_usd":{{{cost.ToString(System.Globalization.CultureInfo.InvariantCulture)}}},"usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}""";

    [Fact]
    public async Task Cost_IsCumulativePerProcess_LedgerGetsDelta()
    {
        // Живой прогон: total_cost_usd в result копится за жизнь процесса (0.136 → 0.289 → 0.441),
        // usage — за ход. Складывать стоимость как есть — завышать счётчик квадратично.
        var s = _h.Manager.Create("161432");
        await s.SendAsync("раз");
        _h.Last.Emit(ResultWithCost(0.10m));
        await s.SendAsync("два");
        _h.Last.Emit(ResultWithCost(0.25m));
        Assert.Equal(0.25m, _h.Tokens.CostToday);

        await s.StopAsync();                          // новый процесс — отсчёт стоимости заново
        await s.SendAsync("три");
        _h.Last.Emit(ResultWithCost(0.05m));
        Assert.Equal(0.30m, _h.Tokens.CostToday);
        Assert.Equal(6, _h.Tokens.Today.Total);       // usage — за ход, складывается как есть
    }

    [Fact]
    public async Task Interrupt_WritesControlRequest_ReturnsQueue()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("считай до 400");
        await s.SendAsync("потом это");

        var stop = s.InterruptAsync();
        var ctl = JsonDocument.Parse(_h.Last.Written[^1]).RootElement;
        Assert.Equal("control_request", ctl.GetProperty("type").GetString());
        Assert.Equal("interrupt", ctl.GetProperty("request").GetProperty("subtype").GetString());

        _h.Last.Emit(Aborted);
        Assert.Equal(new[] { "потом это" }, await stop.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(SessionState.Idle, s.State);
        Assert.Empty(s.Queued);
        Assert.Equal(2, _h.Last.Written.Count);   // «потом это» в claude не ушло
    }

    [Fact]
    public async Task Interrupt_NoResult_StopsProcessAfterTimeout()
    {
        using var h = new SessionHarness(new SessionTimeouts(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200)));
        var s = h.Manager.Create("161432");
        await s.SendAsync("зависни");

        await s.InterruptAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(h.Last.Stopped);
        Assert.Equal(SessionState.Stopped, s.State);
        Assert.Contains(s.History, e => e is DeskNote n && n.Text.Contains("не остановился"));
    }

    [Fact]
    public async Task UnexpectedExit_Crashed_WithStderr_DeniesPermission()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("работай");
        var ask = _h.Broker.AskAsync("161432", "Bash", J("""{"command":"dir"}"""), "t1", default);
        _h.Last.Stderr.Add("Error: not logged in");
        _h.Last.Exit(1);

        Assert.Equal(SessionState.Crashed, s.State);
        var crash = Assert.Single(s.History.OfType<ProcessCrashed>());
        Assert.Equal(1, crash.ExitCode);
        Assert.Contains("not logged in", crash.StderrTail[0]);
        Assert.False((await ask.WaitAsync(TimeSpan.FromSeconds(5))).Allow);
    }

    [Fact]
    public async Task ClaudeNotFound_Crashed_MessageKept_RestartSends()
    {
        _h.ClaudeFound = false;
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");

        Assert.Equal(SessionState.Crashed, s.State);
        Assert.Contains("claude не найден", Assert.Single(s.History.OfType<ProcessCrashed>()).StderrTail[0]);
        Assert.Equal(new[] { "привет" }, s.Queued);

        _h.ClaudeFound = true;
        s.Restart();
        await WaitUntil(() => _h.Processes.Count == 1 && _h.Last.Written.Count == 1);
        Assert.Equal("привет", UserText(_h.Last.Written[0]));
    }

    [Fact]
    public async Task Permission_WaitingState_AnswerInHistory()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("создай файл");
        var ask = _h.Broker.AskAsync("161432", "Write", J("""{"file_path":"a.txt"}"""), "t1", default);

        Assert.Equal(SessionState.WaitingPermission, s.State);
        var asked = Assert.Single(s.History.OfType<PermissionAsked>());
        _h.Broker.Resolve(asked.RequestId, true);

        Assert.True((await ask).Allow);
        Assert.Equal(SessionState.Working, s.State);
        Assert.True(Assert.Single(s.History.OfType<PermissionAnswered>()).Allowed);
    }

    [Fact]
    public async Task PermissionForUnknownKey_DeniedImmediately()
    {
        var v = await _h.Broker.AskAsync("999999", "Bash", J("{}"), null, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(v.Allow);
    }

    [Fact]
    public async Task StopAll_DeniesPendingAndStopsProcesses()
    {
        var a = _h.Manager.Create("161432");
        var b = _h.Manager.Create("161501");
        await a.SendAsync("x");
        var pa = _h.Last;
        await b.SendAsync("y");
        var pb = _h.Last;
        var ask = _h.Broker.AskAsync("161432", "Bash", J("{}"), null, default);

        await _h.Manager.StopAllAsync();

        Assert.True(pa.Stopped);
        Assert.True(pb.Stopped);
        Assert.False((await ask.WaitAsync(TimeSpan.FromSeconds(5))).Allow);
        Assert.Equal(SessionState.Stopped, a.State);
        Assert.Equal(SessionState.Stopped, b.State);
        Assert.DoesNotContain(a.History, e => e is ProcessCrashed);   // штатная остановка — не падение
    }

    [Fact]
    public async Task Archive_ThenSend_Resumes()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Init);
        _h.Last.Emit(Result);

        await s.ArchiveAsync();
        Assert.Equal(SessionState.Archived, s.State);
        Assert.True(_h.Index.Get("161432")!.Archived);

        await s.SendAsync("продолжим");
        Assert.False(_h.Index.Get("161432")!.Archived);
        Assert.Equal(SpikeSession, _h.Last.Launch!.ResumeSessionId);
    }

    [Fact]
    public void Create_Twice_SameSession()
        => Assert.Same(_h.Manager.Create("161432"), _h.Manager.Create("161432"));

    [Fact]
    public async Task History_ReplayedFromTranscript_WithoutServiceLines()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        _h.Last.Emit(Hook);
        _h.Last.Emit(Init);
        _h.Last.Emit(Text);
        _h.Last.Emit(Result);

        var replayed = _h.New().Get("161432")!;
        Assert.IsType<DeskUserMessage>(replayed.History[0]);
        Assert.Contains(replayed.History, e => e is AssistantText { Text: "Привет" });
        Assert.DoesNotContain("hook_response", File.ReadAllText(_h.Transcripts.PathFor("161432")));
    }

    [Fact]
    public async Task Attach_GivesHistoryThenLiveEvents()
    {
        var s = _h.Manager.Create("161432");
        await s.SendAsync("привет");
        var live = new List<ClaudeEvent>();
        var snapshot = s.Attach(live.Add);
        _h.Last.Emit(Text);

        Assert.IsType<DeskUserMessage>(Assert.Single(snapshot));
        Assert.IsType<AssistantText>(Assert.Single(live));
    }
}
