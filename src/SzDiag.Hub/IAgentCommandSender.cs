using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Отправка команд конкретному агенту по его connectionId.</summary>
public interface IAgentCommandSender
{
    Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default);
    Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default);
    Task SendRunDiagAsync(string connectionId, string sz, string? sections, CancellationToken ct = default);
    Task SendExecAsync(string connectionId, ExecRequest request, CancellationToken ct = default);

    /// <summary>Забрать файл(ы) с клиента: агент отвечает потоком чанков + итогом.</summary>
    Task SendPullAsync(string connectionId, PullRequest request, CancellationToken ct = default);

    /// <summary>Доставить инструмент на клиента: агент сам качает его с hub по HTTP.</summary>
    Task SendPushAsync(string connectionId, PushRequest request, CancellationToken ct = default);
}
