using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>
/// `MarkupLineInterpolated` ЭКРАНИРУЕТ всё, что подставляется в `{…}`. Если внутри
/// подстановки уже есть разметка Spectre, она печатается буквально: на живой 162003
/// `szcli alive` выдал «ICMP: [green]отвечает[/]» и «ARP: [dim]записи нет[/]» вместо цвета.
/// Ошибка тихая — компилятор доволен, а видно её только глазами в выводе, поэтому ловим
/// её линтом по исходникам CLI.
///
/// Правило: теги в литеральной части строки — нормально (данные экранируются, как и задумано);
/// теги ВНУТРИ `{…}` — нет, там нужен обычный `MarkupLine`.
/// </summary>
public class MarkupInterpolationLintTests
{
    [Fact]
    public void NoSpectreMarkupInsideInterpolatedHoles()
    {
        var cliDir = Path.Combine(RepoRoot(), "src", "SzDiag.Cli");
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(cliDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("MarkupLineInterpolated")) continue;
                foreach (var hole in InterpolationHoles(lines[i]))
                {
                    // Признак — закрывающий тег `[/]`: парная разметка без него не бывает,
                    // а спутать его с индексатором (`args[1]`, `parts[i]`) невозможно.
                    if (hole.Contains("[/]"))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "разметка внутри подстановки MarkupLineInterpolated будет напечатана текстом — "
            + $"нужен MarkupLine: {string.Join(", ", offenders.Distinct())}");
    }

    /// <summary>Содержимое `{…}` в строке. Учитывает вложенные скобки и `{{`/`}}` (экранированная
    /// фигурная скобка — не подстановка).</summary>
    private static IEnumerable<string> InterpolationHoles(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '{') continue;
            if (i + 1 < line.Length && line[i + 1] == '{') { i++; continue; }

            var depth = 1;
            var start = i + 1;
            var j = start;
            for (; j < line.Length && depth > 0; j++)
            {
                if (line[j] == '{') depth++;
                else if (line[j] == '}') depth--;
            }
            if (depth == 0) yield return line[start..(j - 1)];
            i = j - 1;
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }
}
