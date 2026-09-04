namespace SzDiag.Agent;

/// <summary>Связь агента с hub. Реальная реализация — над SignalR-клиентом.</summary>
public interface IHubLink
{
    Task ConnectAsync(CancellationToken ct = default);
    Task RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null, string? lastShutdown = null,
        CancellationToken ct = default);
    Task HeartbeatAsync(string sz, CancellationToken ct = default);

    /// <summary>Отдать hub события питания из журнала клиента — то, что hub сам увидеть не
    /// может (бэклог п.97).</summary>
    Task ReportPowerEventsAsync(SzDiag.Contracts.PowerEventsReport report, CancellationToken ct = default);

    /// <summary>Подписка на команду revert от hub (sz → callback).</summary>
    void OnRevert(Func<string, Task> handler);

    /// <summary>Подписка на команду прогона тестов от hub (sz, filter → callback).</summary>
    void OnRunTests(Func<string, string?, Task> handler);

    /// <summary>Подписка на команду диагностики от hub (sz, sections → callback).</summary>
    void OnRunDiag(Func<string, string?, Task> handler);
    void OnExec(Func<SzDiag.Contracts.ExecRequest, Task> handler);
    Task SendExecResultAsync(SzDiag.Contracts.ExecResult result, CancellationToken ct = default);

    /// <summary>Подтверждение приёма команды — до запуска скрипта.</summary>
    Task SendExecAckAsync(SzDiag.Contracts.ExecAck ack, CancellationToken ct = default);

    /// <summary>Подписка на запрос состояния фоновой задачи.</summary>
    void OnExecStatus(Func<SzDiag.Contracts.ExecStatusRequest, Task> handler);
    Task SendExecJobStatusAsync(SzDiag.Contracts.ExecJobStatus status, CancellationToken ct = default);

    /// <summary>Подписка на команду доставки инструмента (агент качает его с hub сам).</summary>
    void OnPush(Func<SzDiag.Contracts.PushRequest, Task> handler);
    Task SendPushResultAsync(SzDiag.Contracts.PushResult result, CancellationToken ct = default);

    /// <summary>Подписка на команду забора файлов с клиента.</summary>
    void OnPull(Func<SzDiag.Contracts.PullRequest, Task> handler);

    /// <summary>Подтверждение приёма команды забора — до поиска файлов на диске (как у exec).</summary>
    Task SendPullAckAsync(SzDiag.Contracts.PullAck ack, CancellationToken ct = default);
    Task SendPullChunkAsync(SzDiag.Contracts.PullChunk chunk, CancellationToken ct = default);
    Task SendPullResultAsync(SzDiag.Contracts.PullResult result, CancellationToken ct = default);

    Task UploadReportFileAsync(SzDiag.Contracts.UploadReportPart part, CancellationToken ct = default);

    /// <summary>Агент -> hub: текущая активность (метка + время старта; since=null — простой).</summary>
    Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default);

    /// <summary>Агент -> hub: итог отката, ДО отключения канала — иначе close по офлайн-СЗ
    /// не может подтвердить полноту отката иначе как походом к машине (бэклог п.119).</summary>
    Task SendRevertResultAsync(SzDiag.Contracts.RevertResult result, CancellationToken ct = default);

    /// <summary>Подписка на команду перезапуска — отдельным путём от Exec (бэклог п.202/п.215):
    /// сама SignalR-подписка не завязана на exec-очередь/ack, поэтому доходит даже когда
    /// обычный exec задавлен.</summary>
    void OnRestartAgent(Action<string> handler);

    ValueTask DisposeAsync();
}
