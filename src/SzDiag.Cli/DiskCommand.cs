using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli disk scan <СЗ> [--map|--zone A-B] [--drive N]` — карта скорости чтения по
/// всему объёму накопителя точками + прицельный сплошной прогон по подозрительной зоне.
///
/// Промотано из рецепта `disk-zone-map.ps1` в CLI (бэклог п.200, СЗ 161972): клиент прямым
/// текстом назвал сценарий отказа («Victoria — диск 100%, скорость до 3 МБ/с, после этого
/// компьютер вылетел»), а `szcli test run` (OCCT/TM5/FurMark) накопитель вообще не трогает.
/// Запускается фоновой exec-задачей (`--detach`), потому что `map` по умолчанию идёт до
/// 20 минут, а `zone` — до 25: результат смотреть тем же путём, что и у `sensors`/`exec`
/// (`szcli exec &lt;СЗ&gt; --result &lt;jobId&gt;`).</summary>
public static class DiskCommand
{
    private const int DefaultDriveIndex = 0;
    private const int DefaultPoints = 300;
    private const int DefaultSampleMB = 16;
    private const int DefaultZoneStepMB = 256;
    private const int DefaultMapMaxMinutes = 20;
    private const int DefaultZoneMaxMinutes = 25;
    private const int DefaultSlowMBs = 200;

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        // args = ["disk", "scan", "<СЗ>", ...] или короче
        if (args.Length < 2 || !args[1].Equals("scan", StringComparison.OrdinalIgnoreCase))
            return Usage();
        if (args.Length < 3)
        {
            AnsiConsole.MarkupLine("[red]Не указан номер СЗ.[/]");
            return 2;
        }

        var sz = args[2];
        if (!SzNumber.IsValid(sz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(sz)}.");
            return 2;
        }

        var rest = args[3..];
        var drive = IntArg(rest, "--drive") ?? DefaultDriveIndex;
        var slow = IntArg(rest, "--slow-mbs") ?? DefaultSlowMBs;
        var zoneArg = ArgValue(rest, "--zone");

        string mode;
        double zoneStart = 0, zoneEnd = 0;
        int maxMinutes;
        if (zoneArg is not null)
        {
            mode = "zone";
            var parts = zoneArg.Split('-', 2);
            if (parts.Length != 2
                || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoneStart)
                || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out zoneEnd))
            {
                AnsiConsole.MarkupLineInterpolated($"[red]Не понял диапазон зоны:[/] {zoneArg} (ожидался формат «440-520», ГБ).");
                return 2;
            }
            maxMinutes = IntArg(rest, "--minutes") ?? DefaultZoneMaxMinutes;
        }
        else
        {
            mode = "map";
            maxMinutes = IntArg(rest, "--minutes") ?? DefaultMapMaxMinutes;
        }

        var points = IntArg(rest, "--points") ?? DefaultPoints;
        var sampleMB = IntArg(rest, "--sample-mb") ?? DefaultSampleMB;
        var zoneStepMB = IntArg(rest, "--zone-step-mb") ?? DefaultZoneStepMB;

        var script = DiskZoneMap.BuildScript(drive, mode, points, sampleMB, zoneStart, zoneEnd, zoneStepMB, maxMinutes, slow);
        var res = await client.ExecAsync(sz, script, maxMinutes * 60 + 30, default, detached: true);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (res.JobId is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Не удалось запустить скан:[/] {res.StdErr}");
            return 1;
        }

        var what = mode == "zone"
            ? $"сплошное чтение {zoneStart:N0}-{zoneEnd:N0} ГБ"
            : $"карта по {points} точкам";
        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {sz}: скан диска {drive} запущен[/] ({what}, до {maxMinutes} мин, job {res.JobId})");
        AnsiConsole.MarkupLineInterpolated($"[grey]Результат:[/] szcli exec {sz} --result {res.JobId}");
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && args.Length > idx + 1 ? args[idx + 1] : null;
    }

    private static int? IntArg(string[] args, string name)
        => ArgValue(args, name) is { } v && int.TryParse(v, out var n) ? n : null;

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli disk scan <СЗ> [--map] [--points N] [--sample-mb N]        карта скорости по всему диску (по умолчанию)
              szcli disk scan <СЗ> --zone НАЧАЛО-КОНЕЦ [--zone-step-mb N]      сплошное чтение зоны (ГБ, напр. 440-520)
              szcli disk scan <СЗ> [--drive N] [--minutes N] [--slow-mbs N]    общие параметры (диск/лимит времени/порог просадки)
              [grey]результат смотреть:[/] szcli exec <СЗ> --result <jobId>
            """);
        return 2;
    }
}
