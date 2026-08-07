using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli client info|cleanup <СЗ>` — что мы оставили на клиентской машине и уборка
/// этого.
///
/// На 160306 после закрытия СЗ доступ снялся чисто, но на машине лежало **12 ГБ** мусора от
/// прогонов 27–28.07 и висели задачи `szdiag-iostress`/`szdiag-lhmmon` (бэклог п.56/п.99), а
/// на 161312 остался загруженный драйвер `R0lhmmon`, из-за которого не удалялась папка с
/// инструментами (п.88).</summary>
public static class ClientCommand
{
    private const int TimeoutSeconds = 180;

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        var sub = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        if (args.Length < 3 || sub is not ("info" or "cleanup")) return Usage();

        var sz = args[2];
        if (!SzNumber.IsValid(sz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(sz)}.");
            return 2;
        }

        return sub == "info" ? await InfoAsync(client, sz) : await CleanupAsync(client, sz);
    }

    /// <summary>Показать остатки. Ничего не трогает — только смотрит.</summary>
    public static async Task<int> InfoAsync(IHubApiClient client, string sz)
    {
        var res = await client.ExecAsync(sz, ClientTraces.BuildInventoryScript(), TimeoutSeconds);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        var leftovers = ClientTraces.FindLeftovers(CliXml.Decode(res.StdOut));
        if (leftovers.Count == 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: следов прогонов не осталось.[/]");
            return 0;
        }

        AnsiConsole.MarkupLineInterpolated($"[yellow]СЗ {sz}: на клиенте осталось:[/]");
        foreach (var item in leftovers) AnsiConsole.MarkupLineInterpolated($"  [yellow]•[/] {item}");
        AnsiConsole.MarkupLineInterpolated($"[grey]Убрать:[/] szcli client cleanup {sz}");
        return 1;
    }

    /// <summary>Убрать задачи, драйверы инструментов и наши временные каталоги. Задачи
    /// текущей сессии (sshd/watchdog/автостарт) не трогаются — иначе уборка обрубит канал.</summary>
    public static async Task<int> CleanupAsync(IHubApiClient client, string sz)
    {
        var keep = new[] { $"szdiag-sshd-{sz}", $"szdiag-watchdog-{sz}", $"szdiag-autostart-{sz}" };
        var res = await client.ExecAsync(sz, ClientTraces.BuildCleanupScript(keep), TimeoutSeconds);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        var output = CliXml.Decode(res.StdOut).TrimEnd();
        if (!string.IsNullOrEmpty(output)) Console.WriteLine(output);
        if (res.ExitCode != 0)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Уборка не удалась:[/] {CliXml.Decode(res.StdErr).TrimEnd()}");
            return 1;
        }

        AnsiConsole.MarkupLineInterpolated($"[green]СЗ {sz}: уборка выполнена.[/] Проверить: szcli client info {sz}");
        return 0;
    }

    private static int Usage()
    {
        AnsiConsole.MarkupLine("""
            Использование:
              szcli client info <СЗ>      что осталось на клиенте (задачи szdiag*, драйверы, файлы)
              szcli client cleanup <СЗ>   убрать это (кроме задач текущей сессии)
            """);
        return 2;
    }
}
