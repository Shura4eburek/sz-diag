using System.Globalization;
using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli maintenance <СЗ> "причина" [--from <время>] [--until <время>]` — метка
/// «с машиной работали руками».
///
/// Боль (бэклог п.100, СЗ 160636): hard-off 05.08 16:00:58 **в простое** переворачивал тактику
/// («грузить дальше» против «держать под приборами без нагрузки»), но в той же заявке питание
/// дважды снимали руками — рубильником и кнопкой. Было ли третий раз, нигде не записано, и
/// событие так и осталось неопределённым: либо ключевая улика, либо шум. Вечерами машины в
/// сервисе гасят, свет отключают, стенды перетыкают — каждый раз в журнале клиента появляется
/// подпись, неотличимая от дефекта.</summary>
public static class MaintenanceCommand
{
    /// <summary>Окно по умолчанию, если время не задано: «сейчас ± полчаса». Столько обычно
    /// длится ручная возня со стендом.</summary>
    public static readonly TimeSpan DefaultHalfWindow = TimeSpan.FromMinutes(30);

    public sealed record Args(string Sz, string Reason, DateTimeOffset From, DateTimeOffset Until, bool List);

    /// <summary>Разбор аргументов. `--from`/`--until` принимают `HH:mm` (сегодня) или полную
    /// дату `dd.MM HH:mm` / ISO — оператор пишет так, как ему удобно у машины.</summary>
    public static Args? Parse(string[] args, DateTimeOffset now)
    {
        if (args.Length < 2) return null;
        var sz = args[1];
        var list = args.Any(a => a.Equals("--list", StringComparison.OrdinalIgnoreCase));

        var reason = args.Skip(2).FirstOrDefault(a => !a.StartsWith('-')
            && !IsValueOfFlag(args, a)) ?? "";
        var from = ParseTime(Value(args, "--from"), now) ?? now - DefaultHalfWindow;
        var until = ParseTime(Value(args, "--until"), now) ?? now + DefaultHalfWindow;

        return new Args(sz, reason.Trim(), from, until, list);
    }

    private static bool IsValueOfFlag(string[] args, string candidate)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].StartsWith("--") && ReferenceEquals(args[i + 1], candidate)) return true;
        return false;
    }

    private static string? Value(string[] args, string flag)
    {
        var idx = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && args.Length > idx + 1 ? args[idx + 1] : null;
    }

    /// <summary>`18:30` → сегодня в 18:30; `06.08 18:30` → эта дата; ISO — как есть.</summary>
    public static DateTimeOffset? ParseTime(string? text, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();

        if (TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out var time))
            return new DateTimeOffset(now.Date, now.Offset) + time;

        foreach (var format in new[] { "dd.MM HH:mm", "dd.MM.yyyy HH:mm", "yyyy-MM-dd HH:mm" })
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var dt))
            {
                if (format == "dd.MM HH:mm") dt = dt.AddYears(now.Year - dt.Year);
                return new DateTimeOffset(dt, now.Offset);
            }
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var iso) ? iso : null;
    }

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        var parsed = Parse(args, DateTimeOffset.Now);
        if (parsed is null || !SzNumber.IsValid(parsed.Sz)) return Usage();

        if (parsed.List) return await ListAsync(client, parsed.Sz);

        if (string.IsNullOrWhiteSpace(parsed.Reason))
        {
            AnsiConsole.MarkupLine("[red]Нужна причина[/] — иначе метка через неделю не читается.");
            return 2;
        }

        var ok = await client.AddMaintenanceAsync(
            new MaintenanceWindow(parsed.Sz, parsed.From, parsed.Until, parsed.Reason));
        if (!ok)
        {
            AnsiConsole.MarkupLine("[red]Hub не принял метку.[/]");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {parsed.Sz}: отмечено обслуживание[/] {parsed.From.ToLocalTime():dd.MM HH:mm}–{parsed.Until.ToLocalTime():HH:mm} — {parsed.Reason}");
        AnsiConsole.MarkupLine("[grey]События питания в этом окне в szcli reboots идут как «обслуживание», а не как вырубон.[/]");
        return 0;
    }

    public static async Task<int> ListAsync(IHubApiClient client, string sz)
    {
        var windows = await client.GetMaintenanceAsync(sz);
        if (windows.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[grey]СЗ {sz}: окон обслуживания не отмечено.[/]");
            return 0;
        }

        foreach (var w in windows)
        {
            var active = w.IsActive(DateTimeOffset.Now) ? " [yellow](идёт сейчас)[/]" : "";
            AnsiConsole.MarkupLineInterpolated(
                $"  {w.From.ToLocalTime():dd.MM HH:mm}–{w.Until.ToLocalTime():dd.MM HH:mm}  {w.Reason}{active}");
        }
        return 0;
    }

    /// <summary>Предупреждение при закрытии СЗ: забытая метка скроет реальный дефект — та же
    /// ловушка, что с незакрытым `unfreeze`.</summary>
    public static async Task WarnIfActiveAsync(IHubApiClient client, string sz)
    {
        try
        {
            var windows = await client.GetMaintenanceAsync(sz);
            var active = windows.Where(w => w.IsActive(DateTimeOffset.Now)).ToList();
            if (active.Count == 0) return;

            AnsiConsole.MarkupLineInterpolated(
                $"[yellow]⚠ На СЗ {sz} висит метка обслуживания[/] — события питания в этом окне не считаются дефектом:");
            foreach (var w in active)
                AnsiConsole.MarkupLineInterpolated($"  [yellow]•[/] до {w.Until.ToLocalTime():dd.MM HH:mm} — {w.Reason}");
        }
        catch { /* закрытие СЗ уже произошло — предупреждение не должно его ломать */ }
    }

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli maintenance <СЗ> "причина" [[--from 18:30]] [[--until 19:15]]
                отметить, что с машиной работали руками (по умолчанию — «сейчас ±30 мин»)
              szcli maintenance <СЗ> --list      показать отмеченные окна
            """);
        return 2;
    }
}
