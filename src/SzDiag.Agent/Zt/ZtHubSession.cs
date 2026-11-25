namespace SzDiag.Agent.Zt;

/// <summary>Нода ZT процесса агента на время поиска hub и работы с ним (US3). Поднимается лениво —
/// только когда указатель дал zt-адрес hub и LAN не нашёл его раньше; живёт до конца процесса,
/// потому что через неё идёт сам управляющий канал (SignalR), отчёт об откате и проброс SSH.
///
/// Storage: у основного агента и <c>--resume</c> — папка СЗ (одна identity на сессию, после
/// ребута тот же узел); у headless-отката — одноразовая: если watchdog сработал при живом
/// агенте, два процесса с одной identity в сети ZT мешали бы друг другу.</summary>
public sealed class ZtHubSession : IDisposable
{
    public delegate IZtNode? NodeStarter(string storage, string network, TimeSpan timeout, Action<string>? log);

    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _probeBudget;

    private readonly NodeStarter _start;
    private readonly Action<string>? _log;
    private readonly bool _createdStorage;
    private readonly bool _alwaysWipe;
    private ZtHubTransport? _transport;

    public string StoragePath { get; }
    public IZtNode? Node { get; private set; }
    public ZtHubTransport? Transport => Node is null ? null : _transport ??= new ZtHubTransport(Node);

    public ZtHubSession(string storage, NodeStarter start, Action<string>? log = null, bool alwaysWipe = false,
        TimeSpan? probeBudget = null)
    {
        _probeBudget = probeBudget ?? TimeSpan.FromSeconds(20);
        StoragePath = storage;
        _start = start;
        _log = log;
        _alwaysWipe = alwaysWipe;
        _createdStorage = !Directory.Exists(storage);
    }

    /// <summary>libzt.dll приезжает с пакетом агента; без неё агент работает как раньше, без ZT.</summary>
    public static bool LibraryPresent => File.Exists(Path.Combine(AppContext.BaseDirectory, "libzt.dll"));

    private static IZtNode? Libzt(string s, string n, TimeSpan t, Action<string>? l) => LibztNode.Start(s, n, t, l);

    public static ZtHubSession? ForSession(string sz, Action<string>? log) =>
        LibraryPresent ? new ZtHubSession(ZtStorage.ForSession(sz), Libzt, log) : null;

    public static ZtHubSession? ForRevert(Action<string>? log) =>
        LibraryPresent ? new ZtHubSession(ZtStorage.Throwaway(), Libzt, log, alwaysWipe: true) : null;

    /// <summary>Проба zt-адреса hub для <see cref="Contracts.HubLocator"/>: поднять ноду в сети и
    /// дождаться /healthz через неё. Неудача — нода гасится (и storage стирается, если его
    /// создали здесь), чтобы не висеть в чужой сети без дела.</summary>
    public async Task<bool> ProbeAsync(string network, string url, CancellationToken ct)
    {
        if (Node is not null && !string.Equals(Node.NetworkId, network, StringComparison.OrdinalIgnoreCase))
        {
            _log?.Invoke($"zt: нода уже в сети {Node.NetworkId}, а hub в {network} — пропускаю");
            return false;
        }
        Node ??= await Task.Run(() => _start(StoragePath, network, JoinTimeout, _log), ct).ConfigureAwait(false);
        if (Node is null) return false;

        using var http = Transport!.CreateHttpClient(url);
        http.Timeout = TimeSpan.FromSeconds(8);
        var until = DateTime.UtcNow + _probeBudget;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            try
            {
                using var r = await http.GetAsync("/healthz", ct).ConfigureAwait(false);
                if (r.IsSuccessStatusCode) return true;
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            // Первые секунды после join пир ещё ищет путь — повтор, а не вердикт.
            try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
        }
        _log?.Invoke($"zt: {url} не ответил через сеть {network} за {_probeBudget.TotalSeconds:F0} с");
        StopNode(wipe: _createdStorage || _alwaysWipe);
        return false;
    }

    /// <summary>Откат: при остановке ноды storage стереть (identity и конфиг сети — следы).</summary>
    public void WipeOnStop() { if (Node is not null) Node.WipeOnDispose = true; else ZtStorage.Wipe(StoragePath); }

    private void StopNode(bool wipe)
    {
        var n = Node;
        Node = null;
        _transport = null;
        if (n is null) { if (wipe) ZtStorage.Wipe(StoragePath); return; }
        n.WipeOnDispose = false;
        n.Dispose();
        // Стираем здесь, а не в ноде: итог должен попасть в лог — откат к этому моменту уже
        // отчитался, и остаток иначе не увидел бы никто (его доберёт WipeLeftovers при следующем старте).
        if (wipe && !ZtStorage.Wipe(StoragePath))
            _log?.Invoke($"zt: storage не стёрт после остановки ноды: {StoragePath} — остаток снимется при следующем старте агента");
    }

    public void Dispose() => StopNode(wipe: _alwaysWipe || (Node?.WipeOnDispose ?? false));
}
