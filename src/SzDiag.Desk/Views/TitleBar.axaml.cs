using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace SzDiag.Desk.Views;

public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        MinButton.Click += (_, _) => Owner()!.WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximize();
        CloseButton.Click += (_, _) => Owner()!.Close();
        DragArea.PointerPressed += OnDragPressed;
        DragArea.DoubleTapped += (_, _) => ToggleMaximize();
    }

    private Window? Owner() => TopLevel.GetTopLevel(this) as Window;

    private void ToggleMaximize()
    {
        var w = Owner();
        if (w is null) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
            Owner()?.BeginMoveDrag(e);
    }
}
