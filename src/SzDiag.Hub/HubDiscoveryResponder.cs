using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Отвечает на UDP-broadcast запросы автообнаружения hub'а в локальной сети.</summary>
public sealed class HubDiscoveryResponder : BackgroundService
{
    private readonly HubOptions _options;
    private readonly int _listenPort;

    public HubDiscoveryResponder(IOptions<HubOptions> options, int listenPort = DiscoveryProtocol.Port)
    {
        _options = options.Value;
        _listenPort = listenPort;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, _listenPort));
            DisableConnectionReset(udp);
        }
        catch (SocketException)
        {
            // Порт уже занят (напр. несколько тестовых хостов параллельно) — автообнаружение
            // просто недоступно на этом экземпляре, остальной hub продолжает работать.
            return;
        }

        using (udp)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await udp.ReceiveAsync(stoppingToken); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { continue; } // повреждённый пакет — слушаем дальше

                var text = Encoding.UTF8.GetString(result.Buffer);
                if (!DiscoveryProtocol.TryParseRequest(text, out var token)) continue;
                if (token != _options.AgentToken) continue; // чужой/битый токен — молча игнорируем

                var response = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildResponse(_options.Port));
                try { await udp.SendAsync(response, result.RemoteEndPoint, stoppingToken); }
                catch { /* отправитель мог уйти из сети — не критично */ }
            }
        }
    }

    /// <summary>
    /// На Windows отправка UDP-ответа отправителю, который уже ушёл из сети, иногда прилетает
    /// обратно ICMP Port Unreachable, из-за чего следующий ReceiveAsync падает с SocketException
    /// (WSAECONNRESET) — без этой настройки получался бесконечный тугой цикл retry в ExecuteAsync.
    /// SIO_UDP_CONNRESET отключает этот сигнал для connectionless-сокета.
    /// </summary>
    private static void DisableConnectionReset(UdpClient udp)
    {
        const int SioUdpConnReset = -1744830452; // 0x9800000C
        try { udp.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* не Windows или не поддерживается — не критично, просто не подавили сигнал */ }
    }
}
