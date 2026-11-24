using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace SzDiag.Hub.Tests;

/// <summary>T039/T048: hub отвечает по локальному адресу запроса. Личная ZT-сеть — тишина,
/// сервисная ZT-сеть — без management-API, LAN и loopback — как раньше.</summary>
public class LocalAddressFilterTests
{
    private static readonly HubOptions Opts = new();

    [Theory]
    [InlineData("10.199.89.217", "/healthz")]
    [InlineData("10.199.89.1", "/agents")]
    [InlineData("::ffff:10.199.89.217", "/api/sessions")]
    public void PersonalNetwork_DeniedEntirely(string local, string path) =>
        Assert.Equal(LocalAddressFilter.Verdict.DenyAll, LocalAddressFilter.Decide(IPAddress.Parse(local), path, Opts));

    [Theory]
    [InlineData("/api")]
    [InlineData("/api/sessions")]
    [InlineData("/API/close/123456")]
    public void ServiceNetwork_ManagementDenied(string path) =>
        Assert.Equal(LocalAddressFilter.Verdict.DenyManagement, LocalAddressFilter.Decide(IPAddress.Parse("10.88.0.1"), path, Opts));

    [Theory]
    [InlineData("10.88.0.1", "/agents")]
    [InlineData("10.88.0.1", "/agent/version")]
    [InlineData("10.88.0.1", "/tools/occt/x.exe")]
    [InlineData("10.88.0.1", "/healthz")]
    [InlineData("10.88.0.1", "/apix")]
    [InlineData("127.0.0.1", "/api/sessions")]
    [InlineData("::1", "/api/sessions")]
    [InlineData("192.168.1.102", "/api/sessions")]
    public void Otherwise_Allowed(string local, string path) =>
        Assert.Equal(LocalAddressFilter.Verdict.Allow, LocalAddressFilter.Decide(IPAddress.Parse(local), path, Opts));

    [Fact]
    public void NoLocalAddress_Allowed() =>
        Assert.Equal(LocalAddressFilter.Verdict.Allow, LocalAddressFilter.Decide(null, "/api/sessions", Opts));

    [Fact]
    public void InvalidSubnet_IsReported_NotSilentlyIgnored()
    {
        var o = new HubOptions { DenySubnets = ["10.199.89.0/24", "10.199.89.0/33", "мусор"] };
        Assert.Equal(["10.199.89.0/33", "мусор"], LocalAddressFilter.InvalidSubnets(o));
    }

    [Fact]
    public void EmptyDenySubnets_DisablesOnlyThatPart()
    {
        var o = new HubOptions { DenySubnets = [] };
        Assert.Equal(LocalAddressFilter.Verdict.Allow, LocalAddressFilter.Decide(IPAddress.Parse("10.199.89.217"), "/api", o));
        Assert.Equal(LocalAddressFilter.Verdict.DenyManagement, LocalAddressFilter.Decide(IPAddress.Parse("10.88.0.1"), "/api", o));
    }
}

/// <summary>То же в собранном hub: фильтр стоит в конвейере раньше токенов.</summary>
public class LocalAddressFilterPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"szdiag-laf-{Guid.NewGuid():N}.db");
    private readonly string _kbRoot = Path.Combine(Path.GetTempPath(), $"szkb-laf-{Guid.NewGuid():N}");
    private readonly WebApplicationFactory<Program> _factory;

    public LocalAddressFilterPipelineTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Hub:AgentToken", "test-token")
             .UseSetting("Hub:ManagementToken", "mgmt-token")
             .UseSetting("Hub:SqliteConnectionString", $"Data Source={_dbPath}")
             .UseSetting("Hub:KnowledgeBaseRoot", _kbRoot)
             .ConfigureServices(s => s.AddSingleton<IStartupFilter, FakeLocalIp>())
             .WithoutSystemLogging());
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { if (Directory.Exists(_kbRoot)) Directory.Delete(_kbRoot, true); } catch { }
    }

    private async Task<HttpResponseMessage> Get(string path, string? local)
    {
        var c = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add("X-SzDiag-Mgmt-Token", "mgmt-token");
        if (local != null) req.Headers.Add(FakeLocalIp.Header, local);
        return await c.SendAsync(req);
    }

    [Fact]
    public async Task ManagementWithValidToken_FromServiceZt_403_FromLan_200()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/sessions", "10.88.0.1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/sessions", "192.168.1.102")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Get("/healthz", "10.88.0.1")).StatusCode);
    }

    [Fact]
    public async Task PersonalZt_ConnectionAborted()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Get("/healthz", "10.199.89.217"));
    }

    /// <summary>TestServer не даёт локального адреса — подставляем его до конвейера hub.</summary>
    private sealed class FakeLocalIp : IStartupFilter
    {
        public const string Header = "X-Test-Local-Ip";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue(Header, out var v)) ctx.Connection.LocalIpAddress = IPAddress.Parse(v!);
                await n();
            });
            next(app);
        };
    }
}
