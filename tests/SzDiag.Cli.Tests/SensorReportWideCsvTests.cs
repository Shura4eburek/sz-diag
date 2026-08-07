using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Разбор широкого лога `lhmmon` (п.91), GPU-нагрузка (п.80), датчик-константа (п.71)
/// и выбросы в вольтажах (п.98) — четыре случая, каждый из которых раньше давал уверенно
/// неверное утверждение о железе.</summary>
public class SensorReportWideCsvTests
{
    private const string LhmHeader =
        "timestamp,\"AMD Ryzen 5 5500|Load|CPU Total|/amdcpu/0/load/0\","
        + "\"AMD Ryzen 5 5500|Temperature|Core (Tctl/Tdie)|/amdcpu/0/temperature/0\","
        + "\"AMD Ryzen 5 5500|Power|Package|/amdcpu/0/power/0\","
        + "\"RX 9070 XT|Load|GPU Core|/gpu/0/load/0\","
        + "\"RX 9070 XT|Temperature|GPU Core|/gpu/0/temperature/0\","
        + "\"RX 9070 XT|Power|GPU Package|/gpu/0/power/0\","
        + "\"Nuvoton NCT6687D|Voltage|+12V|/lpc/nct6687d/voltage/3\","
        + "\"Nuvoton NCT6687D|Voltage|+5V|/lpc/nct6687d/voltage/4\"";

    private static string Lhm(params string[] rows) => LhmHeader + "\n" + string.Join("\n", rows);

    [Fact]
    public void WideCsv_IsRecognisedAndParsed()
    {
        var csv = Lhm(
            "2026-08-04 17:00:00,98.5,76.6,88,99.1,71.0,165,12.096,5.040",
            "2026-08-04 17:05:00,99.0,76.0,90,98.0,72.0,160,11.856,5.020");

        var parsed = SensorReport.ParseAny(csv);

        Assert.Equal(SensorCsvFormat.LibreHardwareMonitor, parsed.Format);
        Assert.Equal(2, parsed.Samples.Count);
        Assert.Equal(98.5, parsed.Samples[0].CpuPercent);
        Assert.Equal(99.1, parsed.Samples[0].GpuPercent);
        Assert.Equal(165, parsed.Samples[0].GpuPowerW);
        Assert.Equal(12.096, parsed.Samples[0].Volt12);
    }

    [Fact]
    public void UnknownFormat_SaysSo_InsteadOfClaimingNothingHappened()
    {
        // Регрессия (п.91): валидный лог на 5.7 МБ давал «прогон не подтверждён ничем» —
        // утверждение о железе, сделанное из-за непонятого файла.
        var parsed = SensorReport.ParseAny("что-то совсем не то\nи ещё строка");

        Assert.Equal(SensorCsvFormat.Unknown, parsed.Format);
        var text = SensorReport.Format(SensorReport.Summarize(parsed.Samples, format: parsed.Format));
        Assert.Contains("Формат CSV не распознан", text);
        Assert.DoesNotContain("не подтверждён ничем", text);
    }

    [Fact]
    public void GpuOnlyRun_IsReportedAsLoad_NotAsTwoPercentOfTime()
    {
        // Регрессия (п.80): после 30 минут FurMark отчёт говорил «нагрузка шла 2% времени»,
        // потому что смотрел только на CPU.
        var csv = Lhm(
            "2026-08-04 17:00:00,5,45,20,99,70,300,12.0,5.0",
            "2026-08-04 17:10:00,4,44,19,98,72,305,12.0,5.0",
            "2026-08-04 17:20:00,6,45,21,99,73,302,12.0,5.0");

        var parsed = SensorReport.ParseAny(csv);
        var s = SensorReport.Summarize(parsed.Samples, format: parsed.Format);
        var text = SensorReport.Format(s);

        Assert.Equal(20.0, s.GpuLoadedMinutes);
        Assert.Equal(0.0, s.LoadedMinutes);
        Assert.Contains("Под нагрузкой (GPU", text);
        Assert.Contains("Нагрузка подтверждена приборно", text);
    }

    [Fact]
    public void VoltageSpikes_AreRejectedAndCounted()
    {
        // Регрессия (п.98): четыре замера из 5659 дали «+12V max 49,14 В» — артефакт чтения
        // чипа, который читается как выброс питания.
        var rows = new List<string>();
        for (var i = 0; i < 20; i++)
            rows.Add($"2026-08-04 17:{i:00}:00,90,70,80,10,50,60,12.0{i % 5},5.02");
        rows.Add("2026-08-04 17:30:00,90,70,80,10,50,60,49.14,20.46");

        var s = SensorReport.Summarize(SensorReport.ParseAny(Lhm(rows.ToArray())).Samples,
            format: SensorCsvFormat.LibreHardwareMonitor);
        var text = SensorReport.Format(s);

        var rail12 = s.Rails!.Single(r => r.Name == "+12V");
        Assert.Equal(1, rail12.Rejected);
        Assert.True(rail12.Max < 13, $"выброс уехал в максимум: {rail12.Max}");
        Assert.Contains("вне физического диапазона отброшено", text);
        Assert.DoesNotMatch(@"49[.,]1", text);
    }

    [Fact]
    public void ConstantTemperature_IsCalledBroken_NotReportedAsMaximum()
    {
        // Регрессия (п.71): «Температура max 27,9 °C» после десяти минут под 100 % CPU —
        // ACPI-зона отдаёт константу-заглушку, а по такому отчёту перегрев «исключается».
        var rows = Enumerable.Range(0, 15)
            .Select(i => $"2026-08-04 17:{i:00}:00;100;1;27.9;55")
            .ToArray();
        var csv = "time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct\n" + string.Join("\n", rows);

        var s = SensorReport.Summarize(SensorReport.Parse(csv));
        var text = SensorReport.Format(s);

        Assert.Contains("датчик не отвечает", text);
        Assert.Matches(@"27[.,]9", text);       // значение показываем, но как константу
        Assert.DoesNotContain("Температура max", text);
        Assert.Single(s.ConstantSensors!);
    }

    [Fact]
    public void RealTemperatureRange_IsStillPrintedAsMaximum()
    {
        var rows = Enumerable.Range(0, 15)
            .Select(i => $"2026-08-04 17:{i:00}:00;100;1;{60 + i}.0;55")
            .ToArray();
        var csv = "time;cpu_pct;stress_procs;cpu_temp_c;ram_used_pct\n" + string.Join("\n", rows);

        var text = SensorReport.Format(SensorReport.Summarize(SensorReport.Parse(csv)));

        Assert.Contains("Температура max", text);
        Assert.DoesNotContain("датчик не отвечает", text);
    }
}
