using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class ArpTableParserTests
{
    private const string SampleOutput = """
        Interface: 10.0.0.5 --- 0xb
          Internet Address      Physical Address      Type
          10.0.0.42             a1-b2-c3-d4-e5-f6     dynamic
          10.0.0.99             00-00-00-00-00-00     dynamic
          224.0.0.22            01-00-5e-00-00-16     static
        """;

    [Fact]
    public void HasEntry_KnownIpWithRealMac_ReturnsTrue()
        => Assert.True(ArpTableParser.HasEntry(SampleOutput, "10.0.0.42"));

    [Fact]
    public void HasEntry_UnknownIp_ReturnsFalse()
        => Assert.False(ArpTableParser.HasEntry(SampleOutput, "10.0.0.200"));

    [Fact]
    public void HasEntry_AllZeroMac_ReturnsFalse()
        => Assert.False(ArpTableParser.HasEntry(SampleOutput, "10.0.0.99"));

    [Fact]
    public void HasEntry_EmptyOutput_ReturnsFalse()
        => Assert.False(ArpTableParser.HasEntry("", "10.0.0.42"));

    [Fact]
    public void HasEntry_NullOutput_ReturnsFalse()
        => Assert.False(ArpTableParser.HasEntry(null, "10.0.0.42"));
}
