using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Отправка команд конкретному агенту по его connectionId.</summary>
public interface IAgentCommandSender
{
    Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default);
    Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default);
    Task SendRunDiagAsync(string connectionId, string sz, string? sections, CancellationToken ct = default);
    Task SendExecAsync(string connectionId, ExecRequest request, CancellationToken ct = default);

    /// <summary>Спросить состояние фоновой exec-задачи.</summary>
    Task SendExecStatusAsync(string connectionId, ExecStatusRequest request, CancellationToken ct = default);

    /// <summary>Забрать файл(ы) с клиента: агент отвечает потоком чанков + итогом.</summary>
    Task SendPullAsync(string connectionId, PullRequest request, CancellationToken ct = default);

    /// <summary>Доставить инструмент на клиента: агент сам качает его с hub по HTTP.</summary>
    Task SendPushAsync(string connectionId, PushRequest request, CancellationToken ct = default);

    /// <summary>Перезапустить агента — отдельным путём от exec (бэклог п.202/п.215):
    /// `agent restart` раньше ходил через тот же ack/очередь, что и обычный exec, и потому
    /// был бесполезен ровно тогда, когда нужен (канал забит). Fire-and-forget, как Revert —
    /// подтверждения ждать нечем, агент себя не убивает сам (см. NativeAgentRestart).</summary>
    Task SendRestartAgentAsync(string connectionId, string sz, CancellationToken ct = default);
}
