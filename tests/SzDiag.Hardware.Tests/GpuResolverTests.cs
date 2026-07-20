using SzDiag.Hardware;

namespace SzDiag.Hardware.Tests;

public class GpuResolverTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szres-{Guid.NewGuid():N}.db");
    private GpuRepository _repo = null!;

    private async Task<GpuRepository> SeededRepoAsync()
    {
        _repo = new GpuRepository($"Data Source={_dbPath}");
        await _repo.InitializeAsync();
        await _repo.ImportAsync(new PciIdsData(
            new Dictionary<string, string> { ["10de"] = "NVIDIA Corporation", ["1462"] = "MSI" },
            new[] { new PciDevice("10de", "2d04", "GB206 [GeForce RTX 5060 Ti]", "GB206", "GeForce RTX 5060 Ti") }));
        return _repo;
    }

    private sealed class FakeScraper : IGpuScraper
    {
        private readonly PciDevice? _device;
        private readonly ScrapedCard? _card;
        private readonly Exception? _cardThrows;
        public bool DeviceCalled { get; private set; }
        public bool CardCalled { get; private set; }

        public FakeScraper(PciDevice? device = null, ScrapedCard? card = null, Exception? cardThrows = null)
        { _device = device; _card = card; _cardThrows = cardThrows; }

        public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        { DeviceCalled = true; return Task.FromResult(_device); }

        public Task<ScrapedCard?> ScrapeCardAsync(PciId id, string? model, CancellationToken ct = default)
        {
            CardCalled = true;
            if (_cardThrows is not null) throw _cardThrows;
            return Task.FromResult(_card);
        }
    }

    private static ScrapedCard Card() => new(
        "1462", "5362", "MSI", "Ventus 2x OC Plus",
        "16384 MB", "GDDR7", "2407 MHz", "2602 MHz", "1750 MHz",
        "180.0 W", "180.0 W", "1x HDMI, 3x DisplayPort", "2025-03-15", "98.06.1F.00.CD",
        "https://www.techpowerup.com/vgabios/275654/");

    [Fact]
    public async Task Resolve_Hit_FromCache_ScraperNotCalled()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper();
        var res = await new GpuResolver(repo, scraper)
            .ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));

        Assert.Equal(GpuSource.Cache, res.Source);
        Assert.Equal("GeForce RTX 5060 Ti", res.Model);
        Assert.Equal("NVIDIA Corporation", res.VendorName);
        Assert.Equal("MSI", res.SubVendorName);
        Assert.False(scraper.DeviceCalled);
    }

    [Fact]
    public async Task Resolve_Miss_ScraperFills_AndPersists()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(device: new PciDevice("10de", "ffff", "GH100 [H100]", "GH100", "H100"));
        var resolver = new GpuResolver(repo, scraper);

        var res = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_FFFF"));
        Assert.Equal(GpuSource.Scraper, res.Source);
        Assert.Equal("H100", res.Model);
        Assert.True(scraper.DeviceCalled);

        // записано в БД — повторный резолв берёт из кэша
        var again = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_FFFF"));
        Assert.Equal(GpuSource.Cache, again.Source);
    }

    [Fact]
    public async Task Resolve_Miss_StubScraper_Unresolved_ButVendorKnown()
    {
        var repo = await SeededRepoAsync();
        var res = await new GpuResolver(repo, new NotImplementedGpuScraper())
            .ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_EEEE&SUBSYS_00001462"));

        Assert.Equal(GpuSource.Unresolved, res.Source);
        Assert.Null(res.Model);
        Assert.Equal("NVIDIA Corporation", res.VendorName);
        Assert.Equal("MSI", res.SubVendorName);
    }

    [Fact]
    public async Task Resolve_CardMiss_ScraperFills_AndPersists()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(card: Card());
        var resolver = new GpuResolver(repo, scraper);

        var res = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));
        Assert.NotNull(res.Card);
        Assert.Equal("Ventus 2x OC Plus", res.Card!.CardName);
        Assert.Equal("5362", res.SubDeviceId);
        Assert.True(scraper.CardCalled);

        // повторный резолв — карта из БД, скрапер card не зван
        var scraper2 = new FakeScraper(card: Card());
        var again = await new GpuResolver(repo, scraper2).ResolveAsync(
            PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));
        Assert.NotNull(again.Card);
        Assert.False(scraper2.CardCalled);
    }

    [Fact]
    public async Task Resolve_CardScraperBlocked_CardNull_DeviceIntact()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(cardThrows: new ScrapeBlockedException("blocked"));
        var res = await new GpuResolver(repo, scraper).ResolveAsync(
            PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));

        Assert.Null(res.Card);
        Assert.Equal("GeForce RTX 5060 Ti", res.Model);   // device-часть цела
    }

    [Fact]
    public async Task Resolve_NoSubDevice_CardScraperNotCalled()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(card: Card());
        var res = await new GpuResolver(repo, scraper).ResolveAsync(
            PciId.Parse(@"PCI\VEN_10DE&DEV_2D04"));        // без SUBSYS

        Assert.Null(res.Card);
        Assert.False(scraper.CardCalled);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
