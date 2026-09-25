using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
