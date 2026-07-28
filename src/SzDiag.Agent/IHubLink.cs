namespace SzDiag.Agent;

/// <summary>Связь агента с hub. Реальная реализация — над SignalR-клиентом.</summary>
public interface IHubLink
{
    Task ConnectAsync(CancellationToken ct = default);
    Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null,
        CancellationToken ct = default);
    Task HeartbeatAsync(string sz, CancellationToken ct = default);

    /// <summary>Подписка на команду revert от hub (sz → callback).</summary>
    void OnRevert(Func<string, Task> handler);

    /// <summary>Подписка на команду прогона тестов от hub (sz, filter → callback).</summary>
    void OnRunTests(Func<string, string?, Task> handler);

    /// <summary>Подписка на команду диагностики от hub (sz, sections → callback).</summary>
    void OnRunDiag(Func<string, string?, Task> handler);
    void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler);
    Task SendExecResultAsync(SzDiag.Contracts.ExecResult result, CancellationToken ct = default);

    Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part, CancellationToken ct = default);

    /// <summary>Агент -> hub: текущая активность (метка + время старта; since=null — простой).</summary>
    Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default);

    ValueTask DisposeAsync();
}
