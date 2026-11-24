using System.Net;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Connections.Client;

namespace SzDiag.Agent.Zt;

/// <summary>Как агент ходит к hub, найденному по адресу в сервисной ZT-сети (T044): HttpClient и
/// SignalR идут через ConnectCallback ноды вместо обычных сокетов ОС. Для прочих режимов
/// (LAN, туннель) транспорт не нужен — места вызова передают null и работают как раньше.</summary>
public sealed class ZtHubTransport(IZtNode node)
{
    public IZtNode Node => node;

    /// <summary>Новый handler на каждый HttpClient/подключение: SignalR освобождает handler из
    /// HttpMessageHandlerFactory вместе с подключением (проверено спайком).</summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        ConnectCallback = async (ctx, ct) =>
        {
            if (!IPAddress.TryParse(ctx.DnsEndPoint.Host, out var ip))
                throw new HttpRequestException($"zt: адрес hub должен быть IP в сервисной сети, а не «{ctx.DnsEndPoint.Host}»");
            return await node.ConnectAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct).ConfigureAwait(false);
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public HttpClient CreateHttpClient(string baseUrl) =>
        new(CreateHandler(), disposeHandler: true) { BaseAddress = new Uri(baseUrl) };

    /// <summary>SignalR поверх ZT: WebSocket через тот же handler (спайк, п.3 R8).</summary>
    public void Configure(HttpConnectionOptions o)
    {
        o.HttpMessageHandlerFactory = _ => CreateHandler();
        o.WebSocketFactory = async (ctx, ct) =>
        {
            var ws = new ClientWebSocket();
            foreach (var h in ctx.Options.Headers) ws.Options.SetRequestHeader(h.Key, h.Value);
            await ws.ConnectAsync(ctx.Uri, new HttpMessageInvoker(CreateHandler(), disposeHandler: true), ct)
                .ConfigureAwait(false);
            return ws;
        };
    }
}
