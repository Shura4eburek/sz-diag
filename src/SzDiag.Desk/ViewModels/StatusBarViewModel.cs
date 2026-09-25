using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Desk.Services;

namespace SzDiag.Desk.ViewModels;

public sealed partial class StatusBarViewModel : ObservableObject
{
    /// <summary>Сколько задач в очереди thread pool уже считать захлёбом (бэклог п.50: при
    /// starvation очередь росла сотнями, в норме — единицы).</summary>
    public const long StarvationQueue = 100;

    [ObservableProperty] private bool _hubOk;
    [ObservableProperty] private string _hubText = "hub …";
    [ObservableProperty] private string? _staleText;

    public void Apply(HubSnapshot s, DateTimeOffset now)
    {
        if (s.IsStale)
        {
            HubOk = false;
            HubText = "hub не отвечает";
            StaleText = s.SessionsOkAt is { } at ? $"данные на {at.ToLocalTime():HH:mm}" : "данных ещё не было";
            return;
        }

        StaleText = null;
        var version = s.HubVersion ?? "?";
        if (s.Health is { PendingWorkItemCount: >= StarvationQueue } h)
        {
            HubOk = false;
            HubText = $"hub {version} · очередь {h.PendingWorkItemCount}";
            return;
        }
        HubOk = true;
        HubText = $"hub {version} · ok";
    }
}
