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
}
