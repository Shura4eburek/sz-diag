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
    public void Read_Success_ReturnsParsedValue()
    {
        var ps = new FakePs(new PsResult(0, "2026-07-28T10:56:01.0000000+03:00\r\n", ""));

        Assert.Equal(new DateTimeOffset(2026, 7, 28, 10, 56, 1, TimeSpan.FromHours(3)),
            BootTimeReader.Read(ps)!.Value);
    }
}
