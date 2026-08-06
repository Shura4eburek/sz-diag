using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli pull <СЗ> <путь…> [--max-mb N] [-r]` — забор файлов с клиента.
///
/// Боль (бэклог п.75, СЗ 161312): `C:\Windows\LiveKernelReports` держит в корне один файл на
/// 3 ГБ, а все 11 нужных дампов — в подпапках `WATCHDOG*`. Обход был нерекурсивным, поэтому
/// команду звали пять раз по маске на каждую подпапку. Вдобавок пропуск файла по `--max-mb`
/// (штатное поведение, ради которого лимит и задавался) давал exit code 1 — в скрипте это
/// читается как провал.</summary>
public static class PullCommand
{
    public sealed record Args(string Sz, IReadOnlyList<string> Paths, long? MaxBytes, bool Recurse);

    /// <summary>Разбор аргументов: несколько путей за один вызов, `--max-mb N`, `-r`/`--recurse`.
    /// Пути — всё, что не флаг и не значение флага.</summary>
    public static Args Parse(string[] args)
    {
        var sz = args[1];
        var paths = new List<string>();
        long? maxBytes = null;
        var recurse = false;

        for (var i = 2; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--max-mb", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && long.TryParse(args[i + 1], out var mb)) maxBytes = mb * 1024 * 1024;
                i++;
                continue;
            }
            if (a.Equals("-r", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--recurse", StringComparison.OrdinalIgnoreCase))
            {
                recurse = true;
                continue;
            }
            if (a.StartsWith('-')) continue;
            paths.Add(a);
        }

        return new Args(sz, paths, maxBytes, recurse);
    }

    /// <summary>Код возврата по итогу забора. Забрали хоть что-то — 0. Не забрали ничего, но
    /// всё отсеял лимит — тоже 0: именно этого от лимита и хотели. Ненулевой — когда файлов
    /// не нашлось вовсе или мешала настоящая ошибка (нет прав, файл побился).</summary>
    public static int ExitCodeFor(IReadOnlyList<PullSavedFile> files, bool anyError)
    {
        if (files.Any(f => !f.Skipped)) return 0;
        if (anyError) return 1;
        if (files.Count > 0 && files.All(f => f.OverLimit)) return 0;
        return 1;
    }

    public static async Task<int> RunAsync(IHubApiClient client, string[] args)
    {
        var parsed = Parse(args);
        if (parsed.Paths.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Не указан путь на клиенте.[/]");
            return 2;
        }

        var all = new List<PullSavedFile>();
        var anyError = false;

        foreach (var path in parsed.Paths)
        {
            var res = await client.PullAsync(parsed.Sz, path, parsed.MaxBytes, parsed.Recurse);
            if (res is null)
            {
                AnsiConsole.MarkupLineInterpolated($"[red]СЗ {parsed.Sz} не найдена[/] среди активных.");
                return 1;
            }
            if (!string.IsNullOrEmpty(res.Error))
            {
                // Ошибка по одному пути не должна отменять остальные: чаще всего это
                // «нет такой папки», а рядом заданный путь вполне рабочий.
                AnsiConsole.MarkupLineInterpolated($"[yellow]{path}:[/] {res.Error}");
                anyError = true;
                continue;
            }

            foreach (var f in res.Files)
            {
                var size = $"{f.Size / 1024d / 1024d:N1} МБ";
                if (f.Skipped)
                {
                    AnsiConsole.MarkupLineInterpolated($"[yellow]✗ {f.Name}[/] ({size}) — {f.SkipReason}");
                    if (!f.OverLimit) anyError = true;
                }
                else
                    AnsiConsole.MarkupLineInterpolated($"[green]✓ {f.Name}[/] ({size}) → {f.SavedPath}");
            }
            all.AddRange(res.Files);
        }

        var ok = all.Count(f => !f.Skipped);
        var overLimit = all.Count(f => f.OverLimit);
        var tail = overLimit > 0 ? $", по лимиту пропущено {overLimit}" : "";
        AnsiConsole.MarkupLineInterpolated($"[grey]Забрано файлов: {ok} из {all.Count}{tail}[/]");
        if (!parsed.Recurse && ok == 0 && all.Count == 0)
            AnsiConsole.MarkupLine("[grey]Подпапки не обходились — добавь[/] -r[grey], если файлы лежат глубже.[/]");

        return ExitCodeFor(all, anyError);
    }
}
