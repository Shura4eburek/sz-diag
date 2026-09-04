using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli stress stop <СЗ>` — снять ВСЮ нагрузку разом одной командой.
///
/// Боль (бэклог п.126, СЗ 161346): рецепт `disk-stress.ps1` без ограничений задавил очередь
/// ввода-вывода системного диска — машина перестала отвечать на `exec`, СЗ ушла в offline, и
/// со стороны это выглядело точь-в-точь как воспроизведение дефекта. Оператор успел остановить
/// только OCCT руками, а фоновая `exec --detach`-задача с дисковым стрессом продолжала давить
/// машину ещё 180 минут — «тест остановлен» с точки зрения человека, по факту нагрузка идёт.
///
/// Боль (бэклог п.183): `stop-stress.ps1` сознательно не трогал `lhmmon` (наблюдатель должен
/// жить, пока идёт тест) — из-за этого его процесс, задача `szdiag-lhm-<СЗ>` и драйвер
/// `R0lhmmon` переживали «остановку», и `wipe-tools.ps1` спотыкался об занятую папку.
///
/// `stress start <СЗ> --transient` — качели «нагрузка/простой» штатной командой (бэклог
/// п.154, #93, СЗ 161716): ровный стресс там прошёл чисто, хотя машина у клиента вырубалась
/// пачками — убивает не плато, а срыв нагрузки на переходе, который ровный тест не создаёт
/// вообще. Раньше это писалось руками (`stress-transient.ps1`) на каждой такой заявке.</summary>
public static class StressCommand
{
    private const int TimeoutSeconds = 180;
    private const int DefaultOnSeconds = 60;
    private const int DefaultOffSeconds = 40;
    private const double DefaultTotalHours = 1.5;
    private const int DefaultMemGb = 8;

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (args.Length < 3) return Usage();

        var sz = args[2];
        if (!SzNumber.IsValid(sz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(sz)}.");
            return 2;
        }

        return sub switch
        {
            "stop" => await StopAsync(client, sz),
            "start" => await StartAsync(client, sz, args[3..]),
            _ => Usage(),
        };
    }

    /// <summary>Транзиентный цикл нагрузка/простой — единственный режим `start` сейчас
    /// (ровный (плато) стресс уже есть отдельной командой `szcli test run`). Без `--transient`
    /// отказываем явно, а не молча запускаем что-то не то: этот класс дефектов («hard-off на
    /// простое») ровным тестом не ловится в принципе (бэклог п.154).</summary>
    private static async Task<int> StartAsync(IHubApiClient client, string sz, string[] rest)
    {
        if (!rest.Any(a => a.Equals("--transient", StringComparison.OrdinalIgnoreCase)))
        {
            AnsiConsole.MarkupLine(
                "[red]Нужен режим:[/] сейчас поддерживается только --transient (качели нагрузка/простой).");
            AnsiConsole.MarkupLine(
                "[grey]Ровный (плато) прогон — через szcli test run; он не покрывает симптом «hard-off на простое».[/]");
            return 2;
        }

        var onSeconds = IntArg(rest, "--on") ?? DefaultOnSeconds;
        var offSeconds = IntArg(rest, "--off") ?? DefaultOffSeconds;
        var hours = DoubleArg(rest, "--hours") ?? DefaultTotalHours;
        var memGb = IntArg(rest, "--mem") ?? DefaultMemGb;
        var withGpu = !rest.Any(a => a.Equals("--no-gpu", StringComparison.OrdinalIgnoreCase));

        var script = TransientStressScript.BuildScript(sz, onSeconds, offSeconds, hours, memGb, withGpu);
        var timeoutSeconds = (int)(hours * 3600) + 120;
        var res = await client.ExecAsync(sz, script, timeoutSeconds, default, detached: true);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (res.JobId is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Не удалось запустить транзиентный прогон:[/] {res.StdErr}");
            return 1;
        }

        var gpuLabel = withGpu ? "да" : "нет";
        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {sz}: транзиентный прогон запущен[/] (цикл {onSeconds}с нагрузка / {offSeconds}с простой, всего {hours:0.##} ч, GPU: {gpuLabel}, job {res.JobId})");
        AnsiConsole.MarkupLineInterpolated($"[grey]Ход/лог:[/] szcli exec {sz} --result {res.JobId} --tail 5");
        AnsiConsole.MarkupLineInterpolated($"[grey]Снять досрочно:[/] szcli stress stop {sz}");
        AnsiConsole.MarkupLine(
            "[yellow]Метка в лог пишется ПОСЛЕ каждого снятия нагрузки — упавшая машина оставит фазу отказа последней строкой.[/]");
        return 0;
    }

    /// <summary>Снять процессы стресс-тулов, `lhmmon`, фоновые `exec --detach`-задачи, задачи
    /// планировщика и драйверы инструментов — разом, одним вызовом.</summary>
    public static async Task<int> StopAsync(IHubApiClient client, string sz)
    {
        var script = ClientTraces.BuildStressStopScript(ClientTraces.SessionTasks(sz));
        var res = await client.ExecAsync(sz, script, TimeoutSeconds);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        var output = CliXml.Decode(res.StdOut).TrimEnd();
        if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
        if (res.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Не удалось снять нагрузку полностью:[/] {CliXml.Decode(res.StdErr).TrimEnd()}");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {sz}: нагрузка снята[/] (процессы, lhmmon, фоновые задачи, задачи планировщика, драйверы).");
        AnsiConsole.MarkupLineInterpolated(
            $"[grey]Файлы инструментов теперь свободны — можно:[/] szcli client cleanup {sz}");
        return 0;
    }

    private static string? ArgValue(string[] args, string name)
    {
        var idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && args.Length > idx + 1 ? args[idx + 1] : null;
    }

    private static int? IntArg(string[] args, string name)
        => ArgValue(args, name) is { } v && int.TryParse(v, out var n) ? n : null;

    private static double? DoubleArg(string[] args, string name)
        => ArgValue(args, name) is { } v
            && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : null;

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli stress start <СЗ> --transient [--on 60] [--off 40] [--hours 1.5] [--mem 8] [--no-gpu]
                качели «нагрузка/простой» (CPU+RAM y-cruncher + GPU FurMark) — дискриминатор для
                симптома «hard-off на простое»: ровный (плато) стресс такие срывы не создаёт вообще
              szcli stress stop <СЗ>   снять всю нагрузку разом: процессы, lhmmon, фоновые
                                        exec --detach задачи, задачи планировщика, драйверы
            """);
        return 2;
    }
}
