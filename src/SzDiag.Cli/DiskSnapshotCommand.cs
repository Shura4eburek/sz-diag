using Spectre.Console;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>`szcli disk snapshot <СЗ> --label "<текст>"` — см. <see cref="DiskSnapshotScript"/>.
/// Метка **обязательна** (как у `test run --config`, бэклог п.213): снимок без метки «до/после»
/// через неделю нечитаем — непонятно, было это до переустановки или после.</summary>
public static class DiskSnapshotCommand
{
    private const int TimeoutSeconds = 300;

    public static async Task<int> RunAsync(IHubApiClient client, string[] args, string baseDir)
    {
        // args = ["snapshot", "<СЗ>", "--label", "<текст>", ...] — вызывается из
        // Program.cs как DiskSnapshotCommand.RunAsync(client, args[1..], baseDir).
        if (args.Length < 2)
        {
            Usage();
            return 2;
        }
        var sz = args[1];
        if (!SzNumber.IsValid(sz))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]Неверный номер СЗ:[/] {SzNumber.Explain(sz)}.");
            return 2;
        }

        var labelIdx = Array.FindIndex(args, a => a.Equals("--label", StringComparison.OrdinalIgnoreCase));
        if (labelIdx < 0 || labelIdx + 1 >= args.Length || string.IsNullOrWhiteSpace(args[labelIdx + 1]))
        {
            AnsiConsole.MarkupLine("[red]Нужна метка:[/] szcli disk snapshot <СЗ> --label \"до переустановки\"");
            return 2;
        }
        var label = args[labelIdx + 1];

        var res = await client.ExecAsync(sz, DiskSnapshotScript.Build(), TimeoutSeconds);
        if (res is null)
        {
            AnsiConsole.MarkupLineInterpolated($"[red]СЗ {sz} не найдена[/] среди активных.");
            return 1;
        }

        var stdout = CliXml.Decode(res.StdOut);
        var takenAt = DateTimeOffset.UtcNow;
        var fileName = DiskSnapshotNaming.BuildFileName(takenAt, label);
        var dir = Path.Combine(baseDir, "disk-snapshots", sz);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);

        var header = $"SNAPSHOT {takenAt:yyyy-MM-dd HH:mm:ss} UTC — СЗ {sz} — метка: {label}\r\n\r\n";
        await File.WriteAllTextAsync(path, header + stdout, new System.Text.UTF8Encoding(true));

        AnsiConsole.MarkupLineInterpolated($"[green]Снимок сохранён:[/] {path}");
        if (!string.IsNullOrEmpty(res.StdErr))
            AnsiConsole.MarkupLineInterpolated($"[yellow]stderr:[/] {CliXml.Decode(res.StdErr).TrimEnd()}");
        if (res.ExitCode != 0)
            AnsiConsole.MarkupLineInterpolated($"[yellow]exit code:[/] {res.ExitCode}");
        return 0;
    }

    private static void Usage() => AnsiConsole.MarkupLine("""
        Использование:
          szcli disk snapshot <СЗ> --label "до переустановки"   карта скоростей + SMART + журнал в один файл
        """);
}

/// <summary>Имя файла снимка: сортируется по времени, метка читается в имени без похода в
/// содержимое файла (`ls disk-snapshots\<СЗ>` уже показывает «было/стало»).</summary>
public static class DiskSnapshotNaming
{
    public static string BuildFileName(DateTimeOffset takenAt, string label)
    {
        var safeLabel = new string(label.Trim().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (safeLabel.Contains("--")) safeLabel = safeLabel.Replace("--", "-");
        safeLabel = safeLabel.Trim('-');
        if (safeLabel.Length == 0) safeLabel = "snapshot";
        if (safeLabel.Length > 60) safeLabel = safeLabel[..60];
        return $"{takenAt:yyyyMMdd-HHmmss}-{safeLabel}.txt";
    }
}
