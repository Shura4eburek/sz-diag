using SzDiag.Claude;

namespace SzDiag.Claude.Tests;

public class PeerExchangeTests : IDisposable
{
    private readonly SessionHarness _h = new();
    private readonly FakePeerDirectory _dir = new();

    public void Dispose() => _h.Dispose();

    private static string Result => Fixture.Line("simple-turn.jsonl", e => e is TurnResult);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset At = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => At;
    }

    private PeerExchange New(PeerLimits? limits = null, TimeProvider? time = null)
        => new(_h.Manager, _dir, limits ?? PeerLimits.Default, time ?? TimeProvider.System);

    private void Peer(string key) => _dir.All.Add(new PeerInfo(key, "Ryzen 7 9800X3D · online", null));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task NotLive_ReturnsSummaryWithHint_NoQuestionSent()
    {
        _dir.Summaries["161501"] = "СЗ 161501 · вырубоны на EXPO 6000";
        var r = await New().AskAsync("161432", "161501", "что нашли?", live: false, default);
        Assert.True(r.Ok);
        Assert.StartsWith("СЗ 161501 · вырубоны на EXPO 6000", r.Text);
        Assert.Contains("live", r.Text);
        Assert.Empty(_h.Processes);
    }

    [Fact]
    public async Task NotLive_NothingInKb_Fails()
    {
        var r = await New().AskAsync("161432", "161501", "что нашли?", live: false, default);
        Assert.False(r.Ok);
        Assert.Contains("kb", r.Text);
    }

    [Fact]
    public async Task Self_Rejected()
    {
        var r = await New().AskAsync("161432", "161432", "?", live: false, default);
        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Live_AnswerReturned_PairActiveWhileInFlight()
    {
        Peer("161501");
        _h.Manager.Create("161501");
        var ex = New();
        var changes = 0;
        ex.Changed += () => changes++;

        var reply = ex.AskAsync("161432", "161501", "какой BIOS?", live: true, default);
        await WaitUntil(() => _h.Processes.Count == 1);
        Assert.Equal(new[] { ("161432", "161501") }, ex.Active);

        _h.Last.Emit(Result);
        var r = await reply;
        Assert.True(r.Ok);
        Assert.Empty(ex.Active);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Live_NoActiveSession_Fails()
    {
        var r = await New().AskAsync("161432", "161501", "?", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("нет активной сессии", r.Text);
    }

    [Fact]
    public async Task Depth1_AnsweringAskerCannotAsk()
    {
        Peer("161501");
        Peer("161600");
        _h.Manager.Create("161600");
        var asker = _h.Manager.Create("161501");
        _ = asker.AskAsync("161600", "вопрос", default);   // 161501 сейчас отвечает соседу

        var r = await New().AskAsync("161501", "161600", "встречный?", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("глубина 1", r.Text);
    }

    [Fact]
    public async Task CounterQuestion_Rejected()
    {
        Peer("161432");
        Peer("161501");
        _h.Manager.Create("161432");
        var b = _h.Manager.Create("161501");
        await b.SendAsync("работа оператора");   // B занят — вопрос A ждёт в очереди
        var ex = New();
        _ = ex.AskAsync("161432", "161501", "?", live: true, default);

        var r = await ex.AskAsync("161501", "161432", "встречный", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("сама ждёт", r.Text);
    }

    [Fact]
    public async Task OneLiveQuestionPerAsker()
    {
        Peer("161501");
        Peer("161600");
        _h.Manager.Create("161600");
        await _h.Manager.Create("161501").SendAsync("занят");
        var ex = New();
        _ = ex.AskAsync("161432", "161501", "?", live: true, default);

        var r = await ex.AskAsync("161432", "161600", "?", live: true, default);
        Assert.False(r.Ok);
        Assert.Contains("уже есть вопрос", r.Text);
    }

    [Fact]
    public async Task RateLimit_PerHour()
    {
        Peer("161501");
        _h.Manager.Create("161501");
        var clock = new Clock();
        var ex = New(new PeerLimits(2, TimeSpan.FromMinutes(5)), clock);
        for (var i = 0; i < 2; i++)
        {
            var reply = ex.AskAsync("161432", "161501", $"вопрос {i}", live: true, default);
            await WaitUntil(() => _h.Manager.Get("161501")!.IsAnsweringPeer);
            _h.Last.Emit(Result);
            Assert.True((await reply).Ok);
        }

        var over = await ex.AskAsync("161432", "161501", "третий", live: true, default);
        Assert.False(over.Ok);
        Assert.Contains("лимит 2", over.Text);

        clock.At += TimeSpan.FromHours(1);
        var again = ex.AskAsync("161432", "161501", "через час", live: true, default);
        await WaitUntil(() => _h.Manager.Get("161501")!.IsAnsweringPeer);
        _h.Last.Emit(Result);
        Assert.True((await again).Ok);
    }

    [Fact]
    public async Task Timeout_FailsAndDropsQueuedQuestion()
    {
        Peer("161501");
        var b = _h.Manager.Create("161501");
        await b.SendAsync("долгая работа");
        var r = await New(new PeerLimits(20, TimeSpan.FromMilliseconds(200))).AskAsync("161432", "161501", "?", live: true, default);

        Assert.False(r.Ok);
        Assert.Contains("не ответил", r.Text);
        Assert.Equal(0, b.PeerQueued);
    }

    [Fact]
    public void ListPeers_FormatsSimilarity()
    {
        _dir.All.Add(new PeerInfo("161501", "Ryzen 7 9800X3D · ASUS TUF GAMING B650M-PLUS", "CPU Ryzen 7 9800X3D, память 2×32 ГБ @ 6000"));
        var text = New().ListPeers("161432");
        Assert.Contains("161501 · Ryzen 7 9800X3D", text);
        Assert.Contains("похоже: CPU Ryzen 7 9800X3D", text);
        Assert.Contains("нет", New().ListPeers("161501"));   // кроме себя — никого
    }
}
