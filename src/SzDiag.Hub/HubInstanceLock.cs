namespace SzDiag.Hub;

/// <summary>Один hub на порт. Второй экземпляр (Desk с чужим адресом запускал start-hub.cmd при
/// живом hub, 19.10) успевал до падения на занятом порту поднять службы — и `HubTunnelService`
/// по pid-файлу убивал cloudflared живого hub. Захват — до построения хоста, поэтому второй
/// экземпляр выходит, ничего не трогая.
/// Именованный семафор, а не мьютекс: не привязан к потоку и не бывает «брошенным» — убили hub,
/// закрылся последний дескриптор, объект исчез.
/// Вне Windows именованных семафоров в .NET нет (PlatformNotSupportedException на старте) — там
/// эксклюзивно открытый файл: блокировку так же снимает ядро, когда процесс умирает.</summary>
public sealed class HubInstanceLock : IDisposable
{
    private readonly Semaphore? _semaphore;
    private readonly FileStream? _file;

    private HubInstanceLock(Semaphore semaphore) => _semaphore = semaphore;
    private HubInstanceLock(FileStream file) => _file = file;

    public static string NameFor(int port) => $"Local\\SzDiag.Hub-{port}";

    /// <summary>null — на этом порту уже работает hub.</summary>
    public static HubInstanceLock? TryAcquire(int port)
    {
        if (!OperatingSystem.IsWindows()) return TryAcquireFile(port);

        var semaphore = new Semaphore(1, 1, NameFor(port));
        if (semaphore.WaitOne(0)) return new HubInstanceLock(semaphore);
        semaphore.Dispose();
        return null;
    }

    private static HubInstanceLock? TryAcquireFile(int port)
    {
        var path = Path.Combine(Path.GetTempPath(), $"SzDiag.Hub-{port}.lock");
        try
        {
            return new HubInstanceLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Порт первого адреса прослушивания (`http://0.0.0.0:5099`, `http://*:5099`, `http://+:5099`).</summary>
    public static int PortOf(IEnumerable<string> urls)
    {
        var first = urls.First().Replace("://*", "://localhost").Replace("://+", "://localhost");
        return new Uri(first).Port;
    }

    public void Dispose()
    {
        _file?.Dispose();
        if (_semaphore is null) return;
        try { _semaphore.Release(); }
        catch (SemaphoreFullException) { /* уже отпущен */ }
        _semaphore.Dispose();
    }
}
