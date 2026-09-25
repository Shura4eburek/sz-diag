using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.HubClient;

namespace SzDiag.Desk.Tests;

public class MainViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private static SessionInfo S(string sz, SessionStatus st = SessionStatus.Online, int reboots = 0, string activity = "")
        => new(sz, "10.0.0.5", "PC-" + sz, st, Now.AddHours(-1), Now, Activity: activity, RebootCount: reboots);

    private static HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = Now };

    private static MainViewModel New() => new(new HubPoller(new FakeHubApi(), new Clock()), new DeskUiState(), new Clock());

    [Fact]
    public void Apply_AddsItemsSortedBySz()
    {
        var vm = New();
        vm.Apply(Snap(S("161520"), S("161432")));
        Assert.Equal(new[] { "161432", "161520" }, vm.Items.Select(i => i.Sz));
    }

    [Fact]
    public void Apply_UpdatesInPlace_KeepsInstanceAndSelection()
    {
        var vm = New();
        vm.Apply(Snap(S("161432")));
        var item = vm.Items[0];
        vm.Selected = item;

        vm.Apply(Snap(S("161432", reboots: 3)));

        Assert.Same(item, vm.Items[0]);
        Assert.Same(item, vm.Selected);
        Assert.Equal(3, item.RebootCount);
        Assert.True(item.HasReboots);
    }

    [Fact]
    public void Apply_RemovesVanishedAndClearsSelection()
    {
        var vm = New();
        vm.Apply(Snap(S("161432"), S("161520")));
        vm.Selected = vm.Items.Single(i => i.Sz == "161520");

        vm.Apply(Snap(S("161432")));

        Assert.Equal("161432", Assert.Single(vm.Items).Sz);
        Assert.Null(vm.Selected);
    }

    [Fact]
    public void Item_Liveness_And_Subtitle()
    {
        var vm = New();
        vm.Apply(Snap(S("161432", activity: "OCCT Combined"), S("161501", SessionStatus.Offline)));
        var a = vm.Items.Single(i => i.Sz == "161432");
        var b = vm.Items.Single(i => i.Sz == "161501");
        Assert.Equal(SzLivenessState.Online, a.Liveness);
        Assert.Equal("OCCT Combined", a.Subtitle);
        Assert.Equal(SzLivenessState.LagSuspected, b.Liveness);
        Assert.Equal("PC-161501", b.Subtitle);
    }

    [Fact]
    public void ToggleInspector_PersistsInUiState()
    {
        var ui = new DeskUiState { InspectorOpen = true };
        var vm = new MainViewModel(new HubPoller(new FakeHubApi(), new Clock()), ui, new Clock());
        vm.IsInspectorOpen = false;
        Assert.False(ui.InspectorOpen);
    }

    [Fact]
    public void Apply_Transfers_HasTransfersFlag()
    {
        var vm = New();
        vm.Apply(HubSnapshot.Empty with { Transfers = new[] {
            new TransferInfo("r1", "161432", TransferDirection.Push, "occt", 100, 50, 10, Now, TransferState.Running) } });
        Assert.True(vm.HasTransfers);
        vm.Apply(HubSnapshot.Empty);
        Assert.False(vm.HasTransfers);
    }
}
