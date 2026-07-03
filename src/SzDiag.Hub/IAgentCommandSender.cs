namespace SzDiag.Hub;

/// <summary>Отправка команд конкретному агенту по его connectionId.</summary>
public interface IAgentCommandSender
{
    Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default);
    Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default);
}
