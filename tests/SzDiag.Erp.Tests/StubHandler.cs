using System.Net;
using System.Text;

namespace SzDiag.Erp.Tests;

/// <summary>Отвечает заранее заданным телом; помнит, что и в каком порядке спрашивали.</summary>
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _reply;

    public List<string> Calls { get; } = new();

    public StubHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> reply) => _reply = reply;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(body);
        var (status, response) = _reply(request, body);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        };
    }
}
