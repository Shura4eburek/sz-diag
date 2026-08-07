using SzDiag.Agent;
using Xunit;

namespace SzDiag.Agent.Tests;

public class BootTimeReaderTests
{
    private sealed class FakePs : IPowerShellRunner
    {
        private readonly PsResult _result;
        public FakePs(PsResult result) => _result = result;
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null) => _result;
    }

    private sealed class ThrowingPs : IPowerShellRunner
    {
        public PsResult Run(string script, bool throwOnError = true, TimeSpan? timeout = null)
            => throw new PowerShellTimeoutException("timeout");
    }

    [Fact]
    public void Parse_IsoRoundtrip_ReturnsSameInstant()
    {
        var parsed = BootTimeReader.Parse("2026-07-28T10:56:01.0000000+03:00");

        Assert.NotNull(parsed);
        Assert.Equal(new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.FromHours(3)), parsed!.Value);
    }

    [Fact]
    public void Parse_IgnoresSurroundingWhitespaceAndBlankLines()
    {
        var parsed = BootTimeReader.Parse("\r\n  2026-07-28T10:56:01.0000000+03:00  \r\n");

        Assert.Equal(new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.FromHours(3)), parsed!.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не дата")]
    public void Parse_Garbage_ReturnsNull(string stdout) => Assert.Null(BootTimeReader.Parse(stdout));

    [Fact]
    public void Read_NonZeroExitCode_ReturnsNull()
    {
        var ps = new FakePs(new PsResult(1, "2026-07-28T10:56:01.0000000+03:00", "boom"));

        Assert.Null(BootTimeReader.Read(ps));
    }

    [Fact]
    public void Read_PowerShellThrows_ReturnsNullInsteadOfCrashing()
    {
        // Не смогли прочитать — отдаём «неизвестно». Ложный boot-time хуже отсутствующего:
        // hub принял бы его за ребут.
        Assert.Null(BootTimeReader.Read(new ThrowingPs()));
    }

    [Fact]
    public void Read_Success_ReturnsBootTimeFromUptime()
    {
        // Агент спрашивает у клиента АПТАЙМ, а не абсолютное время загрузки: разность двух
        // локальных времён не зависит от таймзоны, и кривая TZ в WinPE перестаёт всё ломать
        // (бэклог п.90).
        var ps = new FakePs(new PsResult(0, "3600\r\n", ""));

        var boot = BootTimeReader.Read(ps);

        Assert.NotNull(boot);
        Assert.InRange((DateTimeOffset.Now - boot!.Value).TotalMinutes, 59.5, 60.5);
    }

    [Fact]
    public void ParseUptimeSeconds_UsesAgentClock_RegardlessOfClientTimezone()
    {
        var now = new DateTimeOffset(2026, 8, 6, 17, 26, 0, TimeSpan.FromHours(3));

        var boot = BootTimeReader.ParseUptimeSeconds("4044.5", now);

        Assert.Equal(new DateTimeOffset(2026, 8, 6, 16, 18, 35, TimeSpan.FromHours(3)), boot);
    }

    [Fact]
    public void ParseUptimeSeconds_CommaDecimalSeparator_IsUnderstood()
    {
        var now = new DateTimeOffset(2026, 8, 6, 17, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal(now.AddSeconds(-90), BootTimeReader.ParseUptimeSeconds("90,0", now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("не число")]
    [InlineData("-10")]      // отрицательный аптайм = битые часы, лучше «неизвестно»
    public void ParseUptimeSeconds_Garbage_ReturnsNull(string stdout)
        => Assert.Null(BootTimeReader.ParseUptimeSeconds(stdout, DateTimeOffset.Now));
}
