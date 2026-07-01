namespace SzDiag.Agent;

/// <summary>Связь агента с hub. Реальная реализация — над SignalR-клиентом.</summary>
public interface IHubLink
{
    Task ConnectAsync(CancellationToken ct = default);
    Task RegisterAsync(string sz, string hostname, CancellationToken ct = default);
    Task HeartbeatAsync(string sz, CancellationToken ct = default);

    /// <summary>Подписка на команду revert от hub (sz → callback).</summary>
    void OnRevert(Func<string, Task> handler);

    ValueTask DisposeAsync();
}
