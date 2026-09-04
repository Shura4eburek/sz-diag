using System.Collections.Concurrent;

namespace SzDiag.Agent;

/// <summary>Выделенный поток с максимальным приоритетом ОС для отправки ack — в обход общего
/// ThreadPool.
///
/// Боль (бэклог п.201/п.212, СЗ 161211/161498): под OCCT Combined/PowerSupply ack приёма
/// exec-команды не доходил ВООБЩЕ — «команда не принята», а не обещанное «принята, но
/// задавлена» (п.43). `await link.SendExecAckAsync(...)` инлайн в обработчике SignalR всё
/// равно возобновляется на ThreadPool-потоке после `await`, а под 100% CPU на всех ядрах пул
/// может быть исчерпан наравне со всем остальным. Собственный `Thread` с
/// `ThreadPriority.Highest` получает квант ОС раньше пула — то же самое рассуждение, что и
/// для приоритета всего процесса агента (см. `Program.cs`), но на уровне одного потока для
/// самой критичной операции.</summary>
public sealed class HighPriorityAckDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public HighPriorityAckDispatcher()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "szdiag-ack",
        };
        try { _thread.Priority = ThreadPriority.Highest; }
        catch { /* нет прав/платформа не поддерживает — очередь всё равно работает */ }
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var action in _queue.GetConsumingEnumerable())
        {
            // Одно упавшее действие не должно убивать весь выделенный поток — иначе
            // следующий ack повис бы молча (ровно та цена, ради которой поток и заведён).
            try { action(); } catch { }
        }
    }

    /// <summary>Поставить действие в очередь на выполнение выделенным потоком. Синхронный
    /// (не Task): вызывающий не должен ждать — весь смысл в том, чтобы не конкурировать за
    /// ThreadPool.</summary>
    public void Enqueue(Action action) => _queue.Add(action);

    public void Dispose() => _queue.CompleteAdding();
}
