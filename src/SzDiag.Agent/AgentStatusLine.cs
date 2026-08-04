using Spectre.Console;
using SzDiag.ConsoleUi;

namespace SzDiag.Agent;

/// <summary>Состояние агента для панели. Пересобирается на каждом тике перерисовки.</summary>
/// <param name="WatchdogAt">Момент срабатывания watchdog. null — watchdog не ставился (WinPE).</param>
/// <param name="LastHeartbeatOk">Последний удавшийся heartbeat. null — ни одного ещё не было.</param>
/// <param name="Mode">Пометка режима («WinPE») или пусто для обычного.</param>
public sealed record AgentStatusContext(
    string Sz,
    string HubUrl,
    int SshPort,
    DateTimeOffset? WatchdogAt,
    DateTimeOffset? BootTime,
    DateTimeOffset? LastHeartbeatOk,
    TimeSpan HeartbeatTimeout,
    string Mode,
    DateTimeOffset Now);

/// <summary>Две строки статуса агента. Обязана уложиться в заданную ширину.</summary>
public static class AgentStatusLine
{
    public static IReadOnlyList<string> Render(AgentStatusContext ctx, int width)
    {
        var mode = string.IsNullOrEmpty(ctx.Mode) ? "" : $" [grey]({Markup.Escape(ctx.Mode)})[/]";
        var hub = StripScheme(ctx.HubUrl);

        var first = $"[bold]СЗ {Markup.Escape(ctx.Sz)}[/]{mode}  {Link(ctx)}" +
                    $"   [grey]hub[/] {Markup.Escape(hub)}" +
                    $"   [grey]sshd[/] :{ctx.SshPort}" +
                    $"   [grey]watchdog[/] {Watchdog(ctx)}";

        var uptime = ctx.BootTime is { } boot ? Elapsed.Format(ctx.Now - boot) : "—";
        var second = "[green][[C]][/] закрыть СЗ   [grey][[Q]][/] выход" +
                     $"   [grey]uptime[/] {uptime}";

        return new[] { MarkupText.Fit(first, width), MarkupText.Fit(second, width) };
    }

    /// <summary>Статус связи с hub по свежести последнего heartbeat: свой признак, потому
    /// что SignalR молча переподключается и «живой объект» ничего не доказывает.</summary>
    private static string Link(AgentStatusContext ctx)
    {
        if (ctx.LastHeartbeatOk is not { } last) return "[yellow]● подключение…[/]";
        return ctx.Now - last <= ctx.HeartbeatTimeout
            ? "[green]● online[/]"
            : "[yellow]● переподключение[/]";
    }

    private static string Watchdog(AgentStatusContext ctx)
    {
        if (ctx.WatchdogAt is not { } at) return "—";
        return Elapsed.Format(at - ctx.Now);   // Elapsed сам зажимает отрицательное в 0
    }

    /// <summary>«http://192.168.1.10:5099» → «192.168.1.10:5099» — схема в панели только ест ширину.</summary>
    private static string StripScheme(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        var s = i >= 0 ? url[(i + 3)..] : url;
        return s.TrimEnd('/');
    }
}
