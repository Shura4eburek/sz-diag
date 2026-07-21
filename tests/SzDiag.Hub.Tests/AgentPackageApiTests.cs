using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Hub.Tests;

public class AgentPackageApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _dist = Path.Combine(Path.GetTempPath(), $"szdist-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-pkg-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-pkg-{Guid.NewGuid():N}");

    public AgentPackageApiTests(WebApplicationFactory<Program> factory)
    {
        Directory.CreateDirectory(_dist);
        File.WriteAllText(Path.Combine(_dist, "version.txt"), "abc123");
        File.WriteAllText(Path.Combine(_dist, "package.zip"), "ZIPBYTES");
        File.WriteAllText(Path.Combine(_dist, "package.sha256"), "deadbeef");

        _factory = factory.WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:AgentDistRoot", _dist)
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot));
    }

    private HttpClient WithToken()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add(HubRoutes.TokenHeader, "test-token");
        return c;
    }

    [Fact]
    public async Task Version_WithToken_ReturnsVersionString()
    {
        var r = await WithToken().GetAsync(HubRoutes.AgentVersionRoute);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("abc123", (await r.Content.ReadAsStringAsync()).Trim());
    }

    [Fact]
    public async Task Version_NoToken_Unauthorized()
    {
        var r = await _factory.CreateClient().GetAsync(HubRoutes.AgentVersionRoute);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Package_WithToken_ReturnsZip()
    {
        var r = await WithToken().GetAsync(HubRoutes.AgentPackageRoute);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("application/zip", r.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task PackageSha256_WithToken_ReturnsHash()
    {
        var r = await WithToken().GetAsync(HubRoutes.AgentPackageSha256Route);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("deadbeef", (await r.Content.ReadAsStringAsync()).Trim());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dist, recursive: true); } catch { }
        try { File.Delete(_dbPath); } catch { }
        try { Directory.Delete(_kbRoot, recursive: true); } catch { }
    }
}
