using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SzDiag.Claude;
using SzDiag.Contracts;
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
    [ObservableProperty] private string? _detailsText;
    [ObservableProperty] private bool _detailsWarn;
    [ObservableProperty] private string? _limitsText;
    [ObservableProperty] private bool _limitsWarn;
    [ObservableProperty] private string? _limitsTip;

    /// <summary>С какой доли окна подсвечивать лимит.</summary>
    public const double LimitsWarnAt = 0.8;

    public void ApplyLimits(IReadOnlyDictionary<string, LimitsEntry> byProfile, DateTimeOffset now)
        => (LimitsText, LimitsWarn, LimitsTip) = FormatLimits(byProfile, now);

    /// <summary>`claude2 · 5ч 9% до 18:00 · 7д 57%` по каждому профилю. Окно, которое уже сбросилось
    /// без новых данных, — «сброшен»: старый процент врал бы.</summary>
    public static (string? Text, bool Warn, string? Tip) FormatLimits(IReadOnlyDictionary<string, LimitsEntry> byProfile,
        DateTimeOffset now)
    {
        if (byProfile.Count == 0) return (null, false, null);
        var warn = false;
        string Window(string label, RateLimitWindow? w, bool withReset)
        {
            if (w is null) return $"{label} —";
            if (now >= w.ResetsAt) return $"{label} сброшен";
            if (w.Utilization >= LimitsWarnAt) warn = true;
            var pct = $"{label} {Math.Round(w.Utilization * 100).ToString(CultureInfo.InvariantCulture)}%";
            return withReset ? $"{pct} до {w.ResetsAt.ToLocalTime():HH:mm}" : pct;
        }
        var parts = byProfile.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key} · {Window("5ч", p.Value.Info.FiveHour, true)} · {Window("7д", p.Value.Info.SevenDay, false)}")
            .ToList();
        var tip = string.Join("\n", byProfile.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p =>
            $"{p.Key}: неделя сбрасывается {p.Value.Info.SevenDay?.ResetsAt.ToLocalTime():dd.MM HH:mm}, " +
            $"данные на {p.Value.SeenAt.ToLocalTime():HH:mm} (обновляются с каждым ходом)"));
        return (string.Join("  |  ", parts), warn, tip);
    }

    public void Apply(HubSnapshot s, DateTimeOffset now)
    {
        (DetailsText, DetailsWarn) = FormatDetails(s.Status);
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

    /// <summary>Пакет агента, последний бэкап kb, туннель. Предупреждение — когда kb не уехал в
    /// remote или туннель не держится: оба случая иначе видны только в консоли hub.</summary>
    public static (string? Text, bool Warn) FormatDetails(HubStatus? s)
    {
        if (s is null) return (null, false);
        var parts = new List<string>();
        var warn = false;
        if (!string.IsNullOrEmpty(s.AgentPackageVersion)) parts.Add($"агент {s.AgentPackageVersion}");

        var kb = s.KbBackup;
        if (!kb.Enabled) parts.Add("kb: бэкап выключен");
        else if (kb.LastRunAt is null) parts.Add("kb: бэкапа ещё не было");
        else if (kb.Outcome is "Pushed" or "NoChanges") parts.Add($"kb {kb.LastRunAt.Value.ToLocalTime():HH:mm} ✓");
        else if (kb.Outcome == "CommittedNotPushed") { parts.Add("kb: не выгружен в remote"); warn = true; }
        else { parts.Add("kb: бэкап упал"); warn = true; }

        switch (s.Tunnel.State)
        {
            case TunnelStates.Running: parts.Add("туннель ✓"); break;
            case TunnelStates.NotFound: parts.Add("туннель: нет cloudflared"); warn = true; break;
            case TunnelStates.Restarting: parts.Add("туннель ✗ перезапуск"); warn = true; break;
        }
        return (string.Join(" · ", parts), warn);
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
