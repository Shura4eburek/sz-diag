using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>«Чем была занята» едет в журнал СЗ сразу при потере heartbeat, а не только
/// постфактум (бэклог п.202, СЗ 161972: отвал под дисковым тестом пришлось выяснять руками).</summary>
public class OfflineSweeperFormatTests
{
    [Fact]
    public void FormatLostMessage_NoActivity_PlainMessage()
        => Assert.Equal("зв'язок втрачено (heartbeat не приходить)", OfflineSweeper.FormatLostMessage(null));

    [Fact]
    public void FormatLostMessage_WithActivity_IncludesIt()
        => Assert.Equal(
            "зв'язок втрачено (heartbeat не приходить), була зайнята: Disk linear scan",
            OfflineSweeper.FormatLostMessage("Disk linear scan"));

    [Fact]
    public void FormatLostMessage_BlankActivity_TreatedAsNone()
        => Assert.Equal("зв'язок втрачено (heartbeat не приходить)", OfflineSweeper.FormatLostMessage("  "));
}
