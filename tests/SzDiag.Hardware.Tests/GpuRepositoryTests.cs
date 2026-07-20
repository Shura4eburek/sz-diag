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

    private static ScrapedCard SampleCard(string? name = "Ventus 2x OC Plus") => new(
        "1462", "5351", "MSI", name,
        "16384 MB", "GDDR7", "2407 MHz", "2602 MHz", "1750 MHz",
        "180.0 W", "180.0 W", "1x HDMI, 3x DisplayPort", "2025-03-15", "98.06.1F.00.CD",
        "https://www.techpowerup.com/vgabios/275654/");

    [Fact]
    public async Task UpsertCard_ThenLookup_ReturnsCard()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.UpsertCardAsync(SampleCard());

        var card = await repo.LookupCardAsync("1462", "5351");
        Assert.NotNull(card);
        Assert.Equal("Ventus 2x OC Plus", card!.CardName);
        Assert.Equal("180.0 W", card.PowerTarget);
        Assert.Contains("HDMI", card.Outputs);
    }

    [Fact]
    public async Task LookupCard_Missing_ReturnsNull()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        Assert.Null(await repo.LookupCardAsync("1462", "ffff"));
    }

    [Fact]
    public async Task UpsertCard_Twice_Updates()
    {
        var repo = NewRepo();
        await repo.InitializeAsync();
        await repo.UpsertCardAsync(SampleCard("Old Name"));
        await repo.UpsertCardAsync(SampleCard("Ventus 2x OC Plus"));
        var card = await repo.LookupCardAsync("1462", "5351");
        Assert.Equal("Ventus 2x OC Plus", card!.CardName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
