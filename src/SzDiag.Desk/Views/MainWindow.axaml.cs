using Avalonia.Controls;
using Avalonia.Threading;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Напоминание о висящем разрешении: ждём его сколько угодно (как терминал), но
    /// раз в 10 минут окно снова мигает, пока запрос не решён.</summary>
    private readonly DispatcherTimer _attention = new() { Interval = TimeSpan.FromMinutes(10) };

    /// <summary>Ширина, ниже которой инспектор ложится поверх чата: 268 + 520 (читаемый чат) + 308 + зазоры.</summary>
    private const double InspectorMinWindowWidth = 1110;

    /// <summary>Для дизайнера XAML.</summary>
    public MainWindow()
    {
        InitializeComponent();
        // Узкое окно: инспектор не сжимает чат, а ложится поверх него плавающим слоем (с тенью) —
        // переключатель в шапке продолжает работать при любой ширине.
        SizeChanged += (_, e) => SetInspectorOverlay(e.NewSize.Width < InspectorMinWindowWidth);
        // Развёрнутое окно без системной рамки уходит за экран на толщину рамки — отступаем на неё,
        // иначе край островов, иконка тулбара и статус-бар обрезаны.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == OffScreenMarginProperty || e.Property == WindowStateProperty)
                Root.Margin = WindowState == WindowState.Maximized ? OffScreenMargin : default;
        };
    }

    private void SetInspectorOverlay(bool overlay)
    {
        Grid.SetColumn(InspectorSlot, overlay ? 1 : 2);
        InspectorSlot.HorizontalAlignment = overlay ? Avalonia.Layout.HorizontalAlignment.Right
            : Avalonia.Layout.HorizontalAlignment.Stretch;
        InspectorPane.Classes.Set("overlay", overlay);
    }

    public MainWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        // Событие опроса приходит с пула потоков — в коллекции окна пишем только из UI-потока.
        vm.Poller.Changed += snap => Dispatcher.UIThread.Post(() => vm.Apply(snap));
        vm.AttentionNeeded += () => WindowAttention.Flash(this);
        _attention.Tick += (_, _) =>
        {
            if (vm.HasPendingPermissions) WindowAttention.Flash(this);
        };
        Opened += (_, _) =>
        {
            _ = vm.Poller.RunAsync(_stop.Token);
            _ = vm.Inspector?.RunLoopAsync(_stop.Token);
            _attention.Start();
        };
        Closed += (_, _) =>
        {
            _stop.Cancel();
            _attention.Stop();
        };
    }
}
