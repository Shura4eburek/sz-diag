using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>На машине с Ryzen 7700 (APU) + дискретной RTX 5070 Ti отчёт брал первую колонку
/// GPU по порядку — простаивающую встроенную графику (0%) — и молчал про реально нагруженную
/// дискретную карту (97%), выдавая противоречивое «GPU 0%» рядом с живыми температурой/
/// мощностью. На машине с APU-процессором (то есть почти на любой современной сборке AMD)
/// отчёт врал всегда (бэклог п.217, СЗ 161346, 26.08.2026).</summary>
public class SensorReportMultiGpuTests
{
    private const string HeaderWithApuAndDiscreteGpu =
        "timestamp,\"AMD Ryzen 7 7700|Load|CPU Total|/amdcpu/0/load/0\","
        + "\"AMD Ryzen 7 7700|Temperature|CPU Package|/amdcpu/0/temperature/0\","
        + "\"AMD Ryzen 7 7700|Power|Package|/amdcpu/0/power/0\","
        + "\"AMD Radeon(TM) Graphics|Load|GPU Core|/amdgpu/0/load/0\","
        + "\"AMD Radeon(TM) Graphics|Temperature|GPU Core|/amdgpu/0/temperature/0\","
        + "\"AMD Radeon(TM) Graphics|Power|GPU Package|/amdgpu/0/power/0\","
        + "\"NVIDIA GeForce RTX 5070 Ti|Load|GPU Core|/gpu/0/load/0\","
        + "\"NVIDIA GeForce RTX 5070 Ti|Temperature|GPU Core|/gpu/0/temperature/0\","
        + "\"NVIDIA GeForce RTX 5070 Ti|Power|GPU Package|/gpu/0/power/0\"";

    private static string Csv(params string[] rows) => HeaderWithApuAndDiscreteGpu + "\n" + string.Join("\n", rows);

    [Fact]
    public void MultiGpu_PicksDiscreteCard_NotIdleIntegratedGraphics()
    {
        // Встроенная (колонки 4-6) простаивает; дискретная (7-9) под нагрузкой — ровно как на 161346.
        var csv = Csv(
            "2026-08-26 12:00:00,90,70,120,0,40,5,95,76,300",
            "2026-08-26 12:05:00,88,71,118,0,41,5,97,76,300");

        var parsed = SensorReport.ParseAny(csv);
        var s = SensorReport.Summarize(parsed.Samples, format: parsed.Format);
        var text = SensorReport.Format(s);

        Assert.Equal(97, parsed.Samples[1].GpuPercent);
        Assert.Equal(300, parsed.Samples[0].GpuPowerW);
        Assert.Equal(76, parsed.Samples[0].GpuTempC);
        Assert.Contains("Под нагрузкой (GPU", text);
        Assert.DoesNotContain("GPU max 0%", text);
    }

    [Fact]
    public void SingleIntegratedGpuOnly_StillWorks_NoDiscreteToPreferOver()
    {
        // Сборка вообще без дискретной карты — не должна ломаться из-за новой логики выбора.
        const string header =
            "timestamp,\"AMD Ryzen 5 5600G|Load|CPU Total|/amdcpu/0/load/0\","
            + "\"AMD Radeon(TM) Graphics|Load|GPU Core|/amdgpu/0/load/0\","
            + "\"AMD Radeon(TM) Graphics|Temperature|GPU Core|/amdgpu/0/temperature/0\"";
        var csv = header + "\n2026-08-26 12:00:00,80,55,68";

        var parsed = SensorReport.ParseAny(csv);

        Assert.Equal(55, parsed.Samples[0].GpuPercent);
        Assert.Equal(68, parsed.Samples[0].GpuTempC);
    }
}
