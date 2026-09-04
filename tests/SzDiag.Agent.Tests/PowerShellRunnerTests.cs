using System.Diagnostics;
using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class PowerShellRunnerTests
{
    [Fact]
    public void Run_ExceedsTimeout_KillsProcessAndThrowsQuickly()
    {
        var runner = new PowerShellRunner();
        var sw = Stopwatch.StartNew();

        Assert.Throws<PowerShellTimeoutException>(() =>
            runner.Run("Start-Sleep -Seconds 5", timeout: TimeSpan.FromMilliseconds(500)));

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3),
            $"должен убить процесс быстро, а не ждать все 5с (прошло {sw.Elapsed})");
    }

    [Fact]
    public void Run_WithinTimeout_ReturnsNormally()
    {
        var runner = new PowerShellRunner();

        var result = runner.Run("Write-Output 'ok'", timeout: TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("ok", result.StdOut);
    }

    [Fact]
    public void Run_CyrillicOutput_SurvivesRoundTrip()
    {
        // Регрессия (бэклог п.17/29/62): дочерний powershell писал stdout в кодовой странице
        // консоли (cp866 у headless-агента), и кириллица в diag.md/exec приезжала мусором
        // («�������� Windows 11 Pro»). Проверяем сквозной путь: скрипт → stdout → строка.
        var runner = new PowerShellRunner(utf8: true);

        var r = runner.Run("Write-Output 'Перевірка кирилиці: ёжик'", timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("Перевірка кирилиці: ёжик", r.StdOut);
    }

    [Fact]
    public void Run_CyrillicInScriptBody_ParsesAndCompares()
    {
        // Скрипт уходит через -EncodedCommand (UTF-16LE), поэтому кириллица в теле
        // (литералы, сравнения) не должна ломать парсер PowerShell.
        var runner = new PowerShellRunner(utf8: true);

        var r = runner.Run("$s = 'ошибка'; if ($s -eq 'ошибка') { 'СОВПАЛО' }",
            timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("СОВПАЛО", r.StdOut);
    }

    [Fact]
    public void Run_WithoutUtf8_StillWorksForAscii()
    {
        // PE-режим: шапку с [Console]::OutputEncoding не ставим (там она вешает процесс —
        // СЗ 159948), но обычный ASCII-вывод обязан работать как раньше.
        var runner = new PowerShellRunner(utf8: false);

        var r = runner.Run("Write-Output 'pe-ok'", timeout: TimeSpan.FromSeconds(15));

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("pe-ok", r.StdOut);
    }

    [Fact]
    public void Run_HugeScript_FallsBackToFileAndRuns()
    {
        // Регрессия (бэклог п.101/196): -EncodedCommand — это 2,67 символа аргумента на символ
        // скрипта, а лимит командной строки Windows — 32 767. Секция whea (~13 КБ исходника)
        // дважды падала на живых заявках с «имя файла или его расширение имеет слишком большую
        // длину». Длинный скрипт обязан уезжать во временный .ps1 (-File) и работать.
        var runner = new PowerShellRunner(utf8: true);
        var filler = string.Join("\n", Enumerable.Range(1, 600).Select(i =>
            $"# наполнитель {i}: длинная строка комментария, раздувающая скрипт до размеров секции whea"));
        var script = filler + "\nWrite-Output 'огромный-ок'";

        var r = runner.Run(script, timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(0, r.ExitCode);
        Assert.Contains("огромный-ок", r.StdOut);
    }

    [Fact]
    public void Run_HugeScript_TempFileIsCleanedUp()
    {
        // Файл-фоллбэк не должен замусоривать клиентскую машину: после прогона временный
        // .ps1 обязан исчезнуть.
        var runner = new PowerShellRunner(utf8: true);
        var filler = string.Join("\n", Enumerable.Range(1, 600).Select(i =>
            $"# наполнитель {i}: длинная строка комментария, раздувающая скрипт до размеров секции whea"));

        runner.Run(filler + "\n'ok'", timeout: TimeSpan.FromSeconds(30));

        var leftovers = Directory.GetFiles(Path.GetTempPath(), "szdiag-ps-*.ps1");
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Run_ScriptWithParamBlock_UsesDefaultsInsteadOfBreaking()
    {
        // Регрессия (бэклог п.102/168/189): шапка с [Console]::OutputEncoding клеится ПЕРЕД
        // скриптом, а param(...) обязан быть первым выражением — любой рецепт с параметрами
        // падал с CommandNotFoundException и шёл дальше мимо аргументов, искажая результат.
        var runner = new PowerShellRunner(utf8: true);

        var r = runner.Run("param([string]$Name = 'мир')\n\"привет $Name\"",
            timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("привет мир", r.StdOut);
        Assert.DoesNotContain("CommandNotFoundException", r.StdErr);
    }

    [Fact]
    public void Run_ParamBlockAfterComments_StillWorks()
    {
        // param может идти после комментариев — детект не должен требовать первой строки.
        var runner = new PowerShellRunner(utf8: true);
        var script = "# рецепт с граблей\n<# блочный\n   комментарий #>\nparam([int]$N = 7)\n\"N=$N\"";

        var r = runner.Run(script, timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("N=7", r.StdOut);
        // Значение могло «выжить» и случайно (аргумент-выражение вычисляется до провала
        // поиска команды param) — поэтому проверяем и чистоту stderr.
        Assert.DoesNotContain("CommandNotFoundException", r.StdErr);
    }

    [Fact]
    public void Run_SetsWorkingDirectoryExplicitly_InsteadOfInheritingProcessCwd()
    {
        // Регрессия (бэклог п.149, СЗ 161716): ProcessStartInfo.WorkingDirectory не задавался
        // явно и наследовался от CWD агента. На длинном рабочем пути клиента
        // (C:\Users\vasya\OneDrive\Desktop\Client-test) запуск powershell.exe падал с "имя
        // файла или его расширение имеет слишком большую длину" - именно на секции whea,
        // самой объёмной. WorkingDirectory обязан резолвиться явно (каталог самого агента),
        // а не наследоваться от того, откуда его запустили.
        var psi = PowerShellRunner.BuildStartInfo("-NoProfile -Command -", utf8: true);

        Assert.False(string.IsNullOrEmpty(psi.WorkingDirectory));
        Assert.Equal(AppContext.BaseDirectory.TrimEnd('\\'), psi.WorkingDirectory.TrimEnd('\\'));
    }

    [Fact]
    public void BuildStartInfo_Utf8False_DecodesWithActualOemCodepage_NotDefault()
    {
        // Регрессия (бэклог п.228, СЗ 161498): в PE _utf8=false, StandardOutputEncoding
        // оставался null, и кириллица из дочернего процесса (реально написанная в OEM-
        // кодовой странице PE) уезжала кракозябрами в szcli ("=== ?????? szdiag-* ??
        // ??????-????"). Декодировать нужно ТОЙ ЖЕ кодировкой, что использует консоль.
        var psi = PowerShellRunner.BuildStartInfo("-NoProfile -Command -", utf8: false);

        var expected = PeConsoleEncoding.DetectOemEncoding();
        Assert.NotNull(expected);   // на Windows-боксе GetOEMCP всегда что-то отдаёт
        Assert.Equal(expected!.CodePage, psi.StandardOutputEncoding?.CodePage);
        Assert.Equal(expected.CodePage, psi.StandardErrorEncoding?.CodePage);
    }

    [Fact]
    public void PeConsoleEncoding_Cp866_RoundTripsCyrillic()
    {
        // cp866 - основной кириллический OEM-codepage на русскоязычных сборках WinPE
        // (см. заголовок класса PeConsoleEncoding). Кодировка обязана быть зарегистрирована
        // и рабочей независимо от того, какой codepage активен на текущем боксе.
        PeConsoleEncoding.EnsureRegistered();
        var cp866 = System.Text.Encoding.GetEncoding(866);

        var text = "Привет, мир! szdiag-160306";
        var bytes = cp866.GetBytes(text);

        Assert.Equal(text, cp866.GetString(bytes));
    }

    [Fact]
    public void Run_MultilinePipeline_ReturnsAllLines()
    {
        // Регрессия: скрипт раньше шёл через stdin `-Command -`, который в PowerShell 5.1
        // обрывает многострочные конвейеры (строка с хвостовым | или , рвётся) — до вывода
        // доходила лишь первая строка. Все секции RunDiag на живой машине выходили пустыми.
        var runner = new PowerShellRunner();
        var script = string.Join("\n", new[]
        {
            "'FIRST'",
            "1..3 |",
            "  ForEach-Object { \"LINE$_\" } |",
            "  Out-String",
        });

        var r = runner.Run(script, timeout: TimeSpan.FromSeconds(15));

        Assert.Contains("FIRST", r.StdOut);
        Assert.Contains("LINE1", r.StdOut);
        Assert.Contains("LINE2", r.StdOut);
        Assert.Contains("LINE3", r.StdOut);
    }
}
