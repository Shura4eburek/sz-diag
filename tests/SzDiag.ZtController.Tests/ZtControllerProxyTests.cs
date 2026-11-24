using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace SzDiag.ZtController.Tests;

public class ZtRoutesTests
{
    private const string Nw = "/controller/network/0075ada1a4dddade";
    private const string Mem = Nw + "/member/ae264cb785";

    [Theory]
    [InlineData("GET", "/status")]
    [InlineData("GET", "/controller")]
    [InlineData("GET", "/controller/network")]
    [InlineData("POST", "/controller/network/0075ada1a4______")]
    [InlineData("GET", Nw)]
    [InlineData("POST", Nw)]
    [InlineData("DELETE", Nw)]
    [InlineData("GET", Nw + "/member")]
    [InlineData("GET", Mem)]
    [InlineData("POST", Mem)]
    [InlineData("DELETE", Mem)]
    public void Allowed(string method, string path) => Assert.True(ZtRoutes.IsAllowed(method, path));

    [Theory]
    [InlineData("POST", "/status")]
    [InlineData("GET", "/peer")]
    [InlineData("GET", "/network")]
    [InlineData("POST", "/network/0075ada1a4dddade")]       // join самой ноды-контроллера
    [InlineData("GET", "/config")]
    [InlineData("GET", "/controller/network/0075ada1a4______")]
    [InlineData("DELETE", "/controller/network/0075ada1a4______")]
    [InlineData("GET", "/controller/network/0075ADA1A4DDDADE")]
    [InlineData("GET", "/controller/network/0075ada1a4dddade/../../peer")]
    [InlineData("POST", Nw + "/member")]
    [InlineData("GET", Mem + "/x")]
    [InlineData("PUT", Mem)]
    public void Denied(string method, string path) => Assert.False(ZtRoutes.IsAllowed(method, path));
}

public class ZtControllerProxyTests : IDisposable
{
    private const string Token = "test-token-0123456789-abcdefghijklmnop";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "szztctl-" + Guid.NewGuid().ToString("N"));
    private readonly FakeZt _zt = new();
    private readonly WebApplicationFactory<Program> _factory;

    public ZtControllerProxyTests()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "authtoken.secret"), "local-zt-auth\n");
        File.WriteAllText(Path.Combine(_home, "identity.secret"), "0075ada1a4:0:pub:priv\n");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ZT_PROXY_TOKEN", Token);
            b.UseSetting("ZT_HOME", _home);
            b.ConfigureTestServices(s => s.AddHttpClient("zt").ConfigurePrimaryHttpMessageHandler(() => _zt));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_home, true);
    }

    private HttpClient Client(string? token = Token)
    {
        var c = _factory.CreateClient();
        if (token != null) c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    [Fact]
    public async Task WithoutToken_401_AndUpstreamNotCalled()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/controller")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("wrong").GetAsync("/controller")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync("/admin/identity")).StatusCode);
        Assert.Empty(_zt.Requests);
    }

    [Fact]
    public async Task NotWhitelisted_403_AndUpstreamNotCalled()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client().GetAsync("/peer")).StatusCode);
        Assert.Empty(_zt.Requests);
    }

    [Fact]
    public async Task Allowed_ForwardedWithLocalAuth_BodyAndStatusPassedThrough()
    {
        var resp = await Client().PostAsync("/controller/network/0075ada1a4dddade/member/ae264cb785",
            new StringContent("{\"authorized\":false}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("{\"ok\":true}", await resp.Content.ReadAsStringAsync());
        var r = Assert.Single(_zt.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/controller/network/0075ada1a4dddade/member/ae264cb785", r.Path);
        Assert.Equal("local-zt-auth", r.Auth);
        Assert.Equal("{\"authorized\":false}", r.Body);
        Assert.Null(r.Bearer); // токен hub дальше прокси не уходит
    }

    [Fact]
    public async Task AdminIdentity_ReturnsSecretForFleetBackup()
    {
        Assert.Equal("0075ada1a4:0:pub:priv", await Client().GetStringAsync("/admin/identity"));
    }

    [Fact]
    public async Task Healthz_NoToken_ReflectsUpstream()
    {
        Assert.Equal("ok", await Client(null).GetStringAsync("/healthz"));
        _zt.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await Client(null).GetAsync("/healthz")).StatusCode);
    }

    private sealed class FakeZt : HttpMessageHandler
    {
        public record Req(string Method, string Path, string? Auth, string? Bearer, string? Body);
        public List<Req> Requests { get; } = [];
        public bool Fail { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            var path = request.RequestUri!.AbsolutePath;
            if (path != "/status")
                Requests.Add(new Req(request.Method.Method, path,
                    request.Headers.TryGetValues("X-ZT1-Auth", out var v) ? v.Single() : null,
                    request.Headers.Authorization?.Parameter,
                    request.Content == null ? null : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") };
        }
    }
}
