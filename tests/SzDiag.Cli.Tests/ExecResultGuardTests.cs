using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>Регрессия (бэклог п.212, СЗ 161498): `--result ""` уходил в hub без jobId и
/// получал 405, который CliErrors рендерил как «Hub недоступен» при полностью живом hub.
/// Эти проверки должны отсекать пустой jobId ДО похода в hub.</summary>
public class ExecResultGuardTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMissingJobId_TrueForBlank(string? jobId)
        => Assert.True(ExecResultGuard.IsMissingJobId(jobId));

    [Fact]
    public void IsMissingJobId_FalseForRealId()
        => Assert.False(ExecResultGuard.IsMissingJobId("20260824-174429-ab12cd"));

    [Fact]
    public void DetachMissingJobId_TrueWhenDetachedButNoId()
        => Assert.True(ExecResultGuard.DetachMissingJobId(detach: true, jobId: null));

    [Fact]
    public void DetachMissingJobId_FalseWhenNotDetached()
        => Assert.False(ExecResultGuard.DetachMissingJobId(detach: false, jobId: null));

    [Fact]
    public void DetachMissingJobId_FalseWhenIdPresent()
        => Assert.False(ExecResultGuard.DetachMissingJobId(detach: true, jobId: "abc"));
}
