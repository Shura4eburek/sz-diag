using Microsoft.AspNetCore.SignalR.Client;
using SzDiag.Contracts;

namespace SzDiag.Agent;

public sealed class SignalRHubLink : IHubLink
{
    private readonly HubConnection _conn;

    public SignalRHubLink(string hubUrl, string token)
    {
        _conn = new HubConnectionBuilder()
            .WithUrl($"{hubUrl.TrimEnd('/')}{HubRoutes.Path}", o =>
                o.Headers[HubRoutes.TokenHeader] = token)
            .WithAutomaticReconnect()
            .Build();
    }

    public Task ConnectAsync(CancellationToken ct = default) => _conn.StartAsync(ct);

    public Task RegisterAsync(string sz, string hostname, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.Register, new RegisterRequest(sz, hostname), ct);

    public Task HeartbeatAsync(string sz, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.Heartbeat, sz, ct);

    public void OnRevert(Func<string, Task> handler)
        => _conn.On<string>(HubRoutes.Revert, sz => handler(sz));

    public void OnRunTests(Func<string, Task> handler)
        => _conn.On<string>(HubRoutes.RunTests, sz => handler(sz));

    public Task UploadReportFileAsync(UploadReportPart part, CancellationToken ct = default)
        => _conn.InvokeAsync(HubRoutes.UploadReportFile, part, ct);

    public ValueTask DisposeAsync() => _conn.DisposeAsync();
}
