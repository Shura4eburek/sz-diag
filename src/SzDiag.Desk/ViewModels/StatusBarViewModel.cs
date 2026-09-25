using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Claude;
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
    [ObservableProperty] private string? _tokensText;

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
        var version = ShortVersion(s.HubVersion);
        if (s.Health is { PendingWorkItemCount: >= StarvationQueue } h)
        {
            HubOk = false;
            HubText = $"hub {version} · очередь {h.PendingWorkItemCount}";
            return;
        }
        HubOk = true;
        HubText = $"hub {version} · ok";
    }

    /// <summary>`/api/version` отдаёт строку для `szcli --version` целиком («hub 1.0.0+&lt;sha&gt;,
    /// сборка …»): в статусбар — только номер и короткий sha.</summary>
    private static string ShortVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "?";
        var v = raw.Trim();
        if (v.StartsWith("hub ", StringComparison.Ordinal)) v = v[4..];
        var comma = v.IndexOf(',');
        if (comma >= 0) v = v[..comma];
        var plus = v.IndexOf('+');
        if (plus >= 0 && v.Length > plus + 8) v = v[..(plus + 8)];
        return v;
    }

    public static string FormatTokens(TokenUsage u, decimal cost)
    {
        var t = u.Total;
        var n = t >= 1_000_000 ? (t / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M"
            : t >= 1_000 ? (t / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K"
            : t.ToString(CultureInfo.InvariantCulture);
        return $"токены сегодня: {n} · ${cost.ToString("0.00", CultureInfo.InvariantCulture)}";
    }
}
