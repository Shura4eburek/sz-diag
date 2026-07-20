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
        private readonly PciDevice? _result;
        public bool Called { get; private set; }
        public FakeScraper(PciDevice? result) => _result = result;
        public Task<PciDevice?> ScrapeAsync(PciId id, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(_result);
        }
    }

    [Fact]
    public async Task Resolve_Hit_FromCache_ScraperNotCalled()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(null);
        var res = await new GpuResolver(repo, scraper)
            .ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_2D04&SUBSYS_53621462&REV_A1"));

        Assert.Equal(GpuSource.Cache, res.Source);
        Assert.Equal("GeForce RTX 5060 Ti", res.Model);
        Assert.Equal("NVIDIA Corporation", res.VendorName);
        Assert.Equal("MSI", res.SubVendorName);
        Assert.False(scraper.Called);
    }

    [Fact]
    public async Task Resolve_Miss_ScraperFills_AndPersists()
    {
        var repo = await SeededRepoAsync();
        var scraper = new FakeScraper(new PciDevice("10de", "ffff", "GH100 [H100]", "GH100", "H100"));
        var resolver = new GpuResolver(repo, scraper);

        var res = await resolver.ResolveAsync(PciId.Parse(@"PCI\VEN_10DE&DEV_FFFF"));
        Assert.Equal(GpuSource.Scraper, res.Source);
        Assert.Equal("H100", res.Model);
        Assert.True(scraper.Called);

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

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
