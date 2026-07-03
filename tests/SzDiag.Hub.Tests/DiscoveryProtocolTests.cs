using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class DiscoveryProtocolTests
{
    [Fact]
    public void BuildRequest_TryParseRequest_RoundTrips()
    {
        var msg = DiscoveryProtocol.BuildRequest("dev-token");

        Assert.True(DiscoveryProtocol.TryParseRequest(msg, out var token));
        Assert.Equal("dev-token", token);
    }

    [Fact]
    public void TryParseRequest_WrongPrefix_ReturnsFalse()
        => Assert.False(DiscoveryProtocol.TryParseRequest("garbage", out _));

    [Fact]
    public void BuildResponse_TryParseResponse_RoundTrips()
    {
        var msg = DiscoveryProtocol.BuildResponse(5099);

        Assert.True(DiscoveryProtocol.TryParseResponse(msg, out var port));
        Assert.Equal(5099, port);
    }

    [Fact]
    public void TryParseResponse_WrongPrefix_ReturnsFalse()
        => Assert.False(DiscoveryProtocol.TryParseResponse("garbage", out _));

    [Fact]
    public void TryParseResponse_NonNumericPort_ReturnsFalse()
        => Assert.False(DiscoveryProtocol.TryParseResponse("SZDIAG-HUB:abc", out _));
}
