using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;
using SzDiag.Contracts;
using SzDiag.Hub;
using Xunit;

namespace SzDiag.Hub.Tests;

public class HubDiscoveryResponderTests
{
    private static async Task<(HubDiscoveryResponder responder, UdpClient sender)> StartAsync(
        int listenPort, string token, int hubPort)
    {
        var options = Options.Create(new HubOptions { AgentToken = token, Port = hubPort });
        var responder = new HubDiscoveryResponder(options, listenPort);
        await responder.StartAsync(CancellationToken.None);
        await Task.Delay(50); // дать сокету открыться
        var sender = new UdpClient(0);
        sender.Connect(IPAddress.Loopback, listenPort);
        return (responder, sender);
    }

    [Fact]
    public async Task ValidToken_RespondsWithHubPort()
    {
        var (responder, sender) = await StartAsync(15098, "dev-token", 5099);
        try
        {
            var request = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildRequest("dev-token"));
            await sender.SendAsync(request, request.Length);

            var receiveTask = sender.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(2000));
            Assert.Same(receiveTask, completed);

            var text = Encoding.UTF8.GetString((await receiveTask).Buffer);
            Assert.True(DiscoveryProtocol.TryParseResponse(text, out var port));
            Assert.Equal(5099, port);
        }
        finally
        {
            sender.Dispose();
            await responder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task WrongToken_DoesNotRespond()
    {
        var (responder, sender) = await StartAsync(15099, "dev-token", 5099);
        try
        {
            var request = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildRequest("wrong-token"));
            await sender.SendAsync(request, request.Length);

            var receiveTask = sender.ReceiveAsync();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(500));
            Assert.NotSame(receiveTask, completed);
        }
        finally
        {
            sender.Dispose();
            await responder.StopAsync(CancellationToken.None);
        }
    }
}
