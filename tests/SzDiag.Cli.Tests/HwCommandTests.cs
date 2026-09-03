using SzDiag.Cli;
using SzDiag.Hardware;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`hw resolve` на промахе device раньше только советовал `hw update` в тексте —
/// на Blackwell (`DEV_2F04`, устаревший pci.ids) это стоило лишнего ручного раунда на живой
/// СЗ (бэклог п.49). Промах должен пробовать обновление автоматически, один раз.</summary>
public class HwCommandTests
{
    private static GpuResolution Resolution(GpuSource source, string? model = null) => new(
        "10de", "NVIDIA Corporation", "2f04", null, null, model,
        null, null, null, null, source, null);

    [Fact]
    public void ShouldAutoUpdate_DeviceMiss_ReturnsTrue()
    {
        Assert.True(HwCommand.ShouldAutoUpdate(Resolution(GpuSource.Unresolved)));
    }

    [Fact]
    public void ShouldAutoUpdate_CacheHit_ReturnsFalse()
    {
        Assert.False(HwCommand.ShouldAutoUpdate(Resolution(GpuSource.Cache, "GeForce RTX 5060 Ti")));
    }

    [Fact]
    public void ShouldAutoUpdate_ScraperHit_ReturnsFalse()
    {
        // Скрапер уже дозаполнил модель - обновлять локальную базу заново незачем.
        Assert.False(HwCommand.ShouldAutoUpdate(Resolution(GpuSource.Scraper, "H100")));
    }
}
