using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

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

    private Window? _watched;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _watched = Owner();
        if (_watched is null) return;
        _watched.PropertyChanged += OnWindowPropertyChanged;
        SyncMaxIcon(_watched.WindowState);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_watched is not null) _watched.PropertyChanged -= OnWindowPropertyChanged;
        _watched = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs a)
    {
        if (a.Property == Window.WindowStateProperty && _watched is not null) SyncMaxIcon(_watched.WindowState);
    }

    /// <summary>В развёрнутом окне «Развернуть» становится «Восстановить» — и иконкой, и подсказкой.</summary>
    private void SyncMaxIcon(WindowState state)
    {
        var max = state == WindowState.Maximized;
        if (this.TryFindResource(max ? "Icon.Restore" : "Icon.Max", out var g) && g is Geometry geo)
            MaxIcon.Data = geo;
        ToolTip.SetTip(MaxButton, max ? "Восстановить" : "Развернуть");
    }

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
