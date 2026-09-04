using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Цикл «сон → RTC-пробуждение» промотанный в CLI (бэклог п.225, СЗ 161498): забытый
/// цикл 24.08 не остановился и воскрес 26.08 при включении машины, снимать пришлось офлайн
/// из WinPE. Раньше payload был статичным heredoc'ом с захардкоженным номером СЗ — здесь
/// генерируется с реальным.</summary>
public class SleepCycleScriptTests
{
    [Fact]
    public void BuildInstall_EmbedsRealSzNumber_NotHardcoded()
    {
        // Регрессия: старый рецепт нёс `$Sz = '161498'` внутри однокавычечного heredoc'а,
        // где PowerShell-переменные не раскрываются — правка требовалась на каждую заявку.
        var script = SleepCycleScript.BuildInstall("160306");

        Assert.Contains("160306", script);
        Assert.DoesNotContain("161498", script);
    }

    [Fact]
    public void BuildInstall_StampsVersion_InPayloadAndLog()
    {
        // Бэклог п.225 пункт 2: инсталляторы, кладущие payload на клиента, должны штамповать
        // версию и печатать её в лог - иначе правка в репо создаёт ложное ощущение, что на
        // машине исправленная версия.
        var script = SleepCycleScript.BuildInstall("160306");

        Assert.Contains(SleepCycleScript.Version, script);
        Assert.Contains("PayloadVersion", script);
    }

    [Fact]
    public void BuildInstall_KeepsStartWhenAvailableFalse()
    {
        // 161498: StartWhenAvailable=true заставляет просроченную задачу сработать при
        // СЛЕДУЮЩЕМ включении - критично, что осталось false.
        var script = SleepCycleScript.BuildInstall("160306");

        Assert.Contains("<StartWhenAvailable>false</StartWhenAvailable>", script);
    }

    [Fact]
    public void BuildInstall_HasDeadlineSafety()
    {
        var script = SleepCycleScript.BuildInstall("160306", maxHours: 6);

        Assert.Contains("sleep-cycle-deadline", script);
        Assert.Contains("$MaxHours = 6", script);
    }

    [Fact]
    public void BuildInstall_RefusesWithoutConfirmRisk_WhenPasswordFound()
    {
        var script = SleepCycleScript.BuildInstall("160306", confirmRisk: false);

        Assert.Contains("$ConfirmRisk = $false", script);
        Assert.Contains("OSTANOVLENO", script);
        Assert.Contains("exit 1", script);
    }

    [Fact]
    public void BuildInstall_ConfirmRisk_SetsFlagTrue()
    {
        var script = SleepCycleScript.BuildInstall("160306", confirmRisk: true);

        Assert.Contains("$ConfirmRisk = $true", script);
    }

    [Fact]
    public void StopScript_TargetsAnySleepCycleTaskByMask()
    {
        // Стоп не должен требовать номер СЗ - на 161498 останавливать пришлось задачу,
        // оставшуюся от предыдущего запуска.
        Assert.Contains("szdiag-sleepcycle-*", SleepCycleScript.StopScript);
        Assert.Contains("stop-sleep-test", SleepCycleScript.StopScript);
    }
}
