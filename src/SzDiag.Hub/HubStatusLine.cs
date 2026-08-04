using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Spectre.Console;
using SzDiag.ConsoleUi;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Всё, что нужно панели хаба. Собирается на каждом тике перерисовки.</summary>
public sealed record HubStatusContext(
    IReadOnlyList<SessionInfo> Sessions,
    string ListenUrls,
    string LanIp,
    string KbRoot,
    DateTimeOffset StartedAt,
    DateTimeOffset Now);

/// <summary>Две строки статуса хаба для липкой панели. Обязана уложиться в заданную
/// ширину: панель рисует строку как есть, перенос недопустим.</summary>
public static class HubStatusLine
{
    public static IReadOnlyList<string> Render(HubStatusContext ctx, int width)
    {
        var uptime = Elapsed.Format(ctx.Now - ctx.StartedAt);
        var first = $"[bold]sz-diag hub[/]  [green]●[/] слушает {Markup.Escape(ctx.ListenUrls)}" +
                    $"   [grey]LAN[/] {Markup.Escape(ctx.LanIp)}   [grey]аптайм[/] {uptime}";

        var online = ctx.Sessions.Where(s => s.Status == SessionStatus.Online)
            .OrderBy(s => s.Sz).ToList();

        string second;
        if (online.Count == 0)
        {
            second = "[dim]нет активных СЗ[/]";
        }
        else
        {
            var kbTail = $"   [grey]kb[/] {Markup.Escape(ctx.KbRoot)}";
            var budget = width - MarkupText.PlainLength(kbTail);
            var list = SessionList(online, ctx.Now, budget);
            // kb-хвост влезает только если после списка осталось место — иначе отбрасываем.
            second = MarkupText.PlainLength(list) + MarkupText.PlainLength(kbTail) <= width
                ? list + kbTail
                : list;
        }

        return new[] { MarkupText.Fit(first, width), MarkupText.Fit(second, width) };
    }

    /// <summary>«онлайн 3: 156864 (OCCT 42мин 00сек), 160176, 161288» с обрезкой по бюджету.</summary>
    private static string SessionList(IReadOnlyList<SessionInfo> online, DateTimeOffset now, int budget)
    {
        var prefix = $"[grey]онлайн[/] {online.Count}: ";
        var used = MarkupText.PlainLength(prefix);
        var parts = new List<string>();
        var shown = 0;

        foreach (var s in online)
        {
            var cell = Markup.Escape(s.Sz);
            if (!string.IsNullOrEmpty(s.Activity) && s.ActivitySince is { } since)
                cell += $" [yellow]({Markup.Escape(s.Activity)} {Elapsed.Format(now - since)})[/]";
            else if (!string.IsNullOrEmpty(s.Activity))
                cell += $" [grey]({Markup.Escape(s.Activity)})[/]";

            var addition = (shown == 0 ? 0 : 2) + MarkupText.PlainLength(cell);   // 2 — «, »
            var rest = online.Count - shown - 1;
            var tail = rest > 0 ? $" +{rest}".Length : 0;
            if (used + addition + tail > budget && shown > 0) break;

            parts.Add(cell);
            used += addition;
            shown++;
        }

        var text = prefix + string.Join(", ", parts);
        if (shown < online.Count) text += $" [dim]+{online.Count - shown}[/]";
        return text;
    }

    /// <summary>IPv4 первого рабочего не-loopback интерфейса — то, что писать в панель как
    /// адрес, по которому агенты видят hub. Определяется один раз при старте.</summary>
    public static string FindLanIp()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    return addr.Address.ToString();
                }
            }
        }
        catch { /* не критично — покажем прочерк */ }
        return "—";
    }
}
