using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class PciIdParseTests
{
    [Fact]
    public void Parse_FullWindowsId_ExtractsAllFields()
    {
        var id = PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1\4&1FC990D7&0&0019");

        Assert.Equal("10de", id.VendorId);
        Assert.Equal("2d04", id.DeviceId);
        Assert.Equal("1462", id.SubVendorId);   // младшее слово SUBSYS
        Assert.Equal("5362", id.SubDeviceId);    // старшее слово SUBSYS
        Assert.Equal("a1", id.Revision);
    }

    [Fact]
    public void Parse_NoSubsysNoRev_LeavesThoseNull()
    {
        var id = PciId.Parse(@"PCI\VEN_1002&DEV_73FF");

        Assert.Equal("1002", id.VendorId);
        Assert.Equal("73ff", id.DeviceId);
        Assert.Null(id.SubVendorId);
        Assert.Null(id.Revision);
    }

    [Fact]
    public void Parse_Garbage_Throws()
    {
        Assert.Throws<FormatException>(() => PciId.Parse("не pci вовсе"));
    }
}
