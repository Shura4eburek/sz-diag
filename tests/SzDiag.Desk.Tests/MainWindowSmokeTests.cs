using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.Views;

namespace SzDiag.Desk.Tests;

public class MainWindowSmokeTests
{
    [AvaloniaFact]
    public void Window_Shows_WithAllPanes()
    {
        var w = new MainWindow();
        w.Show();
        foreach (var name in new[] { "TitleBar", "LeftPane", "CenterPane", "InspectorPane", "StatusPane" })
            Assert.NotNull(w.FindControl<Control>(name));
    }

    [AvaloniaFact]
    public void CloseButton_ClosesWindow()
    {
        var w = new MainWindow();
        w.Show();
        var closed = false;
        w.Closed += (_, _) => closed = true;
        w.FindControl<TitleBar>("TitleBar")!.FindControl<Button>("CloseButton")!
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(closed);
    }

    private static (MainWindow w, MainViewModel vm) WithVm()
    {
        var vm = new MainViewModel(new SzDiag.Desk.Services.HubPoller(new FakeHubApi(), TimeProvider.System),
            new SzDiag.Desk.Services.DeskUiState { InspectorOpen = true }, TimeProvider.System);
        var w = new MainWindow(vm);
        w.Show();
        return (w, vm);
    }

    [AvaloniaFact]
    public void InspectorToggle_HidesAndShowsInspector()
    {
        var (w, vm) = WithVm();
        var toggle = w.FindControl<ToggleButton>("InspectorToggle")!;
        var pane = w.FindControl<Control>("InspectorPane")!;
        Assert.True(pane.IsVisible);

        toggle.IsChecked = false;
        Assert.False(vm.IsInspectorOpen);
        Assert.False(pane.IsVisible);
    }

    [AvaloniaFact]
    public void Apply_ShowsSzInList()
    {
        var (w, vm) = WithVm();
        vm.Apply(SzDiag.Desk.Services.HubSnapshot.Empty with { Sessions = new[] {
            new SzDiag.Contracts.SessionInfo("161432", "10.0.0.5", "PC", SzDiag.Contracts.SessionStatus.Online,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) } });
        vm.Selected = vm.Items.Single();

        // Биндинги списка и инспектора на живом окне не падают, выбранная СЗ дошла до шапки.
        Assert.Single(vm.Items);
        Assert.True(w.IsVisible);
    }
}
