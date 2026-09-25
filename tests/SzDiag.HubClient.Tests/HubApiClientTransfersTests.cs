using System.Net;
using System.Text;
using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.HubClient.Tests;

public class HubApiClientTransfersTests
{
    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
            => Task.FromResult(respond(r));
    }

    private static HubApiClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new HttpClient(new Stub(respond)) { BaseAddress = new Uri("http://hub") }, "mgmt");

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetTransfers_ParsesList()
    {
        var c = Client(r =>
        {
            Assert.Equal("/api/transfers", r.RequestUri!.AbsolutePath);
            // enum'ы числами: hub не регистрирует JsonStringEnumConverter (проверено при написании плана).
            return Json("""[{"id":"r1","sz":"161432","direction":0,"what":"occt","totalBytes":100,"doneBytes":40,"bytesPerSecond":20,"startedAt":"2026-09-25T12:00:00Z","state":0}]""");
        });
        var t = Assert.Single(await c.GetTransfersAsync());
        Assert.Equal(40, t.DoneBytes);
        Assert.Equal(TransferState.Running, t.State);
    }

    [Fact]
    public async Task GetTransfers_OldHub_ReturnsEmpty()
    {
        var c = Client(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await c.GetTransfersAsync());
    }

    [Fact]
    public async Task GetHealth_HubDown_ReturnsNull()
    {
        var c = Client(_ => throw new HttpRequestException("connection refused"));
        Assert.Null(await c.GetHealthAsync());
    }

    [Fact]
    public async Task GetHealth_Parses()
    {
        var c = Client(_ => Json("""{"threadCount":30,"availableWorkerThreads":32000,"maxWorkerThreads":32767,"availableCompletionPortThreads":1000,"maxCompletionPortThreads":1000,"pendingWorkItemCount":0,"at":"2026-09-25T12:00:00Z"}"""));
        Assert.Equal(30, (await c.GetHealthAsync())!.ThreadCount);
    }
}
