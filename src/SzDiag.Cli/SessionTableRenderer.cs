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
            table.AddRow(s.Sz, StatusCell(s, nowV), s.Ip, s.Hostname, UptimeCell(s, nowV), ActivityCell(s, nowV));
        }
        return table;
    }

    /// <summary>Порог, после которого молчание уже не спишешь на лаг heartbeat под нагрузкой:
    /// exec может не отвечать минутами, пока машина жива, но не десять минут подряд. До этого
    /// порога — вероятно лаг, после — вероятно реальный отказ (подтверждается только сменой
    /// boot-time при реконнекте — «offline» само по себе вырубоном не является, бэклог п.42).</summary>
    public static readonly TimeSpan LikelyFailureThreshold = TimeSpan.FromMinutes(10);

    /// <summary>Ячейка статуса: живая СЗ — просто «online», офлайн — с явной меткой «лаг?»
    /// или «ВЫРУБОН?», а не голое «offline», за которым раньше это различие было видно
    /// только по счётчику ⚡N и подсветке uptime, а не в самом статусе (бэклог п.42).</summary>
    private static string StatusCell(SessionInfo s, DateTimeOffset now)
    {
        // Агент в session 0 (автостарт-задача под SYSTEM) — GUI-операции там ломаются молча
        // (Start-Process без задачи-обхода, скриншот), не гадать об этом заново после каждого
        // ребута (бэклог п.220).
        var sessionZero = s.AgentInSessionZero ? " [yellow]session 0[/]" : "";

        // Без юникод-глифов (●/○) — не в каждом шрифте консоли есть их отрисовка, из-за
        // чего колонка резервирует место под невидимый символ и текст съезжает.
        // Неудачный watchdog/headless-откат (бэклог п.59) обязан выглядеть иначе, чем
        // штатный offline: доступ (sshd, учётка, фаервол) мог остаться на клиенте навсегда.
        if (!string.IsNullOrEmpty(s.RevertNote)) return "[red]⚠ откат[/]";
        if (s.Status == SessionStatus.Online) return $"[green]online[/]{sessionZero}";

        var silentFor = now - s.LastHeartbeat;
        return silentFor >= LikelyFailureThreshold
            ? $"[red]offline (ВЫРУБОН?)[/]{sessionZero}"
            : $"[grey]offline (лаг?)[/]{sessionZero}";
    }

    /// <summary>Ячейка uptime: сколько машина работает с загрузки ОС. Свежий ребут (менее часа
    /// назад) подсвечивается — под стресс-тестом это главный сигнал «клиент реально упал»,
    /// в отличие от пропавшего heartbeat, который под нагрузкой врёт. Счётчик вырубонов
    /// висит рядом постоянно: иначе о них узнаёшь, только если специально спросишь
    /// (на 160306 вырубон на стенде заметили через неделю — бэклог п.55).</summary>
    private static string UptimeCell(SessionInfo s, DateTimeOffset now)
    {
        if (s.BootTime is not DateTimeOffset boot) return "[dim]—[/]";

        // Boot-time в будущем = недостоверный источник: WinPE стартует с дефолтной таймзоной
        // (Pacific) и отдаёт время со смещением -08:00, из-за чего разность отрицательная и
        // рендерилась как «0сек» на машине, стоявшей час (бэклог п.90).
        if (boot - now > TimeSpan.FromMinutes(5))
            return "[yellow]— (boot-time в будущем: часы клиента)[/]";

        var counter = s.RebootCount > 0 ? $" [red]⚡{s.RebootCount}[/]" : "";

        // У offline-СЗ время «с загрузки» замораживаем на последнем контакте: растущий счётчик
        // читается как признак живой машины, хотя связи нет (бэклог п.89).
        if (s.Status == SessionStatus.Offline)
        {
            var frozen = FormatElapsed(s.LastHeartbeat - boot);
            var silent = FormatElapsed(now - s.LastHeartbeat);
            return $"[grey]{frozen}[/] [dim](нет связи {silent})[/]{counter}";
        }

        var up = FormatElapsed(now - boot);
        if (s.LastRebootAt is DateTimeOffset reboot && now - reboot < TimeSpan.FromHours(1))
            return $"[red]{up} (ребут {reboot.ToLocalTime():HH:mm})[/]{counter}";
        return $"[grey]{up}[/]{counter}";
    }

    /// <summary>Ячейка активности: идущий тест с тикающим временем, простой с меткой, или «—».</summary>
    private static string ActivityCell(SessionInfo s, DateTimeOffset now)
    {
        // Провал отката важнее любой обычной активности — не прятать его под «—».
        if (!string.IsNullOrEmpty(s.RevertNote))
            return $"[red]откат не завершён: {Markup.Escape(s.RevertNote)}[/]";
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
