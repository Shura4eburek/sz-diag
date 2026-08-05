using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Request/response поверх SignalR: отправляет агенту <see cref="ExecRequest"/> и ждёт
/// его <see cref="ExecResult"/>, сопоставляя по RequestId. Нужен потому, что SignalR-команды
/// сами по себе fire-and-forget (как RunDiag), а exec обязан вернуть вывод вызвавшему CLI.</summary>
public sealed class ExecCoordinator
{
    private readonly SessionRegistry _registry;
    private readonly IAgentCommandSender _sender;
    private readonly int _graceSeconds;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExecResult>> _pending = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _acked = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExecJobStatus>> _statusPending = new();

    /// <param name="graceSeconds">Запас поверх таймаута скрипта на доставку ответа.
    /// Отдельным параметром — чтобы тесты не ждали реальных секунд.</param>
    public ExecCoordinator(SessionRegistry registry, IAgentCommandSender sender,
        int graceSeconds = ExecLimits.HubGraceSeconds)
    {
        _registry = registry;
        _sender = sender;
        _graceSeconds = graceSeconds;
    }

    /// <summary>Сколько запросов сейчас ждут ответа (для тестов/диагностики).</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Выполнить скрипт на агенте СЗ. Возвращает null, если СЗ не онлайн.</summary>
    /// <exception cref="TimeoutException">Агент не ответил в отведённое время.</exception>
    public async Task<ExecResult?> RunAsync(string sz, string script, int? timeoutSeconds = null,
        CancellationToken ct = default, bool detached = false)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return null;

        var timeout = timeoutSeconds ?? ExecLimits.DefaultTimeoutSeconds;
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ExecResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;
        try
        {
            await _sender.SendExecAsync(connId, new ExecRequest(sz, requestId, script, timeout, detached), ct);

            // Ждём дольше, чем сам скрипт: агенту нужно время убить процесс и доставить ответ.
            var wait = TimeSpan.FromSeconds(timeout + _graceSeconds);
            var delay = Task.Delay(wait, ct);
            var done = await Task.WhenAny(tcs.Task, delay);
            if (done != tcs.Task)
            {
                // Два совершенно разных диагноза: команда до агента не доехала (канал/сеть)
                // против «принял и не успел» (задавлен нагрузкой). Раньше оба выглядели
                // одинаковым глухим «агент не ответил» (бэклог п.35/п.43).
                var hint = _acked.TryGetValue(requestId, out var acceptedAt)
                    ? $"агент СЗ {sz} ПРИНЯЛ команду {(DateTimeOffset.UtcNow - acceptedAt).TotalSeconds:N0} с назад, " +
                      "но не вернул результат — вероятно, задавлен нагрузкой; попробуй --detach"
                    : $"агент СЗ {sz} не принял команду за {wait.TotalSeconds:N0} с " +
                      "(heartbeat при этом может идти — он отдельным лёгким путём)";
                throw new TimeoutException(hint);
            }
            return await tcs.Task;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            _acked.TryRemove(requestId, out _);
        }
    }

    /// <summary>Состояние фоновой задачи на агенте: короткий запрос, проходит и под нагрузкой.</summary>
    public async Task<ExecJobStatus?> StatusAsync(string sz, string jobId, int tailLines,
        CancellationToken ct = default)
    {
        var connId = _registry.TryGetConnectionId(sz);
        if (connId is null) return null;

        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ExecJobStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _statusPending[requestId] = tcs;
        try
        {
            await _sender.SendExecStatusAsync(connId,
                new ExecStatusRequest(sz, requestId, jobId, tailLines), ct);

            var wait = TimeSpan.FromSeconds(ExecLimits.AckSeconds + _graceSeconds);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(wait, ct));
            if (done != tcs.Task)
                throw new TimeoutException($"агент СЗ {sz} не вернул статус задачи за {wait.TotalSeconds:N0} с");
            return await tcs.Task;
        }
        finally { _statusPending.TryRemove(requestId, out _); }
    }

    /// <summary>Агент подтвердил приём команды (ack приходит до запуска скрипта).</summary>
    public bool Acknowledge(ExecAck ack)
    {
        if (!_pending.ContainsKey(ack.RequestId)) return false;
        _acked[ack.RequestId] = ack.AcceptedAt;
        return true;
    }

    /// <summary>Агент прислал состояние фоновой задачи.</summary>
    public bool CompleteStatus(ExecJobStatus status)
        => _statusPending.TryGetValue(status.RequestId, out var tcs) && tcs.TrySetResult(status);

    /// <summary>Агент прислал результат — разбудить ожидающий запрос. Ответ на неизвестный
    /// RequestId (например, запрос уже истёк) тихо игнорируется.</summary>
    public bool Complete(ExecResult result)
    {
        if (!_pending.TryGetValue(result.RequestId, out var tcs)) return false;
        return tcs.TrySetResult(result);
    }
}
