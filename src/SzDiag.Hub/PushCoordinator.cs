using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Доставка инструмента на клиента: hub шлёт агенту команду, агент **сам качает**
/// файлы по HTTP и отчитывается итогом. Hub здесь только диспетчер — байты идут мимо него
/// обычным HTTP-скачиванием, поэтому 300-мегабайтный OCCT не проходит через SignalR.</summary>
public sealed class PushCoordinator
{
    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;
    private readonly int _timeoutSeconds;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<PushResult>> _pending = new();

    public PushCoordinator(SessionRegistry registry, IAgentCommandSender sender,
        int timeoutSeconds = PushLimits.TimeoutSeconds)
    {
        _registry = registry;
        _sender = sender;
        _timeoutSeconds = timeoutSeconds;
    }

    public int PendingCount => _pending.Count;

    /// <summary>Доставить инструмент на клиента. null — СЗ не онлайн.</summary>
    /// <exception cref="TimeoutException">Агент не отчитался в отведённое время.</exception>
    public async Task<PushResult?> PushAsync(string sz, string tool, CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return null;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<PushResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        try
        {
            await _sender.SendPushAsync(connId, new PushRequest(sz, requestId, tool), ct);

            var wait = TimeSpan.FromSeconds(_timeoutSeconds);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(wait, ct));
            if (done != tcs.Task)
                throw new TimeoutException(
                    $"агент СЗ {sz} не завершил доставку '{tool}' за {wait.TotalSeconds:N0} с");
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>Агент отчитался — будим ожидающий запрос. Ответ на неизвестный RequestId
    /// (запрос уже истёк) игнорируется.</summary>
    public bool Complete(PushResult result)
        => _pending.TryGetValue(result.RequestId, out var tcs) && tcs.TrySetResult(result);
}
