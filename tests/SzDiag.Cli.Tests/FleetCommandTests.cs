using SzDiag.Cli;
using SzDiag.Contracts.Fleet;

namespace SzDiag.Cli.Tests;

public class FleetCommandTests : IDisposable
{
    private readonly string _a = Path.Combine(Path.GetTempPath(), "szfleet-a-" + Guid.NewGuid().ToString("N"));
    private readonly string _b = Path.Combine(Path.GetTempPath(), "szfleet-b-" + Guid.NewGuid().ToString("N"));
    private const string Password = "длинный пароль флота";

    public FleetCommandTests()
    {
        Directory.CreateDirectory(_a);
        File.WriteAllText(Path.Combine(_a, "svc_diag_key"), "PRIVATE\n");
        File.WriteAllText(Path.Combine(_a, "svc_diag_key.pub"), "ssh-ed25519 AAAA szdiag-service\n");
    }

    public void Dispose()
    {
        foreach (var d in new[] { _a, _b })
            if (Directory.Exists(d)) Directory.Delete(d, true);
    }

    private static int Run(string[] args, out string stdout, out string stderr, string? password = Password)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = FleetCommand.Run(args, o, e, _ => password);
        stdout = o.ToString();
        stderr = e.ToString();
        return code;
    }

    [Fact]
    public void Keygen_encrypt_decrypt_moves_same_fleet_to_another_machine()
    {
        Assert.Equal(0, Run(["keygen", "--secrets", _a, "--agent-token", "old-pc-token"], out _, out _));
        Assert.Equal(0, Run(["encrypt", "--secrets", _a], out var bundle, out _));

        var bundleFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(bundleFile, bundle);
        try
        {
            Assert.Equal(0, Run(["decrypt", "--secrets", _b, "--in", bundleFile], out _, out _));
        }
        finally { File.Delete(bundleFile); }

        var a = FleetSecretsStore.Read(_a);
        var b = FleetSecretsStore.Read(_b);
        Assert.Equal("old-pc-token", b.AgentToken);
        Assert.Equal(a.ManagementToken, b.ManagementToken);
        Assert.Equal(a.SignPrivateKey, b.SignPrivateKey);
        Assert.Equal(File.ReadAllText(Path.Combine(_a, "svc_diag_key")), File.ReadAllText(Path.Combine(_b, "svc_diag_key")));
    }

    private string ZtJson(string json)
    {
        var path = Path.Combine(_a, "zt.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void SetZt_adds_controller_and_network_keeping_other_secrets_and_survives_bundle()
    {
        Assert.Equal(0, Run(["keygen", "--secrets", _a, "--agent-token", "tok"], out _, out _));
        var before = FleetSecretsStore.Read(_a);
        var zt = ZtJson("""
            {"controllerUrl":"https://ctl.example/","controllerToken":"bearer-x","identitySecret":"aa4bb3b8ba:0:pub:priv\n","network":"aa4bb3b8ba2a96ee"}
            """);

        Assert.Equal(0, Run(["set-zt", "--secrets", _a, "--from", zt], out _, out _));

        var s = FleetSecretsStore.Read(_a);
        Assert.Equal("https://ctl.example", s.ZtControllerUrl);
        Assert.Equal("bearer-x", s.ZtControllerToken);
        Assert.Equal("aa4bb3b8ba:0:pub:priv", s.ZtIdentitySecret);
        Assert.Equal("aa4bb3b8ba2a96ee", s.ZtNetwork);
        Assert.Equal(before.SignPrivateKey, s.SignPrivateKey);
        Assert.Equal("tok", s.AgentToken);

        // Через бандл ZT-поля доезжают до другой машины.
        Assert.Equal(0, Run(["encrypt", "--secrets", _a], out var bundle, out _));
        var bf = Path.Combine(_a, "bundle.json");
        File.WriteAllText(bf, bundle);
        Assert.Equal(0, Run(["decrypt", "--secrets", _b, "--in", bf], out _, out _));
        Assert.Equal("aa4bb3b8ba2a96ee", FleetSecretsStore.Read(_b).ZtNetwork);
        Assert.Equal("bearer-x", FleetSecretsStore.Read(_b).ZtControllerToken);
    }

    [Fact]
    public void SetZt_empty_fields_do_not_erase_and_bad_network_is_refused()
    {
        Assert.Equal(0, Run(["keygen", "--secrets", _a], out _, out _));
        Assert.Equal(0, Run(["set-zt", "--secrets", _a, "--from",
            ZtJson("""{"controllerUrl":"https://ctl","controllerToken":"t1","network":"aa4bb3b8ba2a96ee"}""")], out _, out _));

        Assert.Equal(0, Run(["set-zt", "--secrets", _a, "--from", ZtJson("""{"identitySecret":"id"}""")], out _, out _));
        var s = FleetSecretsStore.Read(_a);
        Assert.Equal("t1", s.ZtControllerToken);
        Assert.Equal("aa4bb3b8ba2a96ee", s.ZtNetwork);
        Assert.Equal("id", s.ZtIdentitySecret);

        Assert.Equal(FleetCommand.Usage, Run(["set-zt", "--secrets", _a, "--from", ZtJson("""{"network":"short"}""")], out _, out _));
        Assert.Equal("aa4bb3b8ba2a96ee", FleetSecretsStore.Read(_a).ZtNetwork);
    }

    [Fact]
    public void Keygen_refuses_to_overwrite_without_force()
    {
        Assert.Equal(0, Run(["keygen", "--secrets", _a], out _, out _));
        var first = FleetSecretsStore.Read(_a).SignPublicKey;

        Assert.Equal(FleetCommand.AlreadyExists, Run(["keygen", "--secrets", _a], out _, out _));
        Assert.Equal(first, FleetSecretsStore.Read(_a).SignPublicKey);

        Assert.Equal(0, Run(["keygen", "--secrets", _a, "--force"], out _, out _));
        Assert.NotEqual(first, FleetSecretsStore.Read(_a).SignPublicKey);
    }

    [Fact]
    public void Keygen_without_ssh_key_fails()
    {
        File.Delete(Path.Combine(_a, "svc_diag_key"));
        Assert.Equal(FleetCommand.Usage, Run(["keygen", "--secrets", _a], out _, out _));
        Assert.False(FleetSecretsStore.Exists(_a));
    }

    [Fact]
    public void Decrypt_with_wrong_password_returns_4_and_writes_nothing()
    {
        Run(["keygen", "--secrets", _a], out _, out _);
        Run(["encrypt", "--secrets", _a], out var bundle, out _);
        var bundleFile = Path.Combine(_a, "bundle.json");
        File.WriteAllText(bundleFile, bundle);

        Assert.Equal(FleetCommand.WrongPassword,
            Run(["decrypt", "--secrets", _b, "--in", bundleFile], out _, out _, password: "совсем другой пароль"));
        Assert.False(FleetSecretsStore.Exists(_b));
    }

    [Fact]
    public void Short_password_is_refused()
    {
        Run(["keygen", "--secrets", _a], out _, out _);
        Assert.Equal(FleetCommand.Usage, Run(["encrypt", "--secrets", _a], out var bundle, out _, password: "short"));
        Assert.Equal("", bundle);
    }

    [Fact]
    public void Generated_tokens_are_random_and_url_safe()
    {
        Run(["keygen", "--secrets", _a], out _, out _);
        var s = FleetSecretsStore.Read(_a);
        Assert.NotEqual(s.AgentToken, s.ManagementToken);
        Assert.Equal(43, s.AgentToken.Length);
        Assert.DoesNotContain('+', s.AgentToken);
        Assert.DoesNotContain('/', s.AgentToken);
    }

    [Fact]
    public void Sign_release_produces_verifiable_envelope_with_package_hash()
    {
        Run(["keygen", "--secrets", _a], out _, out _);
        var zip = Path.Combine(_a, "package.zip");
        File.WriteAllText(zip, "пакет");

        Assert.Equal(0, Run(["sign-release", zip, "--secrets", _a, "--version", "abc1234",
            "--build-time", "2026-11-24T10:00:00Z", "--url", "/package/{sha256}", "--url", "https://m/package.zip"],
            out var json, out _));

        using var v = new SzDiag.Contracts.Signing.EnvelopeVerifier(FleetSecretsStore.Read(_a).SignPublicKey);
        Assert.Equal(SzDiag.Contracts.Signing.EnvelopeFailure.None,
            v.Open<SzDiag.Contracts.Signpost.ReleaseRecord>(SzDiag.Contracts.Signing.SignedEnvelope.TryParse(json), out var rel));
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant();
        Assert.Equal(sha, rel!.PackageSha256);
        Assert.Equal($"/package/{sha}", rel.PackageUrls[0]);
        Assert.Equal(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), rel.BuildTime);
    }

    [Fact]
    public void Sign_release_without_url_is_usage()
    {
        Run(["keygen", "--secrets", _a], out _, out _);
        var zip = Path.Combine(_a, "package.zip");
        File.WriteAllText(zip, "x");
        Assert.Equal(FleetCommand.Usage, Run(["sign-release", zip, "--secrets", _a, "--version", "v",
            "--build-time", "2026-11-24T10:00:00Z"], out _, out _));
    }

    [Fact]
    public void Default_secrets_dir_is_repo_root_secrets_not_cwd()
    {
        var bin = Path.Combine(_b, "src", "SzDiag.Cli", "bin", "Debug");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(_b, "SzDiag.sln"), "");
        Assert.Equal(Path.Combine(_b, "secrets"), FleetCommand.DefaultSecretsDir(bin));
    }

    [Fact]
    public void Default_secrets_dir_without_repo_is_next_to_exe()
    {
        var bin = Path.Combine(_b, "dist", "host", "cli");
        Directory.CreateDirectory(bin);
        Assert.Equal(Path.Combine(bin, "secrets"), FleetCommand.DefaultSecretsDir(bin));
    }
}
