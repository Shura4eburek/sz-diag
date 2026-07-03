using System.Net;
using System.Net.Sockets;
using System.Text;
using SzDiag.Agent;
using SzDiag.Contracts;
using Xunit;

namespace SzDiag.Agent.Tests;

public class HubDiscoveryTests
{
    [Fact]
    public async Task FindHubAsync_RespondingHub_ReturnsUrl()
    {
        using var fakeHub = new UdpClient(new IPEndPoint(IPAddress.Loopback, 15100));
        var listenTask = Task.Run(async () =>
        {
            var result = await fakeHub.ReceiveAsync();
            var text = Encoding.UTF8.GetString(result.Buffer);
            Assert.True(DiscoveryProtocol.TryParseRequest(text, out var token));
            Assert.Equal("dev-token", token);

            var response = Encoding.UTF8.GetBytes(DiscoveryProtocol.BuildResponse(5099));
            await fakeHub.SendAsync(response, result.RemoteEndPoint);
        });

        var url = await HubDiscovery.FindHubAsync("dev-token",
            new[] { IPAddress.Loopback }, 15100, TimeSpan.FromSeconds(2));

        await listenTask;
        Assert.Equal("http://127.0.0.1:5099", url);
    }

    [Fact]
    public async Task FindHubAsync_NoResponse_ThrowsHubNotFoundException()
    {
        await Assert.ThrowsAsync<HubNotFoundException>(() =>
            HubDiscovery.FindHubAsync("dev-token", new[] { IPAddress.Loopback }, 15101, TimeSpan.FromMilliseconds(300)));
    }
}
