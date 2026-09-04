using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli sleep-cycle start|stop <СЗ>` — цикл «сон → RTC-пробуждение» как
/// воспроизводящий тест (промотано из пары рецептов `sleep-cycle-test.ps1`/
/// `sleep-cycle-stop.ps1`, бэклог п.225, СЗ 161498).
///
/// Забытый цикл 24.08 не остановился (стоп-файла не было) и воскрес 26.08 через 5 минут после
/// включения машины — окно бодрствования 90 с не даёт `szcli exec` доехать, снимать пришлось
/// офлайн из WinPE. Критерий из бэклога — остановка **одной командой при живом агенте**
/// (реконнект-очередь на hub для офлайн-случая — отдельная задача, требующая архитектурного
/// решения, здесь не делается). Умирает сам через `--max-hours` — предохранитель уже был в
/// рецепте, CLI его наследует как есть.</summary>
public static class SleepCycleCommand
{
    private const int TimeoutSeconds = 60;

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        // args = ["sleep-cycle", "start"|"stop", "<СЗ>", ...]
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (sub is not ("start" or "stop") || args.Length < 3) return Usage();

        var sz = args[2];
        if (!SzNumber.IsValid(sz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(sz)}.");
            return 2;
        }

        return sub == "start" ? await StartAsync(client, sz, args[3..]) : await StopAsync(client, sz);
    }

    private static async Task<int> StartAsync(IHubApiClient client, string sz, string[] rest)
    {
        var sleepMin = IntArg(rest, "--sleep-min") ?? 4;
        var awakeSec = IntArg(rest, "--awake-sec") ?? 90;
        var maxHours = IntArg(rest, "--max-hours") ?? 8;
        var confirmRisk = rest.Any(a => a.Equals("--confirm-risk", StringComparison.OrdinalIgnoreCase));

        var script = SleepCycleScript.BuildInstall(sz, sleepMin, awakeSec, maxHours, confirmRisk);
        var res = await client.ExecAsync(sz, script, TimeoutSeconds, default, detached: true);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }
        if (res.JobId is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Не удалось запустить цикл сна:[/] {res.StdErr}");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated(
            $"[green]СЗ {sz}: цикл сна запущен[/] (сон {sleepMin} мин, окно бодрствования {awakeSec} с, умрёт сам через {maxHours} ч, job {res.JobId}, версия payload {SleepCycleScript.Version})");
        AnsiConsole.MarkupLineInterpolated($"[grey]Проверить установку:[/] szcli exec {sz} --result {res.JobId}");
        AnsiConsole.MarkupLineInterpolated($"[grey]Остановить:[/] szcli sleep-cycle stop {sz}   [grey](только пока агент жив)[/]");
        if (!confirmRisk)
            AnsiConsole.MarkupLine("[grey]Если на машине пароль без автологина после сна — установка сама откажется стартовать; повтори с --confirm-risk, если риск осознан.[/]");
        return 0;
    }

    private static async Task<int> StopAsync(IHubApiClient client, string sz)
    {
        var res = await client.ExecAsync(sz, SleepCycleScript.StopScript, TimeoutSeconds, default, detached: false);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        var output = CliXml.Decode(res.StdOut).TrimEnd();
        if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
        if (res.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Остановка не удалась:[/] {CliXml.Decode(res.StdErr).TrimEnd()}");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: цикл сна остановлен.[/]");
        return 0;
    }

    private static int? IntArg(string[] args, string name)
    {
        var idx = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && args.Length > idx + 1 && int.TryParse(args[idx + 1], out var n) ? n : null;
    }

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli sleep-cycle start <СЗ> [--sleep-min N] [--awake-sec N] [--max-hours N] [--confirm-risk]
                воспроизводящий тест «сон -> RTC-пробуждение»; сам умирает через --max-hours (по умолч. 8 ч)
              szcli sleep-cycle stop <СЗ>
                остановить (только пока агент жив — офлайн-случай см. tools/recipes/client/pe-offline-kill-sleepcycle.ps1)
            """);
        return 2;
    }
}
