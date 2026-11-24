using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using SzDiag.Contracts;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Signpost.Tests;

public class SignpostApiTests : IDisposable
{
    private readonly (string Priv, string Pub) _keys = FleetKeys.Generate();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "szsignpost-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly EnvelopeSigner _signer;

    public SignpostApiTests()
    {
        _signer = EnvelopeSigner.FromPrivatePem(_keys.Priv);
        _factory = Factory(_keys.Pub);
    }

    private WebApplicationFactory<Program> Factory(string? pub) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(b =>
        {
            b.UseSetting("DATA_DIR", _data);
            b.UseSetting("SZDIAG_SIGN_PUBKEY", pub ?? "");
        });

    public void Dispose()
    {
        _factory.Dispose();
        _signer.Dispose();
        if (Directory.Exists(_data)) Directory.Delete(_data, true);
    }

    private static HubRecord Hub(long seq) => new("pc", "ПК",
        [new HubEndpoint(HubEndpointMode.Lan, "http://192.168.1.102:5099")], null, seq, DateTimeOffset.UtcNow);

    private static StringContent Json(SignedEnvelope env) => new(env.ToJson(), Encoding.UTF8, "application/json");

    private static (byte[] Bytes, string Sha) Package(string text = "zip-bytes")
    {
        var b = Encoding.UTF8.GetBytes(text);
        return (b, Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant());
    }

    private ReleaseRecord Release(string sha, long seq) =>
        new("abc1234", DateTimeOffset.UtcNow, sha, 9, [SignpostRoutes.PackageFor(sha)], seq);

