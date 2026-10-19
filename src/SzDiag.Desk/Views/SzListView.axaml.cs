using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace SzDiag.Desk.Views;

public partial class SzListView : UserControl
{
    public SzListView() => InitializeComponent();

    /// <summary>После «Открыть заявку» курсор — сразу в поле номера: поле появляется только когда
    /// выбор снят, поэтому фокус ставим следующим тактом.</summary>
    private void OnOpenSzClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() =>
            (TopLevel.GetTopLevel(this) as Window)?.FindControl<TextBox>("OfflineSzBox")?.Focus(),
            DispatcherPriority.Background);
}
