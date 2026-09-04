using System.IO;
using System.Linq;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Сорок минут на 161312 ушло на «FurMark не запускается», а причина была в
/// приоритете операторов PowerShell: запятая связывает сильнее «+» (бэклог п.77).</summary>
public class ScriptLintTests
{
    [Fact]
    public void ConcatInsideArray_IsWarnedAbout()
    {
        var script = """
            $fm = 'C:\tools\furmark'
            $bl = @(
              '@echo off',
              'cd /d "' + $fm + '"',
              'furmark.exe --demo furmark-gl'
            )
            """;

        var warnings = ScriptLint.Check(script);

        Assert.NotEmpty(warnings);
        Assert.Contains("запятая связывает сильнее", warnings[0]);
    }

    [Fact]
    public void ParenthesisedConcat_IsFine()
    {
        var script = """
            $bl = @(
              '@echo off',
              ('cd /d "' + $fm + '"'),
              'furmark.exe'
            )
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    [Fact]
    public void PlainLiterals_AreFine()
    {
        var script = """
            $lines = @(
              '$smi = "C:\Windows\System32\nvidia-smi.exe"',
              '& $smi --query-gpu=utilization.gpu --format=csv'
            )
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    [Fact]
    public void SimpleConcatOutsideArray_IsFine()
    {
        // Обычная конкатенация без запятых ничего не ломает.
        Assert.Empty(ScriptLint.Check("$p = 'C:\\a' + $x + '\\b'\nWrite-Output $p"));
    }

    [Fact]
    public void EmptyScript_IsFine()
    {
        Assert.Empty(ScriptLint.Check(""));
        Assert.Empty(ScriptLint.Check(null!));
    }

    // Бэклог п.185: запятая/«+» ВНУТРИ строкового литерала (текст, `-join ', '`) — не
    // синтаксис списка, а содержимое строки. Ловилось ложно на собственных рецептах репозитория.
    [Fact]
    public void CommaInsideNestedStringLiteral_IsFine()
    {
        var script = """
            $kid = Get-CimInstance Win32_Process | Select-Object -First 1
            'процесс: ' + $(if ($kid) { "жив ($($kid.Name), pid=$($kid.ProcessId))" } else { 'нет' })
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    [Fact]
    public void CommaInsideJoinSeparator_IsFine()
    {
        Assert.Empty(ScriptLint.Check("else { 'снято: ' + ($killed -join ', ') }"));
    }

    // Бэклог п.195 (СЗ 161190): строки внутри тела функции попадают в её возврат вместе с
    // $true/$false — непустой массив в булевом контексте всегда истинен.
    [Fact]
    public void BareStringInFunctionUsedAsCondition_IsWarnedAbout()
    {
        var script = """
            function Wait-Idle($sec) { Start-Sleep -Seconds $sec; "      $(Get-Date) $s"; if ($s -match '^P[2-9]') { return $true }; return $false }
            if (Wait-Idle 20) { "ОТПУСТИЛ" }
            """;

        var warnings = ScriptLint.Check(script);

        Assert.Contains(warnings, w => w.Contains("голые") && w.Contains("п.195"));
    }

    [Fact]
    public void FunctionWithWriteHostOnly_IsFine()
    {
        var script = """
            function Wait-Idle($sec) {
                Start-Sleep -Seconds $sec
                Write-Host "      $(Get-Date) $s"
                if ($s -match '^P[2-9]') { return $true }
                return $false
            }
            if (Wait-Idle 20) { "ОТПУСТИЛ" }
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    [Fact]
    public void FunctionWithBareStringButUnused_IsFine()
    {
        // Функция с "голой" строкой в теле, но её результат нигде не проверяется —
        // не диагностически опасно, шуметь незачем.
        var script = """
            function Log-Step($msg) { "лог: $msg" }
            Log-Step 'старт'
            """;

        Assert.Empty(ScriptLint.Check(script));
    }

    // Бэклог п.185, критерий готовности: ни один рецепт из tools/recipes/client не должен
    // сыпать ложное предупреждение про конкатенацию в списке.
    [Fact]
    public void AllRepoClientRecipes_NoFalseConcatWarning()
    {
        var dir = Path.Combine(RepoRoot(), "tools", "recipes", "client");
        var offenders = new List<string>();

        foreach (var path in Directory.GetFiles(dir, "*.ps1"))
        {
            var warnings = ScriptLint.Check(File.ReadAllText(path));
            if (warnings.Any(w => w.Contains("запятая связывает сильнее")))
                offenders.Add(Path.GetFileName(path));
        }

        Assert.True(offenders.Count == 0,
            "ложное предупреждение про запятую+конкатенацию на: " + string.Join(", ", offenders));
    }

    // Бэклог п.183 (СЗ 161346): «файл занят?» ничего не говорило, КЕМ — уборка шла в два
    // захода наугад. wipe-tools обязан хотя бы попытаться назвать держателя.
    [Fact]
    public void WipeTools_ReportsWhoHoldsLockedFolder()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "tools", "recipes", "client", "wipe-tools.ps1"));

        Assert.Contains("Show-Holders", text);
        Assert.Contains("держит:", text);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }

    // #190/бэклог п.231: $PSScriptRoot на агенте указывает на %TEMP%, а не на папку рецепта
    // в репозитории — молчаливая подмена цели на 161538 (CPU-Z снепшот запустил случайный exe).
    [Fact]
    public void PSScriptRoot_IsWarnedAbout()
    {
        var script = "$toolDir = Join-Path $PSScriptRoot '..\\cpuz'\nGet-ChildItem $toolDir";

        var warnings = ScriptLint.Check(script);

        Assert.Contains(warnings, w => w.Contains("PSScriptRoot"));
    }

    [Fact]
    public void PSScriptRoot_CaseInsensitive_IsWarnedAbout()
        => Assert.Contains(ScriptLint.Check("$x = $psscriptroot"), w => w.Contains("PSScriptRoot"));

    [Fact]
    public void NoScriptRootUsage_NoWarning()
        => Assert.Empty(ScriptLint.Check("Write-Output 'hello'"));
}
