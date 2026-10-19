using System.Text.Json;
using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class ClaudeSessionPeerTests : IDisposable
{
    private readonly SessionHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Text => Fixture.Line("simple-turn.jsonl", e => e is AssistantText);
    private static string Result => Fixture.Line("simple-turn.jsonl", e => e is TurnResult);
    private static string Aborted => Fixture.Line("interrupt-turn.jsonl", e => e is TurnResult { Interrupted: true });
    private static string ResultText => Fixture.Events("simple-turn.jsonl").OfType<TurnResult>().First().Text!;

    private static string UserText(string line)
        => JsonDocument.Parse(line).RootElement.GetProperty("message").GetProperty("content").GetString()!;

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Idle_SendsPrefixedQuestion_AnswerIsTurnResultText()
    {
        var s = await _h.Running("161501");
        var answer = s.AskAsync("161432", "какой BIOS на плате?", default);

        Assert.Equal(SessionState.AnsweringPeer, s.State);
        Assert.True(s.IsAnsweringPeer);
        Assert.StartsWith("[вопрос от сессии 161432] какой BIOS на плате?", UserText(_h.Last.Written[^1]));
        var q = Assert.IsType<PeerQuestion>(s.History[^1]);
        Assert.Equal("161432", q.FromKey);

        _h.Last.Emit(Text);
        _h.Last.Emit(Result);
        Assert.Equal(ResultText, await answer);
        Assert.Equal(SessionState.Idle, s.State);
        Assert.False(s.IsAnsweringPeer);
    }

    [Fact]
    public async Task Working_WaitsForTurnEnd_AheadOfOperatorQueue()
    {
        // Решение плана: спрашивающий ждёт не больше 5 минут — очередь оператора его не задерживает.
        var s = _h.Manager.Create("161501");
        await s.SendAsync("первое");
        await s.SendAsync("второе");
        var answer = s.AskAsync("161432", "вопрос", default);
        Assert.Single(_h.Last.Written);
        Assert.Equal(1, s.PeerQueued);

        _h.Last.Emit(Result);
        await WaitUntil(() => _h.Last.Written.Count == 2);
        Assert.StartsWith("[вопрос от сессии 161432]", UserText(_h.Last.Written[1]));
        Assert.Equal(new[] { "второе" }, s.Queued);   // очередь оператора не тронута и в поле ввода не уйдёт
        Assert.False(answer.IsCompleted);
    }

    [Fact]
    public async Task Interrupted_Fails()
    {
        var s = await _h.Running("161501");
        var answer = s.AskAsync("161432", "вопрос", default);
        _h.Last.Emit(Aborted);
        var ex = await Assert.ThrowsAsync<PeerAnswerException>(() => answer);
        Assert.Contains("прервал", ex.Message);
    }

    [Fact]
    public async Task ProcessExit_FailsPendingAnswer()
    {
        var s = await _h.Running("161501");
        var answer = s.AskAsync("161432", "вопрос", default);
        _h.Last.Exit(1);
        await Assert.ThrowsAsync<PeerAnswerException>(() => answer);
    }

    [Fact]
    public async Task Stop_FailsQueuedQuestions()
    {
        var s = _h.Manager.Create("161501");
        await s.SendAsync("первое");
        var answer = s.AskAsync("161432", "вопрос", default);
        await s.StopAsync();
        await Assert.ThrowsAsync<PeerAnswerException>(() => answer);
        Assert.Equal(0, s.PeerQueued);
    }

    [Fact]
    public async Task Cancelled_BeforeSent_RemovedFromQueue()
    {
        var s = _h.Manager.Create("161501");
        await s.SendAsync("первое");
        using var cts = new CancellationTokenSource();
        var answer = s.AskAsync("161432", "вопрос", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => answer);

        _h.Last.Emit(Result);
        await Task.Delay(100);
        Assert.Single(_h.Last.Written);   // сосед не отвечает в пустоту
        Assert.Equal(0, s.PeerQueued);
    }

    [Fact]
    public async Task PermissionDuringAnswer_ReturnsToAnsweringPeer()
    {
        var s = await _h.Running("161501");
        _ = s.AskAsync("161432", "вопрос", default);
        var perm = _h.Broker.AskAsync("161501", "Bash", JsonDocument.Parse("{}").RootElement, null, default);
        Assert.Equal(SessionState.WaitingPermission, s.State);

        _h.Broker.Resolve(_h.Broker.Pending("161501").Single().RequestId, true);
        await perm;
        Assert.Equal(SessionState.AnsweringPeer, s.State);
    }

    [Fact]
    public async Task NotRunning_Rejects_NoProcessStarted()
    {
        // Ревью I-1: остановленная сессия может быть открыта в терминале (--resume того же
        // разговора) — поднять её вопросом соседа значит дать разговору двух писателей.
        var s = _h.Manager.Create("161501");
        var ex = await Assert.ThrowsAsync<PeerAnswerException>(() => s.AskAsync("161432", "вопрос", default));
        Assert.Contains("не запущена", ex.Message);
        Assert.Empty(_h.Processes);
    }

    [Fact]
    public async Task WhileStopping_Rejects()
    {
        // Ревью I-3: вопрос в окне остановки/архива поднял бы новый процесс за «архивной» сессией.
        var s = await _h.Running("161501");
        var gate = new TaskCompletionSource();
        _h.Last.StopGate = gate;
        var stop = s.ArchiveAsync();

        var ex = await Assert.ThrowsAsync<PeerAnswerException>(() => s.AskAsync("161432", "вопрос", default));
        Assert.Contains("останавлива", ex.Message);
        gate.SetResult();
        await stop;
        Assert.Single(_h.Processes);
    }

    [Fact]
    public async Task Archived_Rejects()
    {
        var s = _h.Manager.Create("161501");
        await s.ArchiveAsync();
        var ex = await Assert.ThrowsAsync<PeerAnswerException>(() => s.AskAsync("161432", "вопрос", default));
        Assert.Contains("архив", ex.Message);
    }

    [Fact]
    public async Task PeerQuestion_SurvivesDeskRestart()
    {
        var s = await _h.Running("161501");
        _ = s.AskAsync("161432", "вопрос", default);
        var again = _h.New().Get("161501")!;
        var q = again.History.OfType<PeerQuestion>().Single();
        Assert.Equal(("161432", "вопрос"), (q.FromKey, q.Text));
    }
}
