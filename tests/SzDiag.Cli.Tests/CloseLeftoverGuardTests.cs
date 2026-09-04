using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`szcli close` не должен молча закрывать СЗ, пока на клиенте остаются файлы,
/// доставленные push'ом (бэклог п.158, СЗ 160306: 101 МБ prime95/lhmmon + C:\OCCT
/// пережили close, потому что он не проверял остатки сам, только советовал).</summary>
public class CloseLeftoverGuardTests
{
    [Fact]
    public void HasDeliveredFiles_ToolLeftover_Blocks()
        => Assert.True(CloseLeftoverGuard.HasDeliveredFiles(
            new[] { "доставленный инструмент prime95: 34 МБ" }));

    [Fact]
    public void HasDeliveredFiles_RecipeWorkDirLeftover_Blocks()
        => Assert.True(CloseLeftoverGuard.HasDeliveredFiles(
            new[] { "каталог C:\\OCCT: 5.2 МБ" }));

    [Fact]
    public void HasDeliveredFiles_OnlyOrphanTask_DoesNotBlock()
        // Чужая задача планировщика — повод для client info, но не для отказа close:
        // guard ловит ровно доставленные push'ом файлы, а не всё подряд.
        => Assert.False(CloseLeftoverGuard.HasDeliveredFiles(
            new[] { "задача szdiag-lhmmon: Ready (без номера СЗ — чей хвост, неизвестно)" }));

    [Fact]
    public void HasDeliveredFiles_Empty_DoesNotBlock()
        => Assert.False(CloseLeftoverGuard.HasDeliveredFiles(Array.Empty<string>()));

    // review W2 I-2: `каталог C:\ProgramData\szdiag\jobs: 0.1 МБ` появляется после ЛЮБОГО
    // `exec --detach`, `каталог …\sensors` — после любого `sensors start`. Раньше guard блокировал
    // на ЛЮБОМ слове «каталог», и close отказывал бы почти всегда в реальном потоке работы.
    [Fact]
    public void HasDeliveredFiles_OwnJobsDir_DoesNotBlock()
        => Assert.False(CloseLeftoverGuard.HasDeliveredFiles(
            new[] { "каталог C:\\ProgramData\\szdiag\\jobs: 0.1 МБ" }));

    [Fact]
    public void HasDeliveredFiles_OwnSensorsDir_DoesNotBlock()
        => Assert.False(CloseLeftoverGuard.HasDeliveredFiles(
            new[] { "каталог C:\\ProgramData\\szdiag\\sensors: 2.4 МБ" }));

    [Fact]
    public void IsBlocking_DistinguishesOwnDirsFromDeliveredAndRecipeDirs()
    {
        Assert.False(CloseLeftoverGuard.IsBlocking("каталог C:\\ProgramData\\szdiag\\jobs: 0.1 МБ"));
        Assert.True(CloseLeftoverGuard.IsBlocking("каталог C:\\OCCT: 5.2 МБ"));
        Assert.True(CloseLeftoverGuard.IsBlocking("доставленный инструмент prime95: 34 МБ"));
    }
}
