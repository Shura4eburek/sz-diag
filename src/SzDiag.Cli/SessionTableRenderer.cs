using Spectre.Console;
using SzDiag.ConsoleUi;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public static class SessionTableRenderer
{
    public static Table Render(IReadOnlyList<SessionInfo> sessions, DateTimeOffset? now = null)
    {
        var nowV = now ?? DateTimeOffset.Now;
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("СЗ");
        table.AddColumn("Статус");
        table.AddColumn("IP");
        table.AddColumn("Хост");
        table.AddColumn("Uptime");
        table.AddColumn("Активность");

        if (sessions.Count == 0)
        {
            table.AddRow("[dim]нет активных СЗ[/]", "", "", "", "", "");
            return table;
        }

        foreach (var s in sessions.OrderBy(x => x.Sz))
        {
            // Без юникод-глифов (●/○) — не в каждом шрифте консоли есть их отрисовка,
            // из-за чего колонка резервирует место под невидимый символ и текст съезжает.
            // Фиксированная ширина ("online " с хвостовым пробелом) — чтобы колонка не
            // "гуляла" между кадрами в live-перерисовке (szcli watch).
            var status = s.Status == SessionStatus.Online
                ? "[green]online [/]"
                : "[grey]offline[/]";
            table.AddRow(s.Sz, status, s.Ip, s.Hostname, UptimeCell(s, nowV), ActivityCell(s, nowV));
        }
        return table;
    }

    /// <summary>Ячейка uptime: сколько машина работает с загрузки ОС. Свежий ребут (менее часа
    /// назад) подсвечивается — под стресс-тестом это главный сигнал «клиент реально упал»,
    /// в отличие от пропавшего heartbeat, который под нагрузкой врёт. Счётчик вырубонов
    /// висит рядом постоянно: иначе о них узнаёшь, только если специально спросишь
    /// (на 160306 вырубон на стенде заметили через неделю — бэклог п.55).</summary>
    private static string UptimeCell(SessionInfo s, DateTimeOffset now)
    {
        if (s.BootTime is not DateTimeOffset boot) return "[dim]—[/]";
        var up = FormatElapsed(now - boot);
        var counter = s.RebootCount > 0 ? $" [red]⚡{s.RebootCount}[/]" : "";
        if (s.LastRebootAt is DateTimeOffset reboot && now - reboot < TimeSpan.FromHours(1))
            return $"[red]{up} (ребут {reboot.ToLocalTime():HH:mm})[/]{counter}";
        return $"[grey]{up}[/]{counter}";
    }

    /// <summary>Ячейка активности: идущий тест с тикающим временем, простой с меткой, или «—».</summary>
    private static string ActivityCell(SessionInfo s, DateTimeOffset now)
    {
        if (s.Status == SessionStatus.Offline || string.IsNullOrEmpty(s.Activity))
            return "[dim]—[/]";

        var text = Markup.Escape(s.Activity);
        if (s.ActivitySince is DateTimeOffset since)
            return $"[yellow]{text} {FormatElapsed(now - since)}[/]";
        return $"[grey]{text}[/]";
    }

    /// <summary>Человекочитаемое время. Формат общий с липкой панелью — см. <see cref="Elapsed"/>.</summary>
    public static string FormatElapsed(TimeSpan t) => Elapsed.Format(t);
}
