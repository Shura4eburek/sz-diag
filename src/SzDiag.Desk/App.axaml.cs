using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.Views;
using SzDiag.HubClient;

namespace SzDiag.Desk;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DeskLog.Init();
            var opts = DeskOptions.Load();
            var http = new HttpClient { BaseAddress = new Uri(opts.HubBaseUrl) };
            var api = new HubApiClient(http, opts.ManagementToken);
            var uiPath = Path.Combine(AppContext.BaseDirectory, "desk-ui.json");
            var ui = DeskUiState.Load(uiPath);
            var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), ui, TimeProvider.System);
            desktop.MainWindow = new MainWindow(vm);
            desktop.Exit += (_, _) => ui.Save(uiPath);
            DeskLog.Write($"старт, hub {opts.HubBaseUrl}");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
