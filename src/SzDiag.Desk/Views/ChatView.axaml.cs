using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SzDiag.Desk.ViewModels;

namespace SzDiag.Desk.Views;

public partial class ChatView : UserControl
{
    private INotifyCollectionChanged? _items;

    public ChatView()
    {
        InitializeComponent();
        // Туннелем: TextBox с AcceptsReturn сам съел бы Enter как перенос строки.
        ChatInput.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => Hook();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        e.Handled = true;
        if (DataContext is ChatViewModel vm && vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    private void Hook()
    {
        if (_items is not null) _items.CollectionChanged -= OnItemsChanged;
        _items = (DataContext as ChatViewModel)?.Items;
        if (_items is not null) _items.CollectionChanged += OnItemsChanged;
        Dispatcher.UIThread.Post(() => FeedScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Прокручиваем вниз, только если читатель и так был внизу: новые события не должны
        // выдёргивать его из середины длинного вывода.
        var nearBottom = FeedScroll.Offset.Y >= FeedScroll.Extent.Height - FeedScroll.Viewport.Height - 80;
        if (nearBottom) Dispatcher.UIThread.Post(() => FeedScroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
