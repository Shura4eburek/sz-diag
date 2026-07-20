using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class PciIdsParserTests
{
    // Вендор без таба; устройство — 1 таб; субсистема — 2 таба; комментарии/пустые строки.
    private const string Sample =
        "# комментарий\n" +
        "10de  NVIDIA Corporation\n" +
        "\t2d04  GB206 [GeForce RTX 5060 Ti]\n" +
        "\t\t1462 5362  RTX 5060 Ti Gaming\n" +
        "\t2505  GA106\n" +
        "1462  Micro-Star International Co., Ltd. [MSI]\n";

    [Fact]
    public void Parse_ReadsVendorsAndDevices()
    {
        var data = PciIdsParser.Parse(Sample);

        Assert.Equal("NVIDIA Corporation", data.Vendors["10de"]);
        Assert.Equal("Micro-Star International Co., Ltd. [MSI]", data.Vendors["1462"]);
        Assert.Equal(2, data.Devices.Count);   // субсистем-строки не считаются устройствами
    }

    [Fact]
    public void Parse_SplitsChipAndModel()
    {
        var data = PciIdsParser.Parse(Sample);

        var rtx = data.Devices.Single(d => d.DeviceId == "2d04");
        Assert.Equal("10de", rtx.VendorId);
        Assert.Equal("GB206", rtx.Chip);
        Assert.Equal("GeForce RTX 5060 Ti", rtx.Model);

        var plain = data.Devices.Single(d => d.DeviceId == "2505");
        Assert.Equal("GA106", plain.Chip);
        Assert.Null(plain.Model);
    }
}
