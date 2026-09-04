using System.Linq;
using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli diag status <СЗ>` — на 160306 диагностика не дала отчёта (агента, судя по
/// всему, прибил антивирус), а CLI до этого момента ответил лишь «диагностика запущена»:
/// упавший прогон выглядел как пустая папка, и оператор 20 минут ждал вслепую, а потом ушёл
/// собирать всё руками по SSH (бэклог п.6/б.34a). Команда сводит воедино то, что уже знает
/// hub (текущая Activity сессии — обновляется на каждом шаге прогона) и то, что реально лежит
/// на диске (самый свежий diag.md), без нового round-trip к агенту.</summary>
public static class DiagStatusCommand
{
    public static async Task<int> RunAsync(IHubApiClient client, string sz, string reportsDir)
    {
        var sessions = await client.GetSessionsAsync();
        var session = sessions.FirstOrDefault(s => s.Sz.Equals(sz, StringComparison.OrdinalIgnoreCase));
        var latest = DiagCompletionWatcher.FindLatest(reportsDir, DateTime.MinValue);

        var verdict = BuildVerdict(session, latest, DateTimeOffset.Now);
        AnsiConsole.MarkupLine(verdict.Markup);
        return verdict.Code;
    }

    public sealed record Verdict(int Code, string Markup);

    /// <summary>Чистая логика вердикта — вынесена ради тестов (сеть и диск уже опрошены
    /// вызывающим кодом).</summary>
    public static Verdict BuildVerdict(SessionInfo? session, (string Path, long Bytes)? latest, DateTimeOffset now)
    {
        if (session is null)
            return new Verdict(1, "[red]СЗ не найдена[/] среди активных.");

        var lines = new List<string>();
        var activity = session.Activity ?? "";
        var failed = activity.Contains("ошибка", StringComparison.OrdinalIgnoreCase);
        var running = activity.StartsWith("диагностика:", StringComparison.OrdinalIgnoreCase);

        if (failed)
            lines.Add($"[red]Диагностика упала:[/] {Markup.Escape(activity)}");
        else if (running)
        {
            var since = session.ActivitySince is DateTimeOffset s
                ? $" ({SessionTableRenderer.FormatElapsed(now - s)})" : "";
            lines.Add($"[yellow]Диагностика идёт:[/] {Markup.Escape(activity)}{since}");
        }
        else if (activity.Length > 0)
            lines.Add($"[grey]Последняя активность:[/] {Markup.Escape(activity)}");
        else
            lines.Add("[grey]Активности не было — диагностику ещё не запускали в этой сессии.[/]");

        lines.Add(latest is { } f
            ? $"[grey]Свежий отчёт:[/] {f.Path} ({f.Bytes / 1024d:N1} КБ)"
            : "[grey]Отчётов ещё не было.[/]");

        return new Verdict(failed ? 1 : 0, string.Join("\n", lines));
    }
}
