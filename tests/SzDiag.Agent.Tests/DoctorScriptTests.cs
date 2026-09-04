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
}
