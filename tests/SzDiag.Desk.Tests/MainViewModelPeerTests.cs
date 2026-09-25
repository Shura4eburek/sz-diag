using SzDiag.Claude;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Tests;

public class MainViewModelPeerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private readonly ChatHarness _h = new();
    private readonly ManualClock _clock = new() { Now = Now };

    public void Dispose() => _h.Dispose();

    private static SessionInfo S(string sz) => new(sz, "10.0.0.5", "PC-" + sz, SessionStatus.Online, Now, Now, BootTime: Now);
    private static HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = Now };

    private const string Other = "cpu=Intel(R) Core(TM) i5-12400F\nboard=Gigabyte Technology Co., Ltd.|B760M DS3H\nmem=2|32|3200|x";

    private MainViewModel New(Dictionary<string, string> hw)
    {
        var cache = new HwProfileCache(new FakeHubApi { Exec = (sz, _) => new ExecResult("r", 0, hw[sz], "") }, _clock);
        return new MainViewModel(new HubPoller(new FakeHubApi(), _clock), new DeskUiState(), _clock, _h.Services, hw: cache);
    }

    private static void ApplyTimes(MainViewModel vm, HubSnapshot snap, int n)
    {
        for (var i = 0; i < n; i++) vm.Apply(snap);   // профиль снимается по одной СЗ за опрос
    }

    [Fact]
    public void HwLine_AndSimilarBadge()
    {
        var vm = New(new() { ["161432"] = HwProfileTests.Tuf9800, ["161501"] = HwProfileTests.Tuf9800, ["161600"] = Other });
        ApplyTimes(vm, Snap(S("161432"), S("161501"), S("161600")), 4);

        var a = vm.Items.Single(i => i.Sz == "161432");
        Assert.Equal("Ryzen 7 9800X3D · ASUS TUF GAMING B650M-PLUS · 2×32 ГБ @ 6000", a.HwLine);
        Assert.Equal("≈161501", a.SimilarText);
        Assert.Contains("CPU Ryzen 7 9800X3D", a.SimilarTip);
        Assert.Equal("≈161432", vm.Items.Single(i => i.Sz == "161501").SimilarText);
        Assert.False(vm.Items.Single(i => i.Sz == "161600").HasSimilar);
    }

    [Fact]
    public async Task PeerExchange_MarksBothCards()
    {
        var vm = New(new() { ["161432"] = Other, ["161501"] = Other });
        vm.Apply(Snap(S("161432"), S("161501")));
        _h.Services.Sessions.Create("161432");
        _h.Services.Sessions.Create("161501");
        _h.PeerDir.All.Add(new PeerInfo("161501", "", null));

        var reply = _h.Peers.AskAsync("161432", "161501", "?", live: true, default);
        vm.Apply(Snap(S("161432"), S("161501")));   // точки сессий обновляет опрос (чаты не открыты)
        Assert.Equal("💬161501", vm.Items.Single(i => i.Sz == "161432").PeerText);
        Assert.Equal("💬161432", vm.Items.Single(i => i.Sz == "161501").PeerText);
        Assert.Equal(SessionState.AnsweringPeer, vm.Items.Single(i => i.Sz == "161501").SessionState);

        _h.Last.Exit(1);   // сосед упал — пара снимается
        Assert.False((await reply).Ok);
        Assert.False(vm.Items.Single(i => i.Sz == "161432").HasPeer);
    }
}
