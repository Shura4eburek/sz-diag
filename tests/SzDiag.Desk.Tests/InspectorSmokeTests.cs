using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using SzDiag.Contracts;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.ViewModels.Inspector;
using SzDiag.Desk.Views;
using SzDiag.Kb;

namespace SzDiag.Desk.Tests;

public class InspectorSmokeTests
{
    private static (MainWindow W, MainViewModel Vm) Open(FakeHubApi api, FakeSzcliRunner szcli, string kbRoot)
    {
        var sessions = new[] { new SessionInfo("161432", "10.0.0.5", "PC", SessionStatus.Online, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) };
        api.Sessions = () => sessions;
        var inspector = InspectorViewModel.Create(new DeskTools(api, szcli, new KbPaths(kbRoot), a => a()), TimeProvider.System);
        var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), new DeskUiState(), TimeProvider.System,
            null, inspector);
        var w = new MainWindow(vm);
        w.Show();
        vm.Apply(HubSnapshot.Empty with { SessionsOkAt = DateTimeOffset.UtcNow, Sessions = sessions });
        return (w, vm);
    }

    /// <summary>Вкладки — в своей области имён (InspectorView), FindControl окна их не видит.</summary>
    private static TabControl Tabs(Window w)
        => w.GetLogicalDescendants().OfType<TabControl>().Single(t => t.Name == "InspectorTabs");

    [AvaloniaFact]
    public void TabsRender_RebootsTabLoads()
    {
        var api = new FakeHubApi
        {
            Reboots = sz => new RebootTimeline(sz, new[]
            {
                new RebootEvent(sz, DateTimeOffset.UtcNow, null, null, 600, "OCCT", ShutdownKind.HardOff),
            }, 600),
        };
        var (w, vm) = Open(api, new FakeSzcliRunner(), Path.GetTempPath());
        vm.Selected = vm.Items.Single();
        var tabs = Tabs(w);
        Assert.Equal(7, tabs.ItemCount);

        tabs.SelectedIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Inspector!.Reboots!.Rows);
    }

    [AvaloniaFact]
    public void ActionsTab_ConfirmBeforeClose()
    {
        var szcli = new FakeSzcliRunner();
        var (w, vm) = Open(new FakeHubApi(), szcli, Path.GetTempPath());
        vm.Selected = vm.Items.Single();
        Tabs(w).SelectedIndex = 6;
        Dispatcher.UIThread.RunJobs();

        vm.Inspector!.Actions!.CloseCommand.Execute(null);
        Assert.NotNull(vm.Inspector.Actions.PendingConfirm);
        Assert.Empty(szcli.Calls);
    }
}
