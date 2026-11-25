using System.Net;

namespace SzDiag.Hub;

/// <summary>Что делать с запросом по ЛОКАЛЬНОМУ адресу, на который он пришёл (T048, specs/002).
/// Hub слушает 0.0.0.0, а машина-hub сидит сразу в нескольких сетях: LAN, личная ZT-сеть
/// владельца, сервисная ZT-сеть с клиентами. Сервисная сеть публичная — её участник может
/// оказаться кем угодно, поэтому management-API оттуда закрыт независимо от токена; личная
/// сеть hub-у не нужна вовсе. Bind на конкретные IP не годится: у ноута LAN по DHCP.</summary>
public static class LocalAddressFilter
{
    public enum Verdict { Allow, DenyAll, DenyManagement }

    public static Verdict Decide(IPAddress? local, PathString path, HubOptions opts)
    {
        if (local is null) return Verdict.Allow; // TestServer / не TCP — фильтровать нечего
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();

        foreach (var cidr in opts.DenySubnets)
            if (Contains(cidr, local)) return Verdict.DenyAll;

        if (Contains(opts.ZeroTier.Subnet, local) && path.StartsWithSegments("/api"))
            return Verdict.DenyManagement;

        return Verdict.Allow;
    }

    private static bool Contains(string? cidr, IPAddress ip) =>
        !string.IsNullOrWhiteSpace(cidr) && IPNetwork.TryParse(cidr.Trim(), out var net) && net.Contains(ip);

    /// <summary>Подсети из конфига, которые не разбираются — чтобы опечатка не выключала фильтр молча.</summary>
    public static IEnumerable<string> InvalidSubnets(HubOptions opts) =>
        opts.DenySubnets.Append(opts.ZeroTier.Subnet)
            .Where(c => !string.IsNullOrWhiteSpace(c) && !IPNetwork.TryParse(c.Trim(), out _));

    public static IApplicationBuilder UseLocalAddressFilter(this IApplicationBuilder app, HubOptions opts)
    {
        foreach (var bad in InvalidSubnets(opts))
            Console.WriteLine($"[hub] ВНИМАНИЕ: подсеть «{bad}» в Hub.DenySubnets/Hub.ZeroTier.Subnet не разобрана — по ней фильтра нет");

        return app.Use(async (ctx, next) =>
        {
            switch (Decide(ctx.Connection.LocalIpAddress, ctx.Request.Path, opts))
            {
                case Verdict.DenyAll:
                    ctx.Abort(); // ни ответа, ни признака, что здесь кто-то есть
                    return;
                case Verdict.DenyManagement:
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                default:
                    await next();
                    return;
            }
        });
    }
}
