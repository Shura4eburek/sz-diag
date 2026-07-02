using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public static class SessionTableRenderer
{
    public static Table Render(IReadOnlyList<SessionInfo> sessions)
    {
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("СЗ");
        table.AddColumn("Статус");
        table.AddColumn("IP");
        table.AddColumn("Хост");

        if (sessions.Count == 0)
        {
            table.AddRow("[dim]нет активных СЗ[/]", "", "", "");
            return table;
        }

        foreach (var s in sessions.OrderBy(x => x.Sz))
        {
            var status = s.Status == SessionStatus.Online
                ? "[green]● online[/]"
                : "[grey]○ offline[/]";
            table.AddRow(s.Sz, status, s.Ip, s.Hostname);
        }
        return table;
    }
}
