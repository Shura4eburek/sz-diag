using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.ViewModels.Inspector;

namespace SzDiag.Desk.Tests;

public class MainViewModelInspectorTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly FakeInspectorTab _reboots = new(null, onReboot: true);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szinsp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private SessionInfo S(string sz, int reboots = 0) => new(sz, "10.0.0.5", "PC", SessionStatus.Online,
        _clock.Now.AddHours(-1), _clock.Now, RebootCount: reboots);

    private HubSnapshot Snap(params SessionInfo[] s) => HubSnapshot.Empty with { Sessions = s, SessionsOkAt = _clock.Now };

    private MainViewModel New(InspectorViewModel inspector, FreezeProbe? freeze = null)
        => new(new HubPoller(new FakeHubApi(), _clock), new DeskUiState(), _clock, null, inspector, freeze);

    [Fact]
    public void Selected_DrivesInspector()
    {
        var inspector = new InspectorViewModel(new IInspectorTab?[] { null, _reboots }, _clock);
        var vm = New(inspector);
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        Assert.Equal("161432", inspector.Sz);
        vm.Selected = null;
        Assert.Null(inspector.Sz);
    }

    [Fact]
    public void RebootOfSelected_RefreshesRebootTab()
    {
        var inspector = new InspectorViewModel(new IInspectorTab?[] { null, _reboots }, _clock) { SelectedIndex = 1 };
        var vm = New(inspector);
        vm.Apply(Snap(S("161432")));
        vm.Selected = vm.Items.Single();
        vm.Apply(Snap(S("161432", reboots: 1)));
        Assert.Equal(2, _reboots.Refreshed.Count);
    }

    [Fact]
    public void FreezeBadge_FromProbe()
    {
        var cmd = Path.Combine(_dir, "szcli.cmd");
        Directory.CreateDirectory(Path.Combine(_dir, "cli", "freeze"));
        File.WriteAllText(Path.Combine(_dir, "cli", "freeze", "161432.json"), "{}");
        var vm = New(new InspectorViewModel(new IInspectorTab?[] { null }, _clock), new FreezeProbe(() => cmd));

        vm.Apply(Snap(S("161432"), S("161501")));

        Assert.True(vm.Items.Single(i => i.Sz == "161432").IsFrozen);
        Assert.False(vm.Items.Single(i => i.Sz == "161501").IsFrozen);
    }
}
