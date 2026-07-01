using System.Text;
using SzDiag.Contracts;

namespace SzDiag.Cli;

public static class SessionTableRenderer
{
    public static string Render(IReadOnlyList<SessionInfo> sessions)
    {
        if (sessions.Count == 0) return "  (нет активных СЗ)";

        var sb = new StringBuilder();
        sb.AppendLine("  СЗ         Статус     IP               Хост");
        sb.AppendLine("  ────────── ────────── ──────────────── ────────────");
        foreach (var s in sessions.OrderBy(x => x.Sz))
        {
            var marker = s.Status == SessionStatus.Online ? "● online" : "○ offline";
            sb.AppendLine($"  {s.Sz,-10} {marker,-10} {s.Ip,-16} {s.Hostname}");
        }
        return sb.ToString();
    }
}