    /// <summary>Загрузка пакета так, как это делает build-dist -Publish: с подписанным релизом в заголовке.</summary>
    private Task<HttpResponseMessage> Upload(HttpClient c, string sha, byte[] bytes, SignedEnvelope? release = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, SignpostRoutes.PackageFor(sha)) { Content = new ByteArrayContent(bytes) };
        release ??= _signer.SignRecord(Release(sha, 1));
        req.Headers.Add(SignpostRoutes.ReleaseHeader, Convert.ToBase64String(Encoding.UTF8.GetBytes(release.ToJson())));
        return c.SendAsync(req);
    }

    [Fact]
    public async Task Health_answers_ok()
    {
        var r = await _factory.CreateClient().GetStringAsync(SignpostRoutes.Health);
        Assert.Equal("ok", r);
    }

    [Fact]
    public async Task Valid_hub_envelope_is_published_and_served()
    {
        var c = _factory.CreateClient();
        var put = await c.PutAsync(SignpostRoutes.Hub, Json(_signer.SignRecord(Hub(1))));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var snap = await c.GetFromJsonAsync<SignpostSnapshot>(SignpostRoutes.Signpost, SignpostJson.Options);
        using var v = new EnvelopeVerifier(_keys.Pub);
        Assert.Equal(EnvelopeFailure.None, v.Open<HubRecord>(snap!.Hub, out var rec));
        Assert.Equal("pc", rec!.HubId);
        Assert.Equal("pc", snap.Lease!.HolderHubId);
    }

    [Fact]
    public async Task Bad_signature_is_400_and_nothing_changes()
    {
        var c = _factory.CreateClient();
        var env = _signer.SignRecord(Hub(1));
        var sig = Convert.FromBase64String(env.Sig);
        sig[3] ^= 0x55;

        var put = await c.PutAsync(SignpostRoutes.Hub, Json(env with { Sig = Convert.ToBase64String(sig) }));

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        var snap = await c.GetFromJsonAsync<SignpostSnapshot>(SignpostRoutes.Signpost, SignpostJson.Options);
        Assert.Null(snap!.Hub);
    }

    [Fact]
    public async Task Foreign_key_is_400()
    {
        var other = FleetKeys.Generate();
        using var foreign = EnvelopeSigner.FromPrivatePem(other.PrivatePem);
        var put = await _factory.CreateClient().PutAsync(SignpostRoutes.Hub, Json(foreign.SignRecord(Hub(1))));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Seq_must_grow_412()
    {
        var c = _factory.CreateClient();
        await c.PutAsync(SignpostRoutes.Hub, Json(_signer.SignRecord(Hub(5))));
        var put = await c.PutAsync(SignpostRoutes.Hub, Json(_signer.SignRecord(Hub(5))));
        Assert.Equal(HttpStatusCode.PreconditionFailed, put.StatusCode);
    }

    [Fact]
    public async Task Second_hub_gets_409_with_holder()
    {
        var c = _factory.CreateClient();
        await c.PutAsync(SignpostRoutes.Hub, Json(_signer.SignRecord(Hub(1))));
        var laptop = new HubRecord("laptop", "ноут", [new HubEndpoint(HubEndpointMode.Lan, "http://10.0.0.5:5099")],
            null, 1, DateTimeOffset.UtcNow);

        var put = await c.PutAsync(SignpostRoutes.Hub, Json(_signer.SignRecord(laptop)));

        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var lease = await put.Content.ReadFromJsonAsync<LeaseInfo>(SignpostJson.Options);
        Assert.Equal("pc", lease!.HolderHubId);
    }

    [Fact]
    public async Task Package_with_wrong_sha_is_400()
    {
        var (bytes, _) = Package();
        var wrong = new string('a', 64);
        var put = await Upload(_factory.CreateClient(), wrong, bytes);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _factory.CreateClient().GetAsync(SignpostRoutes.PackageFor(wrong))).StatusCode);
    }

    [Fact]
    public async Task Release_without_uploaded_package_is_400()
    {
        var (_, sha) = Package();
        var put = await _factory.CreateClient().PutAsync(SignpostRoutes.Release, Json(_signer.SignRecord(Release(sha, 1))));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Package_then_release_is_served_and_downloadable()
    {
        var c = _factory.CreateClient();
        var (bytes, sha) = Package();
        Assert.Equal(HttpStatusCode.Created, (await Upload(c, sha, bytes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await c.PutAsync(SignpostRoutes.Release, Json(_signer.SignRecord(Release(sha, 1))))).StatusCode);

        var snap = await c.GetFromJsonAsync<SignpostSnapshot>(SignpostRoutes.Signpost, SignpostJson.Options);
        Assert.NotNull(snap!.Release);
        Assert.Equal(bytes, await c.GetByteArrayAsync(SignpostRoutes.PackageFor(sha)));

        // Тот же seq второй раз — 412; релиз пережил бы рестарт (лежит на volume).
        Assert.Equal(HttpStatusCode.PreconditionFailed,
            (await c.PutAsync(SignpostRoutes.Release, Json(_signer.SignRecord(Release(sha, 1))))).StatusCode);
    }

    [Fact]
    public async Task Keeps_last_three_packages()
    {
        var c = _factory.CreateClient();
        var shas = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var (bytes, sha) = Package($"pkg-{i}");
            shas.Add(sha);
            await Upload(c, sha, bytes);
            await Task.Delay(20);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(SignpostRoutes.PackageFor(shas[0]))).StatusCode);
        foreach (var sha in shas.Skip(1))
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(SignpostRoutes.PackageFor(sha))).StatusCode);
    }

    [Fact]
    public async Task Upload_without_signed_release_is_401()
    {
        var (bytes, sha) = Package();
        var put = await _factory.CreateClient().PutAsync(SignpostRoutes.PackageFor(sha), new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task Upload_with_release_for_other_sha_is_401()
    {
        var (bytes, sha) = Package();
        var put = await Upload(_factory.CreateClient(), sha, bytes, _signer.SignRecord(Release(new string('b', 64), 1)));
        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
    }

    [Fact]
    public async Task Current_release_package_survives_three_new_uploads()
    {
        var c = _factory.CreateClient();
        var (curBytes, curSha) = Package("current");
        await Upload(c, curSha, curBytes);
        await c.PutAsync(SignpostRoutes.Release, Json(_signer.SignRecord(Release(curSha, 1))));

        for (var i = 0; i < 3; i++)
        {
            var (bytes, sha) = Package($"new-{i}");
            await Upload(c, sha, bytes);
            await Task.Delay(20);
        }

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(SignpostRoutes.PackageFor(curSha))).StatusCode);
    }

    [Fact]
    public async Task Without_public_key_writes_are_503()
    {
        using var f = Factory(null);
        var put = await f.CreateClient().PutAsync(SignpostRoutes.Hub, Json(_signer.SignRecord(Hub(1))));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, put.StatusCode);
    }
}
