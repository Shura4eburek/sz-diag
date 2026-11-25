using SzDiag.Agent.Zt;
using SzDiag.Contracts;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Agent;

/// <summary>Поиск hub агентом (старт, `--resume`, `--revert`) — тем же <see cref="HubLocator"/>,
/// что у апдейтера: config → LAN → указатель (specs/002, FR-007). С <paramref name="zt"/> агент
/// понимает и zt-адрес hub из указателя (US3): проба идёт через ноду ZT этого процесса.</summary>
public static class AgentHubFinder
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<HubLocation?> FindAsync(AgentOptions opts, TimeSpan? lanTimeout, CancellationToken ct,
        Action<string>? log = null, ZtHubSession? zt = null)
    {
        IReadOnlyCollection<string> modes = zt is null
            ? LocatorOptions.DefaultModes
            : [HubEndpointMode.Zt, .. LocatorOptions.DefaultModes];
        var locator = new HubLocator(
            new LocatorOptions(opts.HubUrl, opts.SignpostUrl,
                FleetPublicKey.Resolve(opts.SignPublicKey, AppContext.BaseDirectory), modes),
            Http, SeenSeqStore.NextToExe(), HubLocator.Broadcast(opts.AgentToken, lanTimeout),
            ztProbe: zt is null ? null : zt.ProbeAsync);
        var loc = await locator.LocateAsync(ct);
        if (log is not null)
        {
            // Отвергнутые источники — и при удаче, и особенно при неудаче: «почему не нашёл» видно сразу.
            foreach (var r in locator.LastRejected) log($"поиск hub — отвергнут {r}");
        }
        return loc;
    }

    /// <summary>Прямая связь с hub (та же сеть): broadcast или LAN-адрес из указателя.</summary>
    public static bool IsDirect(HubLocation loc)
        => loc.Mode is HubLocator.ModeBroadcast or HubEndpointMode.Lan;

    /// <summary>Hub найден по адресу в сервисной ZT-сети — ходить к нему только через ноду.</summary>
    public static bool IsZt(HubLocation loc) => loc.Mode == HubEndpointMode.Zt;
}
