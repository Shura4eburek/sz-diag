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
}
