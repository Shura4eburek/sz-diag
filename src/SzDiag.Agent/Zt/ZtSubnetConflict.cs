using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SzDiag.Agent.Zt;

/// <summary>Локальная IPv4-сеть клиента: адрес интерфейса с длиной префикса (или маршрут).</summary>
public sealed record LocalIpv4Net(string InterfaceName, IPAddress Address, int PrefixLength);

/// <summary>
/// Пересечение сервисной ZT-подсети (<c>10.88.0.0/24</c>, research R9) с собственными сетями
/// клиента (T050). libzt держит свой стек, поэтому трафик к hub не ломается, но если LAN/VPN
/// клиента уже живёт в той же подсети, адреса <c>10.88.0.x</c> в выводе агента и в
/// <c>szcli target</c> двусмысленны — оператор должен знать об этом заранее.
/// </summary>
public static class ZtSubnetConflict
{
    public const string DefaultServiceSubnet = "10.88.0.0/24";

    /// <summary>Текст предупреждения на русском или null, если пересечений нет.
    /// Петля и адаптеры самого ZeroTier (личная сеть оператора/клиента) не учитываются.</summary>
    public static string? Check(string? serviceSubnet, IEnumerable<LocalIpv4Net> locals)
    {
        if (!IPNetwork.TryParse(string.IsNullOrWhiteSpace(serviceSubnet) ? DefaultServiceSubnet : serviceSubnet,
                out var service) || service.BaseAddress.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var hits = new List<string>();
        foreach (var l in locals)
        {
            if (l.Address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IPAddress.IsLoopback(l.Address)) continue;
            if (IsZeroTierAdapter(l.InterfaceName)) continue;
            if (l.PrefixLength is < 1 or > 32) continue; // /0 — маршрут по умолчанию, не конфликт

            var local = new IPNetwork(Mask(l.Address, l.PrefixLength), l.PrefixLength);
            // Сети пересекаются, если одна содержит базовый адрес другой.
            if (!local.Contains(service.BaseAddress) && !service.Contains(local.BaseAddress)) continue;

            var entry = $"{l.InterfaceName}: {l.Address}/{l.PrefixLength}";
            if (!hits.Contains(entry)) hits.Add(entry);
        }

        if (hits.Count == 0) return null;
        return $"Внимание: сервисная ZT-подсеть {service} пересекается с сетью клиента ({string.Join("; ", hits)}). " +
               "Связь с hub идёт через встроенный стек ZT и не ломается, но адреса из этой подсети " +
               "на клиенте неоднозначны — при сбоях доступа учитывать в первую очередь.";
    }

    /// <summary>Адреса IPv4 работающих интерфейсов машины с длиной префикса (без PowerShell).</summary>
    public static IReadOnlyList<LocalIpv4Net> ReadLocal()
    {
        var result = new List<LocalIpv4Net>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var name = $"{nic.Name} ({nic.Description})";
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    result.Add(new LocalIpv4Net(name, ua.Address, ua.PrefixLength));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Нет данных об интерфейсах — проверка просто не делается, это не повод валить агент.
        }
        return result;
    }

    /// <summary>Проверка по живым интерфейсам; любые сбои — null (предупреждение необязательное).</summary>
    public static string? CheckLocal(string? serviceSubnet = null)
    {
        try { return Check(serviceSubnet, ReadLocal()); }
        catch { return null; }
    }

    private static bool IsZeroTierAdapter(string name) =>
        name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase);

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        var bits = prefix;
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(bits, 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
            bits -= 8;
        }
        return new IPAddress(bytes);
    }
}
