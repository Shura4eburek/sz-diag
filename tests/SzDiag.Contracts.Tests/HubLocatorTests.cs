using System.Net;
using System.Text;
using System.Text.Json;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Contracts.Tests;

public class HubLocatorTests : IDisposable
{
    private const string Signpost = "https://signpost.test";
    private static readonly (string Priv, string Pub) Keys = FleetKeys.Generate();
    private readonly EnvelopeSigner _signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
    private readonly string _seenPath = Path.Combine(Path.GetTempPath(), "szseen-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly Routes _routes = new();

    public void Dispose()
    {
        _signer.Dispose();
        File.Delete(_seenPath);
    }

    /// <summary>Фейковая сеть: url → ответ; всё прочее — обрыв соединения.</summary>
    private sealed class Routes : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Map = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Map.TryGetValue(request.RequestUri!.ToString(), out var f)
                ? Task.FromResult(f())
                : throw new HttpRequestException("соединение отвергнуто");

        public void Json(string url, string body) =>
            Map[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private HubLocator Locator(string? configUrl = null, string? lan = null, string[]? keys = null,
        IReadOnlyCollection<string>? modes = null, HashSet<string>? alive = null)
        => new(new LocatorOptions(configUrl, Signpost, keys ?? [Keys.Pub], modes ?? LocatorOptions.DefaultModes),
            new HttpClient(_routes), new SeenSeqStore(_seenPath),
            _ => Task.FromResult(lan),
            (url, _) => Task.FromResult(alive?.Contains(url) ?? true));

    private SignedEnvelope HubEnv(long seq, params HubEndpoint[] eps)
        => _signer.SignRecord(new HubRecord("pc", "ПК",
            eps.Length > 0 ? eps : [new HubEndpoint(HubEndpointMode.Lan, "http://192.168.1.102:5099")],
            null, seq, DateTimeOffset.UtcNow));

    private void ServeSignpost(SignedEnvelope? hub, SignedEnvelope? release = null)
        => _routes.Json($"{Signpost}/signpost", JsonSerializer.Serialize(new SignpostSnapshot(hub, release, null), SignpostJson.Options));

    [Fact]
    public async Task Live_config_url_wins_over_everything()
    {
        var loc = await Locator(configUrl: "https://hub.example", lan: "http://lan:5099").LocateAsync();
        Assert.Equal(("https://hub.example", HubLocator.ModeConfig), (loc!.Url, loc.Mode));
    }

    [Fact]
    public async Task Dead_config_url_falls_through_to_signpost()
    {
        ServeSignpost(HubEnv(1));
        var loc = await Locator(configUrl: "https://dead.example", alive: ["http://192.168.1.102:5099"]).LocateAsync();

        Assert.Equal(("http://192.168.1.102:5099", "signpost"), (loc!.Url, loc.Source));
        Assert.Contains(loc.Rejected, r => r.Source == "config" && r.Reason.Contains("dead.example"));
    }

    [Fact]
    public async Task Lan_broadcast_goes_before_signpost()
    {
        ServeSignpost(HubEnv(1));
        var loc = await Locator(lan: "http://lan:5099").LocateAsync();
        Assert.Equal(("http://lan:5099", "lan"), (loc!.Url, loc.Source));
    }

    [Fact]
    public async Task Signpost_used_when_lan_silent_and_rejections_logged()
    {
        ServeSignpost(HubEnv(1));
        var loc = await Locator().LocateAsync();

        Assert.Equal("http://192.168.1.102:5099", loc!.Url);
        Assert.Equal("signpost", loc.Source);
        Assert.Contains(loc.Rejected, r => r.Source == "lan");
    }

    [Fact]
    public async Task Tampered_signpost_is_rejected_with_reason()
    {
        var good = HubEnv(1);
        var sig = Convert.FromBase64String(good.Sig);
        sig[0] ^= 1;
        ServeSignpost(good with { Sig = Convert.ToBase64String(sig) });
        var locator = Locator();

        Assert.Null(await locator.LocateAsync());
        Assert.Contains(locator.LastRejected, r => r.Source == "signpost" && r.Reason.Contains("подмен"));
    }

    [Fact]
    public async Task Unreachable_signpost_gives_null_with_reason()
    {
        var locator = Locator();
        Assert.Null(await locator.LocateAsync());
        Assert.Contains(locator.LastRejected, r => r.Source == "signpost");
        Assert.Contains(locator.LastRejected, r => r.Source == "lan");
    }

    [Fact]
    public async Task Foreign_key_is_rejected_as_unknown_kid()
    {
        using var foreign = EnvelopeSigner.FromPrivatePem(FleetKeys.Generate().PrivatePem);
        ServeSignpost(foreign.SignRecord(new HubRecord("evil", "x", [new HubEndpoint("lan", "http://evil")], null, 9, default)));
        var locator = Locator();
        Assert.Null(await locator.LocateAsync());
    }

    [Fact]
    public async Task Older_seq_than_seen_is_rejected_as_replay()
    {
        new SeenSeqStore(_seenPath).Remember(SignpostKinds.Hub, 100);
        ServeSignpost(HubEnv(50));

        var locator = Locator();
        Assert.Null(await locator.LocateAsync());
    }

    [Fact]
    public async Task Accepted_seq_is_remembered()
    {
        ServeSignpost(HubEnv(77));
        await Locator().LocateAsync();
        Assert.Equal(77, new SeenSeqStore(_seenPath).Get(SignpostKinds.Hub));
    }

    [Fact]
    public async Task Unsupported_modes_and_dead_endpoints_are_skipped()
    {
        ServeSignpost(HubEnv(1,
            new HubEndpoint(HubEndpointMode.Zt, "http://10.88.0.1:5099"),
            new HubEndpoint(HubEndpointMode.Lan, "http://192.168.1.102:5099"),
            new HubEndpoint(HubEndpointMode.Tunnel, "https://hub.example")));

        var loc = await Locator(alive: ["https://hub.example"]).LocateAsync();

        Assert.Equal(("https://hub.example", HubEndpointMode.Tunnel), (loc!.Url, loc.Mode));
        Assert.Contains(loc.Rejected, r => r.Source == "signpost/zt");
        Assert.Contains(loc.Rejected, r => r.Source == "signpost/lan" && r.Reason.Contains("не отвечает"));
    }

    [Fact]
    public async Task Without_public_key_signpost_is_not_trusted()
    {
        ServeSignpost(HubEnv(1));
        Assert.Null(await Locator(keys: []).LocateAsync());
    }

    [Fact]
    public async Task Release_resolves_relative_package_url_against_source()
    {
        var sha = new string('a', 64);
        var rel = _signer.SignRecord(new ReleaseRecord("v1", DateTimeOffset.UtcNow, sha, 10,
            [SignpostRoutes.PackageFor(sha), "https://cdn.test/package.zip"], 3));
        ServeSignpost(null, rel);

        var loc = await Locator().LocateReleaseAsync();

        Assert.Equal("v1", loc!.Release.Version);
        Assert.Equal([$"{Signpost}/package/{sha}", "https://cdn.test/package.zip"], loc.PackageUrls);
    }

    [Fact]
    public async Task Release_with_bad_sha_is_rejected()
    {
        ServeSignpost(null, _signer.SignRecord(new ReleaseRecord("v1", DateTimeOffset.UtcNow, "nothex", 1, ["/p"], 1)));
        Assert.Null(await Locator().LocateReleaseAsync());
    }

    [Fact]
    public async Task Hub_envelope_cannot_pose_as_release()
    {
        ServeSignpost(null, HubEnv(1));
        Assert.Null(await Locator().LocateReleaseAsync());
    }

    // --- zt-адрес hub (US3): проверяется только через ноду процесса, снаружи Contracts.

    private const string Nw = "aa4bb3b8ba8f7d86";

    private SignedEnvelope ZtHubEnv(long seq, string? network = Nw)
        => _signer.SignRecord(new HubRecord("pc", "ПК",
            [new HubEndpoint(HubEndpointMode.Zt, "http://10.88.0.1:5099"), new HubEndpoint(HubEndpointMode.Lan, "http://192.168.1.102:5099")],
            network, seq, DateTimeOffset.UtcNow));

    private HubLocator ZtLocator(Func<string, string, CancellationToken, Task<bool>>? ztProbe, HashSet<string>? alive = null)
        => new(new LocatorOptions(null, Signpost, [Keys.Pub], [HubEndpointMode.Zt, .. LocatorOptions.DefaultModes]),
            new HttpClient(_routes), new SeenSeqStore(_seenPath),
            _ => Task.FromResult<string?>(null),
            (url, _) => Task.FromResult(alive?.Contains(url) ?? false),
            ztProbe);

    [Fact]
    public async Task Zt_endpoint_probed_through_zt_hook_with_network_and_returned_first()
    {
        ServeSignpost(ZtHubEnv(1));
        (string Net, string Url)? asked = null;
        var loc = await ZtLocator((net, url, _) => { asked = (net, url); return Task.FromResult(true); }).LocateAsync();

        Assert.Equal((Nw, "http://10.88.0.1:5099"), asked);
        Assert.Equal(("http://10.88.0.1:5099", HubEndpointMode.Zt, Nw), (loc!.Url, loc.Mode, loc.ZtNetwork));
    }

    [Fact]
    public async Task Dead_zt_falls_through_to_lan_endpoint()
    {
        ServeSignpost(ZtHubEnv(1));
        var loc = await ZtLocator((_, _, _) => Task.FromResult(false), alive: ["http://192.168.1.102:5099"]).LocateAsync();

        Assert.Equal(HubEndpointMode.Lan, loc!.Mode);
        Assert.Null(loc.ZtNetwork);
        Assert.Contains(loc.Rejected, r => r.Source.EndsWith("/zt") && r.Reason.Contains(Nw));
    }

    [Fact]
    public async Task Zt_without_hook_or_network_is_rejected_with_reason_not_probed_by_os_socket()
    {
        ServeSignpost(ZtHubEnv(1));
        var noHook = ZtLocator(null, alive: ["http://10.88.0.1:5099"]);
        var loc = await noHook.LocateAsync();
        Assert.Null(loc);
        Assert.Contains(noHook.LastRejected, r => r.Source.EndsWith("/zt") && r.Reason.Contains("ZeroTier"));

        ServeSignpost(ZtHubEnv(2, network: null));
        var noNet = ZtLocator((_, _, _) => Task.FromResult(true));
        Assert.Null(await noNet.LocateAsync());
        Assert.Contains(noNet.LastRejected, r => r.Reason.Contains("id сети"));
    }

    [Fact]
    public async Task Default_modes_still_skip_zt_for_processes_without_zerotier()
    {
        ServeSignpost(ZtHubEnv(1));
        var loc = await Locator(alive: ["http://192.168.1.102:5099"]).LocateAsync();
        Assert.Equal(HubEndpointMode.Lan, loc!.Mode);
        Assert.Contains(loc.Rejected, r => r.Source.EndsWith("/zt") && r.Reason.Contains("не поддерживается"));
    }
}
