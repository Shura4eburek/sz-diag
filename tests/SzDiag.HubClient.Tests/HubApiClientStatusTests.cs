using System.Net;
using System.Text;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.HubClient.Tests;

public class HubApiClientStatusTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static HubApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new Stub(respond)) { BaseAddress = new Uri("http://hub") }, "mgmt");

    [Fact]
    public async Task Parses()
    {
        var c = Client(r =>
        {
            Assert.Equal("/api/status", r.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"agentPackageVersion":"76a6189","kbBackup":{"enabled":true,"lastRunAt":"2026-09-25T12:00:00Z","outcome":"Pushed","message":"ok"},"tunnel":{"state":"running","since":null}}""",
                    Encoding.UTF8, "application/json"),
            };
        });
        var s = (await c.GetStatusAsync())!;
        Assert.Equal("76a6189", s.AgentPackageVersion);
        Assert.Equal("Pushed", s.KbBackup.Outcome);
        Assert.Equal(TunnelStates.Running, s.Tunnel.State);
    }

    [Fact]
    public async Task OldHub_Null()
        => Assert.Null(await Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound)).GetStatusAsync());
}
