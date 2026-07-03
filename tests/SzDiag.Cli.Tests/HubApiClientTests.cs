using System.Net;
using System.Text;
using SzDiag.Cli;
using Xunit;

namespace SzDiag.Cli.Tests;

public class HubApiClientTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _code;
        private readonly string _json;
        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHandler(HttpStatusCode code, string json = "")
        {
            _code = code;
            _json = json;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(_code)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private static HubApiClient NewClient(StubHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://hub") };
        return new HubApiClient(http, "mgmt-token");
    }

    [Fact]
    public async Task GetSessions_ParsesJsonAndSendsToken()
    {
        var json = """
        [{"sz":"156864","ip":"10.0.0.42","hostname":"PC-1","status":0,
          "connectedAt":"2026-07-01T12:00:00+00:00","lastHeartbeat":"2026-07-01T12:00:00+00:00"}]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var client = NewClient(handler);

        var sessions = await client.GetSessionsAsync();

        Assert.Equal("156864", sessions.Single().Sz);
        Assert.Equal("mgmt-token", handler.LastRequest!.Headers.GetValues("X-SzDiag-Mgmt-Token").Single());
    }

    [Fact]
    public async Task Close_Ok_ReturnsTrue()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK));
        Assert.True(await client.CloseAsync("156864"));
    }

    [Fact]
    public async Task Close_NotFound_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.False(await client.CloseAsync("000000"));
    }

    [Fact]
    public async Task GetTarget_NotFound_ReturnsNull()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.Null(await client.GetTargetAsync("000000"));
    }

    [Fact]
    public async Task TriggerTest_Ok_ReturnsTrue()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.OK));
        Assert.True(await client.TriggerTestAsync("156864"));
    }

    [Fact]
    public async Task TriggerTest_NotFound_ReturnsFalse()
    {
        var client = NewClient(new StubHandler(HttpStatusCode.NotFound));
        Assert.False(await client.TriggerTestAsync("000000"));
    }

    [Fact]
    public async Task TriggerTest_WithFilter_AppendsQuery()
    {
        var handler = new StubHandler(HttpStatusCode.OK);
        var client = NewClient(handler);

        await client.TriggerTestAsync("156864", "occt");

        Assert.Contains("filter=occt", handler.LastRequest!.RequestUri!.Query);
    }
}
