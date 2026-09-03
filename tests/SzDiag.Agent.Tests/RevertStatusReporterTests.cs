using System.Net;
using System.Text;
using System.Text.Json;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class RevertStatusReporterTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _reply;
        public List<(string Path, string Body)> Calls { get; } = new();

        public StubHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> reply) => _reply = reply;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            var (status, response) = _reply(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    private static HttpClient Client(StubHandler handler)
        => new(handler) { BaseAddress = new Uri("http://hub.local") };

    [Fact]
    public async Task ReportAsync_PostsToRevertStatusRoute()
    {
        var handler = new StubHandler((_, _) => (HttpStatusCode.OK, "{}"));
        var reporter = new RevertStatusReporter(Client(handler));

        var error = await reporter.ReportAsync("160705", success: true, "откат выполнен чисто");

        Assert.Null(error);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HubRoutes.AgentRevertStatusRoute, call.Path);
        var sent = JsonSerializer.Deserialize<RevertStatusReport>(call.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("160705", sent!.Sz);
        Assert.True(sent.Success);
    }

    [Fact]
    public async Task ReportAsync_HubError_ReturnsDescriptionInsteadOfThrowing()
    {
        var handler = new StubHandler((_, _) => (HttpStatusCode.InternalServerError, ""));
        var reporter = new RevertStatusReporter(Client(handler));

        var error = await reporter.ReportAsync("160705", success: false, "sshd не снят");

        Assert.NotNull(error);
        Assert.Contains("500", error);
    }

    [Fact]
    public async Task ReportAsync_NetworkFailure_ReturnsMessageInsteadOfThrowing()
    {
        // hub недоступен — best-effort: сам откат уже случился, а недоступность hub не
        // должна валить процесс `--revert` необработанным исключением (ровно та боль,
        // из-за которой этот отчёт вообще делается — бэклог п.59).
        var http = new HttpClient(new ThrowingHandler()) { BaseAddress = new Uri("http://hub.local") };
        var reporter = new RevertStatusReporter(http);

        var error = await reporter.ReportAsync("160705", success: false, "x");

        Assert.NotNull(error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("hub недоступен");
    }
}
