using SzDiag.Agent;

namespace SzDiag.Agent.Tests;

/// <summary>Регрессия (бэклог п.201/п.212, СЗ 161211/161498): под Combined/PowerSupply ack
/// приёма exec не доходил вовсе — «команда не принята», а не «принята, но задавлена». Ack
/// отправляется с выделенного потока с максимальным приоритетом, а не через общий ThreadPool
/// (который под 100 % CPU на всех ядрах может быть исчерпан наравне со всем остальным).</summary>
public class HighPriorityAckDispatcherTests : IDisposable
{
    private readonly HighPriorityAckDispatcher _dispatcher = new();

    [Fact]
    public async Task Enqueue_RunsActionOnDedicatedThread()
    {
        var tcs = new TaskCompletionSource<int>();

        _dispatcher.Enqueue(() => tcs.TrySetResult(Environment.CurrentManagedThreadId));

        var ranOnThreadId = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(Environment.CurrentManagedThreadId, ranOnThreadId);
    }

    [Fact]
    public async Task Enqueue_ThreadHasHighestPriority()
    {
        var tcs = new TaskCompletionSource<ThreadPriority>();

        _dispatcher.Enqueue(() => tcs.TrySetResult(Thread.CurrentThread.Priority));

        var priority = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ThreadPriority.Highest, priority);
    }

    [Fact]
    public async Task Enqueue_ExceptionInActionDoesNotKillDispatcherThread()
    {
        _dispatcher.Enqueue(() => throw new InvalidOperationException("boom"));

        var tcs = new TaskCompletionSource<bool>();
        _dispatcher.Enqueue(() => tcs.TrySetResult(true));

        Assert.True(await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)),
            "поток должен продолжать обрабатывать очередь после упавшего действия");
    }

    [Fact]
    public async Task Enqueue_ProcessesMultipleActionsInOrder()
    {
        var results = new List<int>();
        var done = new TaskCompletionSource<bool>();

        for (var i = 0; i < 5; i++)
        {
            var n = i;
            _dispatcher.Enqueue(() =>
            {
                lock (results) results.Add(n);
                if (n == 4) done.TrySetResult(true);
            });
        }

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, results);
    }

    public void Dispose() => _dispatcher.Dispose();
}
