using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

/// <summary>Синтаксическая и содержательная проверка отдельных рецептов
/// <c>tools/recipes/client/*.ps1</c>, которые нельзя тестировать через сборку C# (это просто
/// файлы, которые кладутся на клиента через <c>szcli exec -f</c>). Общий помощник ищет корень
/// репозитория так же, как <see cref="BuildDistScriptTests"/>.</summary>
public class RecipeContentTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }

    private static string Recipe(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "recipes", "client", name));

    /// <summary>Один PowerShell токенизирует все переданные файлы разом — синтаксическая ошибка
    /// в правке падает на сборке, а не на живой заявке (см. DiagnosticTests.AllProbeBodies_ParseAsValidPowerShell).</summary>
    private static void AssertAllParse(params string[] names)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szrecipes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var n in names)
                File.WriteAllText(Path.Combine(dir, n), Recipe(n), new System.Text.UTF8Encoding(true));

            var check = $$"""
                $bad = @()
                foreach ($f in Get-ChildItem '{{dir}}' -Filter *.ps1) {
                    $errors = $null
                    [void][System.Management.Automation.PSParser]::Tokenize(
                        (Get-Content $f.FullName -Raw), [ref]$errors)
                    if ($errors.Count -gt 0) {
                        $bad += "$($f.Name): $($errors[0].Message) (строка $($errors[0].Token.StartLine))"
                    }
                }
                if ($bad.Count -gt 0) { $bad; exit 1 } else { 'all-ok' }
                """;
            var r = new PowerShellRunner().Run(check, throwOnError: false, timeout: TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
                $"рецепты с ошибками разбора:\n{r.StdOut}\n{r.StdErr}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void StartGameCs2_UnifiesProcessAndMapCheckIntoSingleVerdict()
    {
        // Регрессия (#133 / б.188, 160705): «cs2.exe: НЕ ПОДНЯЛСЯ» и «ботов в матче: 9»
        // печатались двумя независимыми строками — расхождение читалось по отдельности,
        // а не как одна ошибка. Теперь — единственный вердикт по прогону.
        var text = Recipe("start-game-cs2.ps1");

        Assert.Contains("ПРОГОН ЗАСЧИТАН", text);
        Assert.Contains("НЕ ПОДНЯЛСЯ", text);
        Assert.Contains("НЕ СОСТОЯЛСЯ", text);
        Assert.Contains("КАРТА НЕ ЗАГРУЗИЛАСЬ", text);
        // Старого разнобоя из двух независимых Write-Output про процесс и про ботов быть не должно.
        Assert.DoesNotContain("'cs2.exe: ' + $(if ($g)", text);
    }

    [Fact]
    public void StartGameCs2_ParsesAsValidPowerShell() => AssertAllParse("start-game-cs2.ps1");

    [Fact]
    public void PeOfflineTriage_HasWerLiveKernelSection()
    {
        // #136 / б.191 (161556): pe-offline-triage.ps1 (один заход по машине з PE) не дивився
        // Report.wer взагалі - справжня причина 35 x KP41 без BSOD/WHEA лежала саме там.
        var text = Recipe("pe-offline-triage.ps1");

        Assert.Contains("WER: LiveKernelEvent", text);
        Assert.Contains("ReportArchive", text);
        Assert.Contains("Kernel_|Critical_", text);
        Assert.Contains("VIDEO_ENGINE_TIMEOUT_DETECTED", text);
        Assert.Contains("вимкнон", text);   // звірка з Kernel-Power 41
    }

    [Fact]
    public void PeOfflineTriage_ParsesAsValidPowerShell() => AssertAllParse("pe-offline-triage.ps1");

    [Fact]
    public void DiskStressWrite_ChecksSmartBeforeAndAfter()
    {
        // #72 / б.135 и #84 / б.141 (СЗ 161346): бюджет записи уже был в рецепте, но приёмка
        // шла по логу ("расхождений 0"), хотя обратное чтение шло из page cache и SMART
        // DataUnitsRead не рос вовсе. Проверяем PercentageUsed до старта и прирост
        // DataUnitsRead/Written после - именно этого раньше не было.
        var text = Recipe("disk-stress-write.ps1");

        Assert.Contains("PercentageUsed", text);
        Assert.Contains("DataUnitsRead", text);
        Assert.Contains("DataUnitsWritten", text);
        Assert.Contains("IOCTL_STORAGE_QUERY_PROPERTY", text);
        Assert.Contains("ПРОГОН НЕВАЛИДЕН", text);
        Assert.Contains("ПРИЁМКА: OK", text);
        Assert.Contains("WriteCapGB", text);
    }

    [Fact]
    public void DiskStressWrite_ParsesAsValidPowerShell() => AssertAllParse("disk-stress-write.ps1");

    [Fact]
    public void ProcessIoTop_MeasuresIoWithoutPerfCounters()
    {
        // #151 / б.201 (СЗ 161972): Get-Counter и Win32_PerfRawData_* дают "Invalid class"
        // (HRESULT 0x80041010) на клиенте со сломанными perf-счётчиками, а рецепт при этом
        // молча отчитывался успехом с пустой таблицей. GetProcessIoCounters — прямой Win32 API,
        // не завязан на perflib/lodctr вообще.
        var text = Recipe("process-io-top.ps1");

        Assert.Contains("GetProcessIoCounters", text);
        Assert.Contains("НЕДОСТУПЕН", text);           // явная строка отказа, не пустая таблица
        Assert.Contains("Find-ProcessByTaskName", text);
        Assert.Contains("Actions.Execute", text);   // PID по образу задачи, а не по времени старта
    }

    [Fact]
    public void ProcessIoTop_ParsesAsValidPowerShell() => AssertAllParse("process-io-top.ps1");

    /// <summary>DiskZoneMap живёт в SzDiag.Contracts (генерируется CLI, а не читается с диска
    /// как рецепт), но синтаксис сгенерированного PowerShell проверяем тем же способом.</summary>
    [Fact]
    public void DiskZoneMap_GeneratedScripts_ParseAsValidPowerShell()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szdiskmap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "map.ps1"),
                SzDiag.Contracts.DiskZoneMap.BuildScript(0, "map", 300, 16, 0, 0, 256, 20, 150),
                new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "zone.ps1"),
                SzDiag.Contracts.DiskZoneMap.BuildScript(0, "zone", 0, 0, 440, 520, 256, 25, 200),
                new System.Text.UTF8Encoding(true));

            var check = $$"""
                $bad = @()
                foreach ($f in Get-ChildItem '{{dir}}' -Filter *.ps1) {
                    $errors = $null
                    [void][System.Management.Automation.PSParser]::Tokenize(
                        (Get-Content $f.FullName -Raw), [ref]$errors)
                    if ($errors.Count -gt 0) {
                        $bad += "$($f.Name): $($errors[0].Message) (строка $($errors[0].Token.StartLine))"
                    }
                }
                if ($bad.Count -gt 0) { $bad; exit 1 } else { 'all-ok' }
                """;
            var r = new PowerShellRunner().Run(check, throwOnError: false, timeout: TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
                $"сгенерированные скрипты DiskZoneMap с ошибками разбора:\n{r.StdOut}\n{r.StdErr}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>SleepCycleScript тоже живёт в SzDiag.Contracts и генерируется CLI — тот же
    /// PSParser-страж, что и для DiskZoneMap: инсталлятор пишет payload как одинарную строку
    /// (Replace на кавычках), синтаксическую ошибку в этой сборке легко не заметить глазами.</summary>
    [Fact]
    public void SleepCycleScript_GeneratedScripts_ParseAsValidPowerShell()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szsleepcycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "install.ps1"),
                SzDiag.Contracts.SleepCycleScript.BuildInstall("160306"),
                new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "install-confirmed.ps1"),
                SzDiag.Contracts.SleepCycleScript.BuildInstall("160306", confirmRisk: true),
                new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "stop.ps1"),
                SzDiag.Contracts.SleepCycleScript.StopScript,
                new System.Text.UTF8Encoding(true));

            var check = $$"""
                $bad = @()
                foreach ($f in Get-ChildItem '{{dir}}' -Filter *.ps1) {
                    $errors = $null
                    [void][System.Management.Automation.PSParser]::Tokenize(
                        (Get-Content $f.FullName -Raw), [ref]$errors)
                    if ($errors.Count -gt 0) {
                        $bad += "$($f.Name): $($errors[0].Message) (строка $($errors[0].Token.StartLine))"
                    }
                }
                if ($bad.Count -gt 0) { $bad; exit 1 } else { 'all-ok' }
                """;
            var r = new PowerShellRunner().Run(check, throwOnError: false, timeout: TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
                $"сгенерированные скрипты SleepCycleScript с ошибками разбора:\n{r.StdOut}\n{r.StdErr}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    /// <summary>TransientStressScript (#93, бэклог п.154) тоже живёт в SzDiag.Contracts и
    /// генерируется CLI — тот же PSParser-страж.</summary>
    [Fact]
    public void TransientStressScript_GeneratedScripts_ParseAsValidPowerShell()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sztransient-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "default.ps1"),
                SzDiag.Contracts.TransientStressScript.BuildScript("160306"),
                new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(dir, "no-gpu.ps1"),
                SzDiag.Contracts.TransientStressScript.BuildScript("160306", onSeconds: 30, offSeconds: 20, totalHours: 2, memGb: 4, withGpu: false),
                new System.Text.UTF8Encoding(true));

            var check = $$"""
                $bad = @()
                foreach ($f in Get-ChildItem '{{dir}}' -Filter *.ps1) {
                    $errors = $null
                    [void][System.Management.Automation.PSParser]::Tokenize(
                        (Get-Content $f.FullName -Raw), [ref]$errors)
                    if ($errors.Count -gt 0) {
                        $bad += "$($f.Name): $($errors[0].Message) (строка $($errors[0].Token.StartLine))"
                    }
                }
                if ($bad.Count -gt 0) { $bad; exit 1 } else { 'all-ok' }
                """;
            var r = new PowerShellRunner().Run(check, throwOnError: false, timeout: TimeSpan.FromSeconds(60));
            Assert.True(r.ExitCode == 0 && r.StdOut.Contains("all-ok"),
                $"сгенерированные скрипты TransientStressScript с ошибками разбора:\n{r.StdOut}\n{r.StdErr}");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
