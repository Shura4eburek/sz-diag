using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Колонка «Была занята» отвечает на вопрос «под чем машина вырубилась». На 160306
/// она двадцать минут показывала давно законченную диагностику, пока шёл OCCT (бэклог п.73).</summary>
public class ActivityProbeTests
{
    [Fact]
    public void RunningStressTool_BeatsIdleLabel()
    {
        var text = ActivityProbe.Describe(new[] { "OCCTCmd" }, backgroundJobs: 0);

        Assert.Contains("стресс: OCCTCmd", text);
    }

    [Fact]
    public void BackgroundJobs_AreCountedToo()
    {
        var text = ActivityProbe.Describe(Array.Empty<string>(), backgroundJobs: 2);

        Assert.Contains("фоновых задач: 2", text);
    }

    [Fact]
    public void StressAndJobs_AreShownTogether()
    {
        var text = ActivityProbe.Describe(new[] { "FurMark", "lhmmon" }, backgroundJobs: 1);

        Assert.Contains("FurMark", text);
        Assert.Contains("lhmmon", text);
        Assert.Contains("фоновых задач: 1", text);
    }

    [Fact]
    public void NothingRunning_UsesIdleLabel()
    {
        Assert.Equal("— готов", ActivityProbe.Describe(Array.Empty<string>(), 0));
        Assert.Equal("— готов (после ребута)",
            ActivityProbe.Describe(Array.Empty<string>(), 0, "— готов (после ребута)"));
    }

    [Fact]
    public void Duplicates_AreCollapsed()
    {
        var text = ActivityProbe.Describe(new[] { "OCCT", "occt" }, 0);

        Assert.Equal("стресс: OCCT", text);
    }

    [Fact]
    public void RunningStress_DoesNotThrowOnThisMachine()
    {
        // Опрос идёт в фоновом цикле рядом с heartbeat и обязан быть безопасным всегда.
        var running = ActivityProbe.RunningStress();

        Assert.NotNull(running);
    }
}
