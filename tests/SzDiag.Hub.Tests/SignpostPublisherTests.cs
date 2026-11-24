using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SzDiag.Contracts;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;
using SzDiag.Hub;

namespace SzDiag.Hub.Tests;

public class SignpostPublisherTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "szpub-" + Guid.NewGuid().ToString("N"));
    private readonly (string Priv, string Pub) _keys = FleetKeys.Generate();
    private readonly Fake _net = new();
    private readonly HubStatusTracker _status = new(TimeProvider.System);

    public SignpostPublisherTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "fleet-sign.key"), _keys.Priv);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Fake : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();
        public Func<HttpResponseMessage> Reply = () => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return Reply();
        }
    }

    private SignpostPublisher Publisher(bool takeover = false, string keyPath = "fleet-sign.key")
    {
        var hub = new HubOptions
        {
            Signpost = new HubSignpostOptions
            {
                Enabled = true, Url = "https://signpost.test", HubName = "ПК", Takeover = takeover, SignKeyPath = keyPath,
            },
        };
        return new SignpostPublisher(hub, _status, NullLogger<SignpostPublisher>.Instance, new HttpClient(_net),
            TimeProvider.System, () => [new HubEndpoint(HubEndpointMode.Lan, "http://192.168.1.102:5099")], _dir);
    }

    private HubRecord Sent(int i)
    {
        using var v = new EnvelopeVerifier(_keys.Pub);
        Assert.Equal(EnvelopeFailure.None, v.Open<HubRecord>(SignedEnvelope.TryParse(_net.Bodies[i]), out var rec));
        return rec!;
    }

    [Fact]
    public async Task Publishes_signed_record_and_reports_published()
    {
        var p = Publisher();
        Assert.True(p.Init());
        await p.PublishOnceAsync(default);

        var rec = Sent(0);
        Assert.Equal("ПК", rec.HubName);
        Assert.Equal("http://192.168.1.102:5099", rec.Endpoints[0].Url);
        Assert.Equal(SignpostStates.Published, _status.SignpostSnapshot().State);
        Assert.NotNull(_status.SignpostSnapshot().LastPublishedAt);
    }

    [Fact]
    public async Task Seq_grows_and_hub_id_is_stable_across_restarts()
    {
        var p = Publisher();
        p.Init();
        await p.PublishOnceAsync(default);
        await p.PublishOnceAsync(default);
        Assert.True(Sent(1).Seq > Sent(0).Seq);

        var again = Publisher();
        again.Init();
        await again.PublishOnceAsync(default);
        Assert.Equal(Sent(0).HubId, Sent(2).HubId);
    }

    [Fact]
    public async Task Conflict_reports_holder_and_keeps_trying()
    {
        var lease = new LeaseInfo("laptop-1", "ноут", DateTimeOffset.UtcNow.AddSeconds(60));
        _net.Reply = () => new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(lease, SignpostJson.Options), Encoding.UTF8, "application/json"),
        };
        var p = Publisher();
        p.Init();

        await p.PublishOnceAsync(default);

        var s = _status.SignpostSnapshot();
        Assert.Equal(SignpostStates.OtherHolder, s.State);
        Assert.Equal("ноут", s.Holder!.HubName);
        Assert.Contains("--takeover", s.Message);
        Assert.False(Sent(0).Takeover);
    }

    [Fact]
    public async Task Takeover_is_sent_once_then_normal_renewal()
    {
        var p = Publisher(takeover: true);
        p.Init();
        await p.PublishOnceAsync(default);
        await p.PublishOnceAsync(default);

        Assert.True(Sent(0).Takeover);
        Assert.False(Sent(1).Takeover);
    }

    [Fact]
    public async Task Network_failure_is_unreachable_not_crash()
    {
        _net.Reply = () => throw new HttpRequestException("нет сети");
        var p = Publisher();
        p.Init();
        await p.PublishOnceAsync(default);
        Assert.Equal(SignpostStates.Unreachable, _status.SignpostSnapshot().State);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Signature_problems_are_misconfigured(HttpStatusCode code)
    {
        _net.Reply = () => new HttpResponseMessage(code);
        var p = Publisher();
        p.Init();
        await p.PublishOnceAsync(default);
        Assert.Equal(SignpostStates.Misconfigured, _status.SignpostSnapshot().State);
    }

    [Fact]
    public void Missing_key_is_misconfigured_and_nothing_sent()
    {
        var p = Publisher(keyPath: "нет-такого.key");
        Assert.False(p.Init());
        Assert.Equal(SignpostStates.Misconfigured, _status.SignpostSnapshot().State);
        Assert.Empty(_net.Bodies);
    }

    [Fact]
    public void Extra_endpoints_are_appended_as_tunnel_by_default()
    {
        var hub = new HubOptions { Port = 5099 };
        hub.Signpost.ExtraEndpoints.Add(new HubSignpostEndpoint { Url = "https://hub.example" });

        var eps = SignpostPublisher.DefaultEndpoints(hub);

        Assert.Contains(eps, e => e is { Mode: HubEndpointMode.Tunnel, Url: "https://hub.example" });
        Assert.All(eps.Where(e => e.Mode == HubEndpointMode.Lan), e => Assert.EndsWith(":5099", e.Url));
    }
}
