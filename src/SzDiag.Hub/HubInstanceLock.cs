namespace SzDiag.Hub;

/// <summary>Один hub на порт. Второй экземпляр (Desk с чужим адресом запускал start-hub.cmd при
/// живом hub, 19.10) успевал до падения на занятом порту поднять службы — и `HubTunnelService`
/// по pid-файлу убивал cloudflared живого hub. Захват — до построения хоста, поэтому второй
/// экземпляр выходит, ничего не трогая.
/// Именованный семафор, а не мьютекс: не привязан к потоку и не бывает «брошенным» — убили hub,
/// закрылся последний дескриптор, объект исчез.</summary>
public sealed class HubInstanceLock : IDisposable
{
    private readonly Semaphore _semaphore;

    private HubInstanceLock(Semaphore semaphore) => _semaphore = semaphore;

    public static string NameFor(int port) => $"Local\\SzDiag.Hub-{port}";

    /// <summary>null — на этом порту уже работает hub.</summary>
    public static HubInstanceLock? TryAcquire(int port)
    {
        var semaphore = new Semaphore(1, 1, NameFor(port));
        if (semaphore.WaitOne(0)) return new HubInstanceLock(semaphore);
        semaphore.Dispose();
        return null;
    }

    /// <summary>Порт первого адреса прослушивания (`http://0.0.0.0:5099`, `http://*:5099`, `http://+:5099`).</summary>
    public static int PortOf(IEnumerable<string> urls)
    {
        var first = urls.First().Replace("://*", "://localhost").Replace("://+", "://localhost");
        return new Uri(first).Port;
    }

    public void Dispose()
    {
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* уже отпущен */ }
        _semaphore.Dispose();
    }
}
