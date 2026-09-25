using Avalonia.Controls;
using Avalonia.Threading;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Views;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Для дизайнера XAML.</summary>
    public MainWindow() => InitializeComponent();

    public MainWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        // Событие опроса приходит с пула потоков — в коллекции окна пишем только из UI-потока.
        vm.Poller.Changed += snap => Dispatcher.UIThread.Post(() => vm.Apply(snap));
        Opened += (_, _) => _ = vm.Poller.RunAsync(_stop.Token);
        Closed += (_, _) => _stop.Cancel();
    }
}
