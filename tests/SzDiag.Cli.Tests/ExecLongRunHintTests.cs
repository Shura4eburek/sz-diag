using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>22-минутное наблюдение под FurMark, запущенное обычным `exec`, пропало целиком
/// при обрыве харнесса — «промежуточно» синхронный exec ничего не показывает, поэтому
/// подсказка про `--detach` должна приходить до старта, а не после потери данных (п.220).</summary>
public class ExecLongRunHintTests
{
    [Fact]
    public void ShouldWarn_ShortTimeout_NoWarning()
        => Assert.False(ExecLongRunHint.ShouldWarn(60, detach: false));

    [Fact]
    public void ShouldWarn_LongTimeout_WithoutDetach_Warns()
        => Assert.True(ExecLongRunHint.ShouldWarn(1320, detach: false));

    [Fact]
    public void ShouldWarn_LongTimeout_WithDetach_NoWarning()
        => Assert.False(ExecLongRunHint.ShouldWarn(1320, detach: true));

    [Fact]
    public void ShouldWarn_NoTimeout_NoWarning()
        => Assert.False(ExecLongRunHint.ShouldWarn(null, detach: false));

    [Fact]
    public void ShouldWarn_ExactlyAtThreshold_NoWarning()
        => Assert.False(ExecLongRunHint.ShouldWarn(ExecLongRunHint.ThresholdSeconds, detach: false));
}
