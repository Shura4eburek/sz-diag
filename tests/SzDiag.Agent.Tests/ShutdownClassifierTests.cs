using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

/// <summary>Смена boot-time сама по себе вырубоном не является: на 161312 два «аварийных
/// выключения» из пяти оказались нажатием кнопки питания (бэклог п.93).</summary>
public class ShutdownClassifierTests
{
    private static readonly DateTimeOffset Boot = new(2026, 8, 6, 12, 41, 30, TimeSpan.Zero);

    private static string Line(string bugcheck, string powerButton, DateTimeOffset? at = null)
        => $"{(at ?? Boot).ToString("o")};{bugcheck};{powerButton}";

    [Fact]
    public void PowerButtonTimestampSet_IsNotADefect()
    {
        Assert.Equal(ShutdownKind.PowerButton,
            ShutdownClassifier.Classify(Line("0", "134304824548711412"), Boot));
    }

    [Fact]
    public void ZeroEverywhere_IsHardOff()
    {
        Assert.Equal(ShutdownKind.HardOff, ShutdownClassifier.Classify(Line("0", "0"), Boot));
    }

    [Fact]
    public void BugcheckSet_IsBsod()
    {
        Assert.Equal(ShutdownKind.Bsod, ShutdownClassifier.Classify(Line("190", "0"), Boot));
    }

    [Fact]
    public void NoEvents_MeansCleanShutdown()
    {
        Assert.Equal(ShutdownKind.Clean, ShutdownClassifier.Classify("", Boot));
        Assert.Equal(ShutdownKind.Clean, ShutdownClassifier.Classify(null, Boot));
    }

    [Fact]
    public void OldEventFromPreviousLife_DoesNotCountForThisBoot()
    {
        // Событие недельной давности к текущей загрузке отношения не имеет: машину выключили
        // штатно, а 41 висит с прошлого раза.
        var stale = ShutdownClassifier.Classify(Line("0", "0", Boot.AddDays(-7)), Boot);

        Assert.Equal(ShutdownKind.Clean, stale);
    }

    [Fact]
    public void GarbageOutput_IsUnknown_AndStillCountsAsFailure()
    {
        // «Не знаем» считаем отказом: лучше лишний вопрос, чем пропущенный дефект.
        Assert.Equal(ShutdownKind.Unknown, ShutdownClassifier.Classify("что-то не то", Boot));
        Assert.True(ShutdownKind.CountsAsFailure(ShutdownKind.Unknown));
        Assert.True(ShutdownKind.CountsAsFailure(null));
    }

    [Fact]
    public void CountsAsFailure_ButtonAndCleanAreExcluded()
    {
        Assert.False(ShutdownKind.CountsAsFailure(ShutdownKind.PowerButton));
        Assert.False(ShutdownKind.CountsAsFailure(ShutdownKind.Clean));
        Assert.True(ShutdownKind.CountsAsFailure(ShutdownKind.HardOff));
        Assert.True(ShutdownKind.CountsAsFailure(ShutdownKind.Bsod));
    }

    [Fact]
    public void Script_IsAsciiAndReadsTheRightFields()
    {
        Assert.All(ShutdownClassifier.Script, c => Assert.True(c < 128));
        Assert.Contains("Id=41", ShutdownClassifier.Script);
        Assert.Contains("PowerButtonTimestamp", ShutdownClassifier.Script);
        Assert.Contains("BugcheckCode", ShutdownClassifier.Script);
    }
}
