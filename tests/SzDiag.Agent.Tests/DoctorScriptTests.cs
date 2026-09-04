using System.Diagnostics;

namespace SzDiag.Agent.Tests;

/// <summary>R-I4 (ревью волны 1): freshness-guard в <c>tools\doctor.ps1</c> раньше сверял
/// свежесть `szcli`/`hub`/пакета агента только против нескольких ВРУЧНУЮ перечисленных путей
/// (`SzDiag.Cli`/`SzDiag.Contracts`), хотя `SzDiag.Cli` реально ссылается ещё на `SzDiag.Kb`,
/// `SzDiag.Hardware`, `SzDiag.ConsoleUi`, `SzDiag.Erp`, а `SzDiag.Hub` — на `SzDiag.ConsoleUi` и
/// `SzDiag.Kb`. Коммит, тронувший только `src/SzDiag.Kb`, давал «szcli свежий» на протухшем
/// exe — ровно тот отказ (бэклог п.198/211), ради которого сам guard делался.</summary>
public class DoctorScriptTests
{
    private static string RepoRoot() => TestPaths.RepoRoot();

    private static string DoctorScript() => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "doctor.ps1"));

    /// <summary>Реальный граф `<ProjectReference>` из .csproj — источник истины, с которым
    /// сверяем doctor.ps1, а не наоборот.</summary>
    private static IReadOnlyCollection<string> TransitiveProjectDirs(string csprojRelPath)
    {
        var root = RepoRoot();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(csprojRelPath.Replace('\\', '/'));
        var result = new List<string>();
        while (queue.Count > 0)
        {
            var rel = queue.Dequeue();
            if (!seen.Add(rel)) continue;
            var dir = Path.GetDirectoryName(rel)!.Replace('\\', '/');
            result.Add(dir);
            var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue;
            var xml = System.Xml.Linq.XDocument.Load(full);
            foreach (var include in xml.Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include")))
            {
                if (string.IsNullOrEmpty(include)) continue;
                var refFull = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(full)!, include));
                var refRel = Path.GetRelativePath(root, refFull).Replace('\\', '/');
                queue.Enqueue(refRel);
            }
        }
        return result;
    }

    /// <summary>doctor.ps1 обязан вычислять список путей через реальный граф зависимостей
    /// (`Get-TransitiveProjectDirs`), а не хранить его отдельным списком, который неизбежно
    /// расходится с .csproj при добавлении новой ссылки.</summary>
    [Fact]
    public void FreshnessGuard_ComputesPathsFromProjectReferences_NotHardcodedList()
    {
        var text = DoctorScript();

        Assert.Contains("function Get-TransitiveProjectDirs", text);
        Assert.Contains("Get-TransitiveProjectDirs \"src/SzDiag.Cli/SzDiag.Cli.csproj\"", text);
        Assert.Contains("Get-TransitiveProjectDirs \"src/SzDiag.Hub/SzDiag.Hub.csproj\"", text);
    }

    [Theory]
    [InlineData("src/SzDiag.Cli/SzDiag.Cli.csproj")]
    [InlineData("src/SzDiag.Hub/SzDiag.Hub.csproj")]
    [InlineData("src/SzDiag.Agent/SzDiag.Agent.csproj")]
    public void TransitiveProjectDirs_IncludesEveryRealDependency(string csproj)
    {
        // Замок против будущего дрейфа: сколько бы ссылок ни добавили в .csproj, обход
        // графа найдёт их все — в отличие от списка, который писали руками.
        var dirs = TransitiveProjectDirs(csproj);

        Assert.Contains("src/SzDiag.Contracts", dirs);
        if (csproj.Contains("Cli"))
        {
            Assert.Contains("src/SzDiag.Kb", dirs);
            Assert.Contains("src/SzDiag.Hardware", dirs);
            Assert.Contains("src/SzDiag.ConsoleUi", dirs);
            Assert.Contains("src/SzDiag.Erp", dirs);
        }
        if (csproj.Contains("Hub") || csproj.Contains("Agent"))
        {
            Assert.Contains("src/SzDiag.Kb", dirs);
            Assert.Contains("src/SzDiag.ConsoleUi", dirs);
        }
    }

    /// <summary>Вырезает ОДНУ функцию из текста скрипта по балансу фигурных скобок — без
    /// исполнения остального doctor.ps1 (проверки dist/git и т.п., не нужные тут).</summary>
    private static string ExtractFunction(string scriptText, string functionName)
    {
        var marker = "function " + functionName;
        var start = scriptText.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{functionName} не найдена в doctor.ps1");
        var braceStart = scriptText.IndexOf('{', start);
        var depth = 0;
        var i = braceStart;
        for (; i < scriptText.Length; i++)
        {
            if (scriptText[i] == '{') depth++;
            else if (scriptText[i] == '}') { depth--; if (depth == 0) { i++; break; } }
        }
        return scriptText[start..i];
    }

    /// <summary>Реально запускает `Get-TransitiveProjectDirs` ИЗ doctor.ps1 (не C#-копию) и
    /// возвращает её вывод. Под <c>pwsh</c>, а не <c>powershell.exe</c> 5.1: последний сейчас
    /// падает на `[System.IO.Path]::GetRelativePath` (review W2 C-6, чинится отдельно и не в
    /// этом пакете правок — tools/** не трогаем). T-3 требует реального исполнения функции и
    /// сверки множеств, а не привязки этого теста к судьбе параллельного C-6.</summary>
    private static IReadOnlyList<string> RunRealDoctorFunction(string csprojRelPath)
    {
        var root = RepoRoot();
        var funcBody = ExtractFunction(DoctorScript(), "Get-TransitiveProjectDirs");
        var script = $"$Root = '{root.Replace("'", "''")}'\n" + funcBody +
            $"\n(Get-TransitiveProjectDirs '{csprojRelPath.Replace("'", "''")}') -join \"`n\"";

        // Файлом (-File), а не стдином через -Command - : стдин-пайп молча давал пустой вывод
        // без ошибки (наблюдалось при отладке этого теста) — -File надёжнее.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"doctor-fn-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(20000), "pwsh не завершился вовремя");
            Assert.True(string.IsNullOrWhiteSpace(stderr), $"doctor.ps1: Get-TransitiveProjectDirs упала: {stderr}");

            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().Replace('\\', '/'))
                .Where(s => s.Length > 0)
                .ToList();
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
        }
    }

    /// <summary>review W2 T-3: раньше тест сверял doctor.ps1 с ручной C#-копией
    /// <see cref="TransitiveProjectDirs"/>, а сама PowerShell-функция не исполнялась и не
    /// сравнивалась вообще — «замок против дрейфа» ловил только дрейф C#-копии от .csproj,
    /// а реальная функция в doctor.ps1 могла вернуть пустой список, и оба теста остались бы
    /// зелёными. Теперь функция реально запускается под powershell.exe и сверяется с
    /// .csproj-графом как множество (порядок обхода у очереди/рекурсии не обязан совпадать).</summary>
    [Theory]
    [InlineData("src/SzDiag.Cli/SzDiag.Cli.csproj")]
    [InlineData("src/SzDiag.Hub/SzDiag.Hub.csproj")]
    [InlineData("src/SzDiag.Agent/SzDiag.Agent.csproj")]
    public void RealDoctorFunction_MatchesCsprojGraph(string csproj)
    {
        var expected = TransitiveProjectDirs(csproj).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = RunRealDoctorFunction(csproj).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.NotEmpty(actual);
        Assert.Equal(expected, actual);
    }
}
