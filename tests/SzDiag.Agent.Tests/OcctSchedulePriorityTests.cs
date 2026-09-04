namespace SzDiag.Agent.Tests;

/// <summary>Регрессия (бэклог п.127/п.201, СЗ 161346/161211): под штатным `Priority=Normal`
/// (донор) OCCT душит агента настолько, что не проходит даже `--detach` — ack не доходит вовсе.
/// `BelowNormal` — дешёвый обратимый шаг: освобождает OS-планировщику приоритет для процесса
/// агента над тестовым процессом, не меняя саму нагрузку теста.</summary>
public class OcctSchedulePriorityTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "SzDiag.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репо (SzDiag.sln)");
    }

    private static string ReadRecipe(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "recipes", "client", name));

    [Fact]
    public void CpuSchedule_SetsBelowNormalPriorityOnAllThreeConfigs()
    {
        var text = ReadRecipe("make-cpu-schedule.ps1");

        Assert.Contains("$c.CpuOcctConfig.Priority", text);
        Assert.Contains("$c.CpuOnlyOcctConfig.Priority", text);
        Assert.Contains("$c.CpuLinpackConfig.Priority", text);
        Assert.Contains("'BelowNormal'", text);
    }

    [Fact]
    public void GpuSchedule_SetsBelowNormalPriorityOnAllFourConfigs()
    {
        var text = ReadRecipe("make-gpu-schedule.ps1");

        Assert.Contains("$c.Gpu3dConfig.Priority", text);
        Assert.Contains("$c.VramConfig.Priority", text);
        Assert.Contains("$c.GpuUnrealConfig.Priority", text);
        Assert.Contains("$c.PowerSupplyConfig.Priority", text);
        Assert.Contains("'BelowNormal'", text);
    }
}
