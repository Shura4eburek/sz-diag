using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SzDiag.Contracts;

/// <summary>hub не откликнулся на автообнаружение за отведённое время.</summary>
public sealed class HubNotFoundException : Exception
{
    public HubNotFoundException(string message) : base(message) { }
}

/// <summary>Находит hub в локальной сети через UDP-broadcast (см. DiscoveryProtocol).</summary>
public static class HubDiscovery
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Разослать broadcast по всем локальным подсетям и дождаться ответа hub'а.</summary>
    public static Task<string> FindHubAsync(string token, int port = DiscoveryProtocol.Port,
        TimeSpan? timeout = null, CancellationToken ct = default)
        => FindHubAsync(token, BroadcastTargets(), port, timeout, ct);

    /// <summary>Тестируемая перегрузка с явным списком адресов для отправки запроса.</summary>
    public static async Task<string> FindHubAsync(string token, IReadOnlyList<IPAddress> targets, int port,
        TimeSpan? timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var payload = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildRequest(token));

        using var udp = new UdpClient();
        // Явный bind ДО старта ReceiveLoopAsync: UdpClient() иначе привязывается лениво при
        // первом Send, а ReceiveAsync на ещё не забинженном сокете кидает исключение
        // синхронно (не await-suspend) — catch{continue} в ReceiveLoopAsync тогда крутится
        // на этом же потоке и никогда не доходит до SendAsync ниже, который и должен был
        // выполнить bind. Итог — livelock на 100% CPU. Явный Bind разрывает эту цепочку.
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.EnableBroadcast = true;
        DisableConnectionReset(udp);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = ReceiveLoopAsync(udp, linkedCts.Token);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                foreach (var target in targets)
                {
                    try { await udp.SendAsync(payload, new IPEndPoint(target, port), ct); }
                    catch { /* интерфейс мог отвалиться между вызовами — не критично */ }
                }

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;
                var waitFor = remaining < RetryInterval ? remaining : RetryInterval;

                var completed = await Task.WhenAny(receiveTask, Task.Delay(waitFor, ct));
                if (completed == receiveTask) return await receiveTask;
            }
        }
        finally
        {
            linkedCts.Cancel();
            try { await receiveTask; } catch { /* отменили — ожидаемо */ }
        }

        throw new HubNotFoundException(
            "hub не найден в сети. Проверьте подключение к сети сервисного центра или укажите HubUrl вручную в appsettings.json");
    }

    private static async Task<string> ReceiveLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (true)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { continue; } // повреждённый пакет — слушаем дальше

            var text = Encoding.UTF8.GetString(result.Buffer);
            if (DiscoveryProtocol.TryParseResponse(text, out var hubPort))
                return $"http://{result.RemoteEndPoint.Address}:{hubPort}";
        }
    }

    /// <summary>
    /// На Windows отправка UDP на адрес без слушателя иногда прилетает обратно ICMP Port
    /// Unreachable, из-за чего следующий ReceiveAsync падает с SocketException (WSAECONNRESET) —
    /// без этой настройки получался бесконечный тугой цикл retry в ReceiveLoopAsync.
    /// SIO_UDP_CONNRESET отключает этот сигнал для connectionless-сокета.
    /// </summary>
    private static void DisableConnectionReset(UdpClient udp)
    {
        const int SioUdpConnReset = -1744830452; // 0x9800000C
        try { udp.Client.IOControl((IOControlCode)SioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* не Windows или не поддерживается — не критично, просто не подавили сигнал */ }
    }

    /// <summary>Широковещательные адреса всех локальных IPv4-подсетей + 255.255.255.255 подстраховкой.</summary>
    private static IReadOnlyList<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (ua.Address.ToString().StartsWith("169.254.")) continue;
                if (ua.IPv4Mask is null) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bcast = new byte[4];
                for (var i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                targets.Add(new IPAddress(bcast));
            }
        }
        return targets.Distinct().ToList();
    }
}
