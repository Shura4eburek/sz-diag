using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Главный вопрос прогона — «сколько времени нагрузка реально держалась».
/// На 160306 «40 минут выстояла» на деле означало 4.2 минуты нагрузки и 23 минуты простоя
/// (бэклог п.7).</summary>
public class SensorReportTests
{
    private static string Csv(params string[] rows)
        => "time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct\n" + string.Join("\n", rows);

    [Fact]
    public void Summarize_CountsOnlyTimeUnderLoad()
    {
        // 2 минуты под нагрузкой из 10 минут ряда — ровно кейс 160306 в миниатюре.
        var csv = Csv(
            "2026-08-04 17:00:00;100;1;70;55",
            "2026-08-04 17:01:00;100;1;72;56",
            "2026-08-04 17:02:00;100;1;71;56",
            "2026-08-04 17:04:00;3;0;40;30",
            "2026-08-04 17:10:00;2;0;39;30");

        var s = SensorReport.Summarize(SensorReport.Parse(csv));

        Assert.Equal(5, s.Samples);
        Assert.Equal(2.0, s.LoadedMinutes);
        Assert.Equal(10.0, s.SpanMinutes);
        Assert.Equal(100, s.MaxCpu);
        Assert.Equal(72, s.MaxTempC);
        Assert.Equal(1, s.MaxStressProcesses);
        Assert.InRange(s.LoadedShare, 0.19, 0.21);
    }

    [Fact]
    public void Format_LoadMostlyAbsent_WarnsAgainstWrongConclusion()
    {
        var csv = Csv(
            "2026-08-04 17:00:00;100;1;70;55",
            "2026-08-04 17:01:00;5;0;40;30",
            "2026-08-04 17:30:00;4;0;39;30");

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("Нагрузка шла лишь", text);
        Assert.Contains("160306", text);
    }

    [Fact]
    public void Format_NoLoadedSamples_SaysSoInsteadOfEmptyPlaceholder()
    {
        // Регрессия (бэклог п.116): при нуле замеров с CPU ≥ 60 % печаталось
        // «средний под нагрузкой %;» — пустое место читается как баг разбора.
        var csv = Csv(
            "2026-08-04 17:00:00;30;0;40;30",
            "2026-08-04 17:05:00;25;0;40;30");

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.DoesNotContain("средний под нагрузкой %", text);
        Assert.Contains("средний под нагрузкой — не было", text);
    }

    [Fact]
    public void Format_NoStressProcessEverSeen_SaysRunProbablyNeverStarted()
    {
        // Тихая смерть OCCT при перенаправленном stdout выглядит именно так (п.40).
        var csv = Csv(
            "2026-08-04 17:00:00;4;0;40;30",
            "2026-08-04 17:05:00;3;0;40;30");

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("не стартовал", text);
    }

    [Fact]
    public void Format_RealLoad_ConfirmsInstrumentally()
    {
        var csv = Csv(
            "2026-08-04 17:00:00;98;2;70;60",
            "2026-08-04 17:30:00;99;2;75;61",
            "2026-08-04 18:00:00;97;2;76;61");

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("Нагрузка подтверждена приборно", text);
    }

    [Fact]
    public void Summarize_DetectsLongGap_WhenWatcherWasStarved()
    {
        // Под 100% нагрузкой наблюдатель тормозит: за 9 минут вместо 54 строк было 11 (п.64).
        var csv = Csv(
            "2026-08-04 17:00:00;100;1;70;55",
            "2026-08-04 17:00:10;100;1;70;55",
            "2026-08-04 17:03:00;100;1;71;56");

        var s = SensorReport.Summarize(SensorReport.Parse(csv));

        Assert.Equal(170, s.GapSeconds);
        Assert.Contains("разрыв в ряду", SensorReport.Format(s));
    }

    [Fact]
    public void Parse_EmptyCells_AreNullNotZero()
    {
        // Пустая температура — «датчика нет», а не «холодный CPU» (ловушка из п.38).
        var sample = Assert.Single(SensorReport.Parse(Csv("2026-08-04 17:00:00;50;1;;60")));

        Assert.Null(sample.CpuTempC);
        Assert.Equal(50, sample.CpuPercent);
    }

    [Fact]
    public void Parse_CommaDecimal_HandledAsInvariant()
    {
        // Локаль клиента печатает запятую — на этом уже ломался разбор (п.2).
        var sample = Assert.Single(SensorReport.Parse(Csv("2026-08-04 17:00:00;99,5;1;70,3;60")));

        Assert.Equal(99.5, sample.CpuPercent);
        Assert.Equal(70.3, sample.CpuTempC);
    }

