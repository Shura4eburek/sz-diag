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
/// `R0lhmmon` переживали «остановку», и `wipe-tools.ps1` спотыкался об занятую папку.</summary>
public static class StressCommand
{
    private const int TimeoutSeconds = 180;

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (args.Length < 3 || sub != "stop") return Usage();

        var sz = args[2];
        if (!SzNumber.IsValid(sz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(sz)}.");
            return 2;
        }

        return await StopAsync(client, sz);
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

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli stress stop <СЗ>   снять всю нагрузку разом: процессы, lhmmon, фоновые
                                        exec --detach задачи, задачи планировщика, драйверы
            """);
        return 2;
    }
}
