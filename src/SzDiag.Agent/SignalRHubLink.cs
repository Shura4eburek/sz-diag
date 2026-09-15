using Microsoft.AspNetCore.SignalR.Client;
using SzDiag.Contracts;

namespace SzDiag.Agent;

public sealed class SignalRHubLink : IHubLink
{
    private readonly HubConnection _conn;

    /// <param name="accessClientId">Service token приложения Cloudflare Access перед hub
    /// (пара заголовков CF-Access-*). Пусто — Access не используется: hub в локальной сети.
    /// Это секрет доступа к НАШЕМУ hub, общий для всех агентов, — ровно то же, чем уже
    /// является <paramref name="token"/>, так что модель угроз не меняется.</param>
    public SignalRHubLink(string hubUrl, string token,
        string? accessClientId = null, string? accessClientSecret = null)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{hubUrl.TrimEnd('/')}{HubRoutes.Path}", o =>
            {
                o.Headers[HubRoutes.TokenHeader] = token;
                if (!string.IsNullOrWhiteSpace(accessClientId))
                {
                    o.Headers["CF-Access-Client-Id"] = accessClientId;
                    o.Headers["CF-Access-Client-Secret"] = accessClientSecret ?? "";
                }
            })
            .WithAutomaticReconnect()
            .Build();
    }

    public Task ConnectAsync(CancellationToken ct = default) => _conn.StartAsync(ct);

    public async Task<string?> RegisterAsync(string sz, string hostname, DateTimeOffset? bootTime = null,
        string? lastShutdown = null, string? agentUser = null, int? agentSessionId = null,
        CancellationToken ct = default)
    {
        var response = await _conn.InvokeAsync<RegisterResponse?>(HubRoutes.Register,
            new RegisterRequest(sz, hostname, bootTime, lastShutdown, agentUser, agentSessionId), ct);
        return response?.SessionSecret;
    }

    public Task HeartbeatAsync(string sz, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.Heartbeat, sz, ct);

    public Task ReportAccessAsync(AccessReportRequest report, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.ReportAccess, report, ct);

    public Task ReportPowerEventsAsync(PowerEventsReport report, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.PowerEvents, report, ct);

    public void OnReconnected(Func<Task> handler)
        => _conn.Reconnected += _ => handler();

    public void OnRevert(Func<string, Task> handler)
        => _conn.On<string>(HubRoutes.Revert, sz => handler(sz));

    public void OnRunTests(Func<string, string?, string?, Task> handler)
        => _conn.On<string, string?, string?>(HubRoutes.RunTests, (sz, filter, schedule) => handler(sz, filter, schedule));

    public void OnRunDiag(Func<string, string?, Task> handler)
        => _conn.On<string, string?>(HubRoutes.RunDiag, (sz, sections) => handler(sz, sections));

    public void OnExec(Func<ExecRequest, Task> handler)
        => _conn.On<ExecRequest>(HubRoutes.Exec, req => handler(req));

    public Task SendExecResultAsync(ExecResult result, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.ExecResult, result, ct);

    public Task SendExecAckAsync(ExecAck ack, CancellationToken ct = default)
        => _conn.SendAsync(HubRoutes.ExecAck, ack, ct);   // Send, а не Invoke: ack не должен ждать hub

    public void OnExecStatus(Func<ExecStatusRequest, Task> handler)
        => _conn.On<ExecStatusRequest>(HubRoutes.ExecStatus, req => handler(req));

    public Task SendExecJobStatusAsync(ExecJobStatus status, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.ExecJobStatus, status, ct);

    public void OnPush(Func<PushRequest, Task> handler)
        => _conn.On<PushRequest>(HubRoutes.Push, req => handler(req));

    public Task SendPushResultAsync(PushResult result, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.PushResult, result, ct);

    public void OnPull(Func<PullRequest, Task> handler)
        => _conn.On<PullRequest>(HubRoutes.Pull, req => handler(req));

    public Task SendPullAckAsync(PullAck ack, CancellationToken ct = default)
        => _conn.SendAsync(HubRoutes.PullAck, ack, ct);   // Send, а не Invoke: ack не должен ждать hub

    // InvokeAsync (а не SendAsync): чанки обязаны дойти по порядку и с подтверждением —
    // потерянный кусок означал бы битый файл на хосте.
    public Task SendPullChunkAsync(PullChunk chunk, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.PullChunk, chunk, ct);

    public Task SendPullResultAsync(PullResult result, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.PullResult, result, ct);

    public Task UploadReportFileAsync(UploadReportPart part, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.UploadReportFile, part, ct);

    public Task ReportActivityAsync(string sz, string activity, DateTimeOffset? since, CancellationToken ct = default)
        => _conn.SendAsync(HubRoutes.ReportActivity, sz, activity, since, ct);

    // InvokeAsync (а не SendAsync): без подтверждения агент мог бы уйти в DisposeAsync
    // раньше, чем сообщение реально ушло по сети — итог отката потерялся бы молча.
    public Task SendRevertResultAsync(RevertResult result, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.RevertResult, result, ct);

    public void OnRestartAgent(Action<string> handler)
        => _conn.On<string>(HubRoutes.RestartAgent, sz => handler(sz));

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
