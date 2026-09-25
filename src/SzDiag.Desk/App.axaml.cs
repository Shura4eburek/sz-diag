using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using SzDiag.Desk.Services;
using SzDiag.Desk.ViewModels;
using SzDiag.Desk.ViewModels.Inspector;
using SzDiag.Desk.Views;
using SzDiag.HubClient;
using SzDiag.Kb;

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

            // Старт и остановка ядра — на пуле потоков: блокирующее ожидание async-кода прямо на
            // UI-потоке Avalonia повесило бы окно дедлоком на его же SynchronizationContext.
            var kbRoot = Path.IsPathRooted(opts.KbRoot) ? opts.KbRoot : Path.Combine(AppContext.BaseDirectory, opts.KbRoot);
            var kb = new KbPaths(kbRoot);
            var hw = new HwProfileCache(api, TimeProvider.System);
            var claude = Task.Run(() => DeskClaudeHost.StartAsync(opts, AppContext.BaseDirectory,
                sessions => new SzPeerDirectory(kb, hw, sessions))).GetAwaiter().GetResult();
            var chat = claude.Services(a => Dispatcher.UIThread.Post(a));

            var szcli = new Func<string?>(() => claude.Szcli);
            var tools = new DeskTools(api, new SzcliRunner(szcli), kb, a => Dispatcher.UIThread.Post(a));
            var inspector = InspectorViewModel.Create(tools, TimeProvider.System);

            var vm = new MainViewModel(new HubPoller(api, TimeProvider.System), ui, TimeProvider.System, chat,
                inspector, new FreezeProbe(szcli));
            desktop.MainWindow = new MainWindow(vm);
            desktop.Exit += (_, _) =>
            {
                ui.Save(uiPath);
                Task.Run(() => claude.DisposeAsync().AsTask()).GetAwaiter().GetResult();
                DeskLog.Write("выход: сессии остановлены");
            };
            DeskLog.Write($"старт, hub {opts.HubBaseUrl}");
        }
        base.OnFrameworkInitializationCompleted();
    }
}
