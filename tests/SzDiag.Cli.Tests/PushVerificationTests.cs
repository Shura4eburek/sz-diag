using SzDiag.Cli;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Cli.Tests;

/// <summary>`szcli push --verify-size/--verify-signer` — проверка инструмента ДО доставки
/// на клиента (бэклог, СЗ 163013: сверку размера и подписи прошивки LED-контроллера делали
/// руками, `push` этого не умел вовсе).</summary>
public class PushVerificationTests
{
    private static ToolCatalogInfo Catalog(params ToolInfo[] tools) => new("C:\\tools", true, tools);

    [Fact]
    public void CheckSize_Matches_Ok()
    {
        var res = PushVerification.CheckSize(Catalog(new ToolInfo("occt", 3, 3_885_605)), "occt", 3_885_605);
        Assert.True(res.Ok);
    }

    [Fact]
    public void CheckSize_Mismatch_Fails()
    {
        var res = PushVerification.CheckSize(Catalog(new ToolInfo("occt", 3, 1000)), "occt", 3_885_605);
        Assert.False(res.Ok);
        Assert.Contains("размер не совпал", res.Error);
    }

    [Fact]
    public void CheckSize_UnknownTool_Fails()
    {
        var res = PushVerification.CheckSize(Catalog(new ToolInfo("occt", 3, 1000)), "iteshfu", 1000);
        Assert.False(res.Ok);
        Assert.Contains("не найден", res.Error);
    }

    [Fact]
    public void CheckSize_NoCatalog_Fails()
    {
        var res = PushVerification.CheckSize(null, "occt", 1000);
        Assert.False(res.Ok);
    }

    [Fact]
    public void CheckSigner_AllMatch_Ok()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szpush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "ITESHFU.exe");
        File.WriteAllText(exe, "stub");
        try
        {
            var res = PushVerification.CheckSigner(dir, "ITE Tech. Inc.",
                _ => "ITE Tech. Inc.");
            Assert.True(res.Ok);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CheckSigner_WrongSigner_Fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szpush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "malicious.exe");
        File.WriteAllText(exe, "stub");
        try
        {
            var res = PushVerification.CheckSigner(dir, "ITE Tech. Inc.", _ => "Evil Corp");
            Assert.False(res.Ok);
            Assert.Contains("Evil Corp", res.Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CheckSigner_Unsigned_Fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szpush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "unsigned.exe");
        File.WriteAllText(exe, "stub");
        try
        {
            var res = PushVerification.CheckSigner(dir, "ITE Tech. Inc.", _ => null);
            Assert.False(res.Ok);
            Assert.Contains("не подписан", res.Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CheckSigner_MissingDir_Fails()
    {
        var res = PushVerification.CheckSigner(@"C:\does-not-exist-szdiag", "CN", _ => "CN");
        Assert.False(res.Ok);
        Assert.Contains("не найден", res.Error);
    }

    [Fact]
    public void CheckSigner_NoBinaries_Fails()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"szpush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "hi");
        try
        {
            var res = PushVerification.CheckSigner(dir, "CN", _ => "CN");
            Assert.False(res.Ok);
            Assert.Contains("нет .exe/.dll", res.Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
