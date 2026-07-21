using System.Net;
using System.Text;
using SzDiag.Contracts;
using SzDiag.Updater;

namespace SzDiag.Updater.Tests;

public class HttpUpdateClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(_fn(req));
    }

    [Fact]
    public async Task GetVersion_SendsTokenHeader_ReturnsTrimmedBody()
    {
        string? sentToken = null;
        var http = new HttpClient(new StubHandler(req =>
        {
            sentToken = req.Headers.GetValues(HubRoutes.TokenHeader).FirstOrDefault();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("v1\n") };
        }));
        var client = new HttpUpdateClient("http://hub", "tok", http);

        var v = await client.GetVersionAsync();

        Assert.Equal("v1", v);
        Assert.Equal("tok", sentToken);
    }

    [Fact]
    public async Task DownloadPackage_WritesBodyToFile()
    {
        var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ZIPDATA")) }));
        var client = new HttpUpdateClient("http://hub", "tok", http);
        var dest = Path.Combine(Path.GetTempPath(), $"pkg-{Guid.NewGuid():N}.zip");

        try
        {
            await client.DownloadPackageAsync(dest);
            Assert.Equal("ZIPDATA", File.ReadAllText(dest));
        }
        finally { File.Delete(dest); }
    }
}
