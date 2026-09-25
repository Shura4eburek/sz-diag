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

    /// <summary>Для дизайнера XAML.</summary>
    public MainWindow() => InitializeComponent();

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
