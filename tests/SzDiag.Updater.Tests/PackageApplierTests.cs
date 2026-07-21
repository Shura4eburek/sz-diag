using System.IO.Compression;
using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

public class PackageApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pa-{Guid.NewGuid():N}");
    private string Target => Path.Combine(_root, "target");
    private string ZipPath => Path.Combine(_root, "package.zip");

    public PackageApplierTests()
    {
        Directory.CreateDirectory(Target);
        // Локальные файлы клиента, которые нельзя перетирать:
        File.WriteAllText(Path.Combine(Target, "appsettings.json"), "LOCAL-CONFIG");
        Directory.CreateDirectory(Path.Combine(Target, "tools"));
        File.WriteAllText(Path.Combine(Target, "tools", "big.exe"), "LOCAL-TOOL");

        // Пакет: свежий agent.exe + version.txt + попытка перетереть appsettings/tools.
        using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
        AddEntry(zip, "SzDiag.Agent.exe", "NEW-AGENT");
        AddEntry(zip, "version.txt", "v2");
        AddEntry(zip, "appsettings.json", "SHOULD-NOT-OVERWRITE");
        AddEntry(zip, "tools/big.exe", "SHOULD-NOT-OVERWRITE");
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name);
        using var w = new StreamWriter(e.Open());
        w.Write(content);
    }

    [Fact]
    public void Apply_WritesPackageFiles_ButKeepsLocalConfigAndTools()
    {
        PackageApplier.Apply(ZipPath, Target);

        Assert.Equal("NEW-AGENT", File.ReadAllText(Path.Combine(Target, "SzDiag.Agent.exe")));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(Target, "version.txt")));
        // Локальные не тронуты:
        Assert.Equal("LOCAL-CONFIG", File.ReadAllText(Path.Combine(Target, "appsettings.json")));
        Assert.Equal("LOCAL-TOOL", File.ReadAllText(Path.Combine(Target, "tools", "big.exe")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
