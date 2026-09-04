using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Транзиентный («качели нагрузка/простой») стресс промотанный в CLI из рецепта
/// <c>stress-transient.ps1</c> (бэклог п.154, #93, СЗ 161716): ровный стресс на этой заявке
/// прошёл чисто, хотя машина у клиента вырубалась пачками — убивает не плато, а срыв
/// нагрузки, который ровный тест не создаёт вообще.</summary>
public class TransientStressScriptTests
{
    [Fact]
    public void BuildScript_EmbedsRealSzNumber()
    {
        var script = TransientStressScript.BuildScript("161716");

        Assert.Contains("'161716'", script);
    }

    [Fact]
    public void BuildScript_DefaultsMatchOriginalRecipe()
    {
        // Оригинал: 60с нагрузка / 40с простой, 90 минут — воспроизведено 1:1.
        var script = TransientStressScript.BuildScript("160306");

        Assert.Contains("$OnSec    = 60", script);
        Assert.Contains("$OffSec   = 40", script);
        Assert.Contains("$TotalMin = 90", script);
    }

    [Fact]
    public void BuildScript_CustomOnOffHours_AppliedVerbatim()
    {
        var script = TransientStressScript.BuildScript("160306", onSeconds: 30, offSeconds: 20, totalHours: 2);

        Assert.Contains("$OnSec    = 30", script);
        Assert.Contains("$OffSec   = 20", script);
        Assert.Contains("$TotalMin = 120", script);
    }

    [Fact]
    public void BuildScript_NoGpu_SetsFlagFalse()
    {
        var script = TransientStressScript.BuildScript("160306", withGpu: false);

        Assert.Contains("$WithGpu  = $false", script);
    }

    [Fact]
    public void BuildScript_WithGpu_SetsFlagTrue()
    {
        var script = TransientStressScript.BuildScript("160306", withGpu: true);

        Assert.Contains("$WithGpu  = $true", script);
    }

    [Fact]
    public void BuildScript_MarksLogAfterEachLoadDrop()
    {
        // Критерий из бэклога: метка ПОСЛЕ каждого снятия нагрузки — упавшая машина оставляет
        // последнюю строку, по которой видно фазу отказа.
        var script = TransientStressScript.BuildScript("160306");

        Assert.Contains("nagruzka snyata", script);
        Assert.Contains(TransientStressScript.LogPath, script);
    }

    [Fact]
    public void PlateauCoverageWarning_MentionsTransientAsDiscriminator()
    {
        Assert.Contains("--transient", TransientStressScript.PlateauCoverageWarning);
        Assert.Contains("плато", TransientStressScript.PlateauCoverageWarning);
    }
}
