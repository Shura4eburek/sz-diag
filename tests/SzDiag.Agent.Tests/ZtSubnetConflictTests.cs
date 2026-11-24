using System.Net;
using SzDiag.Agent.Zt;

namespace SzDiag.Agent.Tests;

public class ZtSubnetConflictTests
{
    private static LocalIpv4Net Net(string name, string ip, int prefix) => new(name, IPAddress.Parse(ip), prefix);

    [Fact]
    public void NoLocals_NoWarning() =>
        Assert.Null(ZtSubnetConflict.Check(null, []));

    [Fact]
    public void ClientLanInSameSubnet_Warns()
    {
        var warn = ZtSubnetConflict.Check("10.88.0.0/24", [Net("Ethernet", "10.88.0.15", 24)]);
        Assert.NotNull(warn);
        Assert.Contains("10.88.0.0/24", warn);
        Assert.Contains("Ethernet", warn);
    }

    [Fact]
    public void ClientSupersetRoute_Warns() =>
        Assert.NotNull(ZtSubnetConflict.Check(null, [Net("VPN", "10.20.30.40", 8)]));

    [Fact]
    public void ClientNarrowerSubnetInside_Warns() =>
        Assert.NotNull(ZtSubnetConflict.Check(null, [Net("VPN", "10.88.0.130", 25)]));

    [Fact]
    public void UnrelatedLan_NoWarning() =>
        Assert.Null(ZtSubnetConflict.Check(null, [Net("Ethernet", "192.168.1.10", 24)]));

    [Fact]
    public void AdjacentSubnet_NoWarning() =>
        Assert.Null(ZtSubnetConflict.Check(null, [Net("Ethernet", "10.88.1.10", 24)]));

    [Fact]
    public void Loopback_Ignored() =>
        Assert.Null(ZtSubnetConflict.Check(null, [Net("Loopback", "127.0.0.1", 8)]));

    [Fact]
    public void ZeroTierAdapter_Ignored() =>
        Assert.Null(ZtSubnetConflict.Check(null, [Net("ZeroTier One [abcdef]", "10.88.0.7", 24)]));

    [Fact]
    public void DefaultRoutePrefixZero_Ignored() =>
        Assert.Null(ZtSubnetConflict.Check(null, [Net("Ethernet", "0.0.0.0", 0)]));

    [Fact]
    public void InvalidServiceSubnet_NoWarning() =>
        Assert.Null(ZtSubnetConflict.Check("мусор", [Net("Ethernet", "10.88.0.15", 24)]));

    [Fact]
    public void ReadLocal_DoesNotThrow() =>
        Assert.NotNull(ZtSubnetConflict.ReadLocal());
}
