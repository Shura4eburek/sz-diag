using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class GpuRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szgpu-{Guid.NewGuid():N}.db");
    private GpuRepository NewRepo() => new($"Data Source={_dbPath}");

    private static PciIdsData Sample() => new(
        new Dictionary<string, string> { ["10de"] = "NVIDIA Corporation", ["1462"] = "MSI" },
        new[] { new PciDevice("10de", "2d04", "GB206 [GeForce RTX 5060 Ti]", "GB206", "GeForce RTX 5060 Ti") });

    [Fact]
    public async Task ImportThenLookup_ReturnsDeviceAndVendor()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.ImportAsync(Sample());

        var dev = await repo.LookupDeviceAsync("10de", "2d04");
        Assert.NotNull(dev);
        Assert.Equal("GeForce RTX 5060 Ti", dev!.Model);
        Assert.Equal("NVIDIA Corporation", await repo.LookupVendorAsync("10de"));
        Assert.Equal("MSI", await repo.LookupVendorAsync("1462"));
    }

    [Fact]
    public async Task LookupDevice_Missing_ReturnsNull()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        Assert.Null(await repo.LookupDeviceAsync("10de", "ffff"));
    }

    [Fact]
    public async Task Upsert_InsertsThenUpdates()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();

        await repo.UpsertDeviceAsync(new PciDevice("10de", "aaaa", "Old", "Old", null));
        await repo.UpsertDeviceAsync(new PciDevice("10de", "aaaa", "New [Model]", "New", "Model"));

        var dev = await repo.LookupDeviceAsync("10de", "aaaa");
        Assert.Equal("Model", dev!.Model);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
