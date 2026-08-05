using Microsoft.AspNetCore.SignalR;
using SzDiag.Contracts;

namespace SzDiag.Hub;

public sealed class SignalRAgentCommandSender : IAgentCommandSender
{
    private readonly IHubContext<AgentHub> _hub;

    public SignalRAgentCommandSender(IHubContext<AgentHub> hub) => _hub = hub;

    public Task SendRevertAsync(string connectionId, string sz, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.Revert, sz, ct);

    public Task SendRunTestsAsync(string connectionId, string sz, string? filter, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.RunTests, sz, filter, ct);

    public Task SendRunDiagAsync(string connectionId, string sz, string? sections, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.RunDiag, sz, sections, ct);

    public Task SendExecAsync(string connectionId, ExecRequest request, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.Exec, request, ct);

    public Task SendExecStatusAsync(string connectionId, ExecStatusRequest request, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.ExecStatus, request, ct);

    public Task SendPullAsync(string connectionId, PullRequest request, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.Pull, request, ct);

    public Task SendPushAsync(string connectionId, PushRequest request, CancellationToken ct = default)
        => _hub.Clients.Client(connectionId).SendAsync(HubRoutes.Push, request, ct);
}