    [Fact]
    public void Format_EmptyCsv_SaysRunIsNotConfirmed()
        => Assert.Contains("не подтверждён", SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(""))));

    [Fact]
    public void Format_CpuTempColumnAlwaysEmpty_WarnsInsteadOfSilentlyOmitting()
    {
        // Регрессия (бэклог п.153, СЗ 160587): на машине без ACPI-датчика колонка cpu_temp_c
        // пуста во ВСЕХ строках, а CSV при этом честно пишется (GPU-колонки живые) — выглядит
        // как рабочий прогон, хотя вопрос "перегрев или нет" не закрыт вовсе.
        var csv = Csv(
            "2026-08-13 17:46:04;100;1;;35",
            "2026-08-13 17:46:14;100;1;;35",
            "2026-08-13 17:46:24;98;1;;36");

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("Температура CPU недоступна", text);
        Assert.Contains("start-sensors.ps1", text);
        Assert.DoesNotContain("Температура max", text);
    }

    [Fact]
    public void Format_CpuClockPresent_PrintsMaxClock()
    {
        // Бэклог п.153: без частоты вердикт "троттлинга нет" недоказуем.
        var csv = "time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct;gpu_pct;gpu_temp_c;gpu_power_w;cpu_clock_mhz\n" +
                  "2026-08-13 17:46:04;100;1;70;35;;;;4550\n" +
                  "2026-08-13 17:46:14;100;1;71;35;;;;4650\n";

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("Частота CPU max 4650 МГц", text);
    }

    [Fact]
    public void ConstantTemperature_VaryingLoad_StillCalledBroken()
    {
        // Регрессия (#95 / б.156, СЗ 161190): cpu_temp_c=27,9 не шевельнулась НИ РАЗУ, пока
        // cpu_pct прыгал от простоя до 100% под prime95 — источник (ACPI thermal zone) отдаёт
        // температуру корпуса, а не ядер, и такое "ровно держит" само по себе диагноз.
        var rows = new[]
        {
            "2026-08-13 10:00:00;2;0;27.9;30",
            "2026-08-13 10:00:10;100;1;27.9;35",
            "2026-08-13 10:00:20;100;1;27.9;35",
            "2026-08-13 10:00:30;100;1;27.9;35",
            "2026-08-13 10:00:40;100;1;27.9;35",
            "2026-08-13 10:00:50;100;1;27.9;35",
            "2026-08-13 10:01:00;100;1;27.9;35",
            "2026-08-13 10:01:10;100;1;27.9;35",
            "2026-08-13 10:01:20;100;1;27.9;35",
            "2026-08-13 10:01:30;100;1;27.9;35",
            "2026-08-13 10:01:40;5;0;27.9;30",
        };
        var csv = "time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct\n" + string.Join("\n", rows);

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("датчик не отвечает", text);
        Assert.Matches(@"27[.,]9", text);
    }
}

public class SensorWatcherScriptTests
{
    [Fact]
    public void Script_WritesLineByLineToSurvivePowerLoss()
    {
        var script = SensorWatcher.BuildScript(@"C:\ProgramData\szdiag\s.csv", 10, 60, new[] { "OCCT" });

        Assert.Contains("Add-Content", script);      // открыл-записал-закрыл на каждой строке
        Assert.DoesNotContain("Out-File -Append -Encoding utf8 -NoNewline", script);
        Assert.Contains("Start-Sleep -Seconds 10", script);
        Assert.Contains("AddMinutes(60)", script);
    }

    [Fact]
    public void Script_UsesCheapCountersOnly()
    {
        // Get-Counter по GPU под нагрузкой сам становился узким местом (п.64), а ring0-логгеры
        // конфликтуют с OCCT за драйвер (п.19/п.23).
        var script = SensorWatcher.BuildScript(@"C:\x.csv", 10, 0, new[] { "OCCT" });

        Assert.DoesNotContain("Get-Counter", script);
        Assert.DoesNotContain("lhmmon", script);
        Assert.Contains("Win32_Processor", script);
    }

    [Fact]
    public void Script_CountsStressProcesses()
    {
        var script = SensorWatcher.BuildScript(@"C:\x.csv", 5, 0, new[] { "OCCT", "TM5" });

        Assert.Contains("'OCCT','TM5'", script);
        Assert.Contains("Get-Process -Name $n", script);
    }

    [Fact]
    public void Script_ZeroMinutes_RunsUntilKilled()
        => Assert.Contains("AddYears(1)", SensorWatcher.BuildScript(@"C:\x.csv", 5, 0, new[] { "OCCT" }));

    [Fact]
    public void Script_WritesCpuClockColumn_FromSameProcessorQuery()
    {
        // Бэклог п.153: без частоты вердикт "троттлинга нет" недоказуем. Частоту берём из
        // того же запроса Win32_Processor, что и загрузку - второй дорогой WMI-вызов под
        // нагрузкой не нужен.
        var script = SensorWatcher.BuildScript(@"C:\x.csv", 5, 0, new[] { "OCCT" });

        Assert.Contains("cpu_clock_mhz", script);
        Assert.Contains("CurrentClockSpeed", script);
        Assert.Contains("$clock", script);
    }

    [Fact]
    public void Script_ScrubsNvidiaSmiNotAvailableValues()
    {
        // RTX 3050 отдаёт power.draw как "[N/A]" — писать эту строку в CSV как число нельзя,
        // szcli sensors report падает на приведении к double (бэклог п.166).
        var script = SensorWatcher.BuildScript(@"C:\x.csv", 5, 0, new[] { "OCCT" });

        Assert.Contains("ScrubNum", script);
        Assert.DoesNotContain("$gpuPower = $p[2].Trim()", script);
    }

    // #156/бэклог п.206: дыра 18 минут в CSV на 161716, при этом `status` рапортовал «идёт».
    [Fact]
    public void Script_EmitsHeartbeatSoStatusCanDetectStaleness()
    {
        var script = SensorWatcher.BuildScript(@"C:\x.csv", 10, 0, new[] { "OCCT" });

        Assert.Contains("Write-Output (\"tick;{0};{1}\"", script);
    }

    [Fact]
    public void Script_RaisesOwnPriorityAndRetriesCounters()
    {
        // Под 100% нагрузкой Get-CimInstance сам голодал и cpu_pct/ram_used_pct уходили
        // пустыми (бэклог п.206).
        var script = SensorWatcher.BuildScript(@"C:\x.csv", 10, 0, new[] { "OCCT" });

        Assert.Contains("PriorityClass", script);
        Assert.Contains("'n/a'", script);
    }
}
