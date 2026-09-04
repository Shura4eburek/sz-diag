using SzDiag.Agent;

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

    /// <summary>Стенд для запуска doctor.ps1 целиком: изолированный `-Root` с минимальным
    /// набором файлов, до которых скрипт доходит по порядку (пакет агента → cli/hub → каталог
    /// инструментов, где и живёт проверка расписаний OCCT — бэклог п.124/#60).
    ///
    /// Живёт ПОД настоящим корнем репо (в `dist\`, который в .gitignore), а не во временном
    /// каталоге вовсе без `.git`: `git -C $Root log …` на пути без git-репозитория пишет в
    /// stderr, и даже с `2&gt;$null` PowerShell (при запуске `-File`, не интерактивно) всё
    /// равно поднимает это как завершающую ошибку под `$ErrorActionPreference = 'Stop'` —
    /// проверено эмпирически, не документированная тонкость самого PowerShell, не баг
    /// doctor.ps1 (в бою `$Root` всегда настоящий репозиторий).</summary>
    private static string BuildFixtureRoot(string toolsRoot, out string deployOcctDir, out string deployedOcctDir)
    {
        var root = Path.Combine(RepoRoot(), "dist", $".test-doctor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "dist", "host", "hub", "agent-dist"));
        File.WriteAllBytes(Path.Combine(root, "dist", "host", "hub", "agent-dist", "package.zip"), new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(root, "dist", "host", "hub"));
        File.WriteAllText(Path.Combine(root, "dist", "host", "hub", "appsettings.json"),
            $$"""{ "Hub": { "ToolsRoot": "{{toolsRoot.Replace("\\", "\\\\")}}" } } """);

        deployOcctDir = Path.Combine(root, "deploy", "occt");
        Directory.CreateDirectory(deployOcctDir);
        deployedOcctDir = Path.Combine(toolsRoot, "occt");
        Directory.CreateDirectory(deployedOcctDir);
        // Каталог инструментов должен выглядеть непустым (проверка "почти пусто" считает папки).
        Directory.CreateDirectory(Path.Combine(toolsRoot, "occt", "sub"));
        Directory.CreateDirectory(Path.Combine(toolsRoot, "tm5"));
        return root;
    }

    private static string RunDoctor(string fixtureRoot)
    {
        var doctorPath = Path.Combine(RepoRoot(), "tools", "doctor.ps1");
        var runner = new PowerShellRunner();
        var r = runner.Run($"& '{doctorPath}' -Root '{fixtureRoot}' 2>&1 | Out-String",
            throwOnError: false, timeout: TimeSpan.FromSeconds(30));
        return r.StdOut;
    }

    [Fact]
    public void OcctScheduleFreshness_MatchingFiles_ReportsOk()
    {
        var toolsRoot = Path.Combine(Path.GetTempPath(), $"sztools-{Guid.NewGuid():N}");
        var fixtureRoot = BuildFixtureRoot(toolsRoot, out var deployOcctDir, out var deployedOcctDir);
        try
        {
            File.WriteAllText(Path.Combine(deployOcctDir, "schedule.json"), """{ "Periods": [] }""");
            File.WriteAllText(Path.Combine(deployedOcctDir, "schedule.json"), """{ "Periods": [] }""");

            var output = RunDoctor(fixtureRoot);

            Assert.Contains("расписания OCCT в раздаче совпадают с репозиторием", output);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
            Directory.Delete(toolsRoot, recursive: true);
        }
    }

    [Fact]
    public void OcctScheduleFreshness_ContentDiffers_ReportsMismatch()
    {
        // Регрессия СЗ 161346 (бэклог п.124/#60): раздача 5+5 минут, репо — 30+30, узнали
        // только разбором occt-report.html постфактум.
        var toolsRoot = Path.Combine(Path.GetTempPath(), $"sztools-{Guid.NewGuid():N}");
        var fixtureRoot = BuildFixtureRoot(toolsRoot, out var deployOcctDir, out var deployedOcctDir);
        try
        {
            File.WriteAllText(Path.Combine(deployOcctDir, "schedule.json"),
                """{ "Periods": [ { "TestType": "Combined", "Duration": "00:30:00", "IsInfinite": false } ] }""");
            File.WriteAllText(Path.Combine(deployedOcctDir, "schedule.json"),
                """{ "Periods": [ { "TestType": "Combined", "Duration": "00:05:00", "IsInfinite": false } ] }""");

            var output = RunDoctor(fixtureRoot);

            Assert.Contains("расписания OCCT в раздаче расходятся с репозиторием", output);
            Assert.Contains("schedule.json", output);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
            Directory.Delete(toolsRoot, recursive: true);
        }
    }

    [Fact]
    public void OcctScheduleFreshness_MissingInDeployment_ReportsMismatch()
    {
        var toolsRoot = Path.Combine(Path.GetTempPath(), $"sztools-{Guid.NewGuid():N}");
        var fixtureRoot = BuildFixtureRoot(toolsRoot, out var deployOcctDir, out _);
        try
        {
            File.WriteAllText(Path.Combine(deployOcctDir, "schedule-long.json"),
                """{ "Periods": [] }""");
            // deployedOcctDir специально остаётся без schedule-long.json.

            var output = RunDoctor(fixtureRoot);

            Assert.Contains("нет в раздаче", output);
        }
        finally
        {
            Directory.Delete(fixtureRoot, recursive: true);
            Directory.Delete(toolsRoot, recursive: true);
        }
    }
}
