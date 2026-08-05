namespace SzDiag.Agent;

/// <summary>Не даёт запустить второй агент на одной машине.
///
/// Боль (СЗ 160306, бэклог п.52): рядом с работающим агентом стартовал второй, дрался за
/// <c>agent.log</c> и падал — но первый при этом тоже переставал слать heartbeat, и СЗ час
/// висела offline при живой машине. Два агента на одной машине не нужны никогда: доступ
/// открыт один раз, sshd один, watchdog один.
///
/// Мьютекс именованный и локальный для сессии (<c>Local\</c>): агент запускается и под
/// пользователем, и под SYSTEM из автостарт-задачи, поэтому сессии могут различаться —
/// используем <c>Global\</c>, чтобы конфликт ловился в любом сочетании.</summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string MutexName = @"Global\szdiag-agent";

    private readonly Mutex? _mutex;
    private readonly bool _owned;

    private SingleInstanceGuard(Mutex? mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>true — мы единственный агент; false — кто-то уже держит мьютекс.</summary>
    public bool IsPrimary => _owned;

    /// <summary>Пытается занять слот единственного агента. Если механизм мьютексов почему-то
    /// недоступен (экзотические права), считаем себя основным: отказ запускаться из-за
    /// сбойного guard'а хуже, чем сам конфликт.</summary>
    public static SingleInstanceGuard Acquire(string name = MutexName)
    {
        try
        {
            // Именно createdNew, а не WaitOne: мьютекс Windows реентерабелен для владельца,
            // и WaitOne из того же процесса/потока успешно взяло бы его второй раз. А если
            // прошлый агент умер — объект ядра уничтожается вместе с последним хендлом,
            // и createdNew снова true (зависший процесс, наоборот, слот держит — так и надо).
            var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
            return new SingleInstanceGuard(mutex, createdNew);
        }
        catch
        {
            return new SingleInstanceGuard(null, true);
        }
    }

    public void Dispose()
    {
        if (_mutex is null) return;
        try { if (_owned) _mutex.ReleaseMutex(); } catch { /* уже отпущен */ }
        _mutex.Dispose();
    }
}
