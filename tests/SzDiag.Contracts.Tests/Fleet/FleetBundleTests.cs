using System.Text.Json;
using SzDiag.Contracts.Fleet;
using SzDiag.Contracts.Signing;

namespace SzDiag.Contracts.Tests.Fleet;

public class FleetBundleTests
{
    private static FleetSecrets Sample()
    {
        var (priv, pub) = FleetKeys.Generate();
        return new FleetSecrets
        {
            SignPrivateKey = priv,
            SignPublicKey = pub,
            AgentToken = "agent-token-x",
            ManagementToken = "mgmt-token-y",
            SvcDiagKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----\n",
            SvcDiagKeyPub = "ssh-ed25519 AAAA szdiag-service\n",
            ZtNetwork = "0123456789abcdef",
        };
    }

    [Fact]
    public void Encrypt_then_decrypt_with_same_password_roundtrips()
    {
        var s = Sample();
        var back = FleetBundle.Decrypt(FleetBundle.Encrypt(s, "пароль флота"), "пароль флота");

        Assert.Equal(s.AgentToken, back.AgentToken);
        Assert.Equal(s.SignPrivateKey, back.SignPrivateKey);
        Assert.Equal(s.SvcDiagKey, back.SvcDiagKey);
        Assert.Equal(s.ZtNetwork, back.ZtNetwork);
    }

    [Fact]
    public void Wrong_password_is_reported_as_such()
    {
        var bundle = FleetBundle.Encrypt(Sample(), "верный");
        var ex = Assert.Throws<FleetBundleException>(() => FleetBundle.Decrypt(bundle, "неверный"));
        Assert.Equal(FleetBundleError.WrongPassword, ex.Error);
    }

    [Fact]
    public void Tampered_tag_is_rejected()
    {
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(FleetBundle.Encrypt(Sample(), "p"))!;
        var tag = Convert.FromBase64String(doc["tag"].GetString()!);
        tag[0] ^= 0xFF;
        var dict = doc.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        dict["tag"] = Convert.ToBase64String(tag);

        var ex = Assert.Throws<FleetBundleException>(() => FleetBundle.Decrypt(JsonSerializer.Serialize(dict), "p"));
        Assert.Equal(FleetBundleError.WrongPassword, ex.Error);
    }

    [Fact]
    public void Wrapper_has_documented_shape_and_no_plaintext()
    {
        var s = Sample();
        var json = FleetBundle.Encrypt(s, "p");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("v").GetInt32());
        Assert.Equal("pbkdf2-sha256", root.GetProperty("kdf").GetString());
        Assert.Equal(600_000, root.GetProperty("iter").GetInt32());
        foreach (var f in new[] { "salt", "nonce", "ct", "tag" })
            Assert.False(string.IsNullOrEmpty(root.GetProperty(f).GetString()));
        Assert.Equal(16, Convert.FromBase64String(root.GetProperty("salt").GetString()!).Length);

        Assert.DoesNotContain(s.AgentToken, json);
        Assert.DoesNotContain("PRIVATE KEY", json);
    }

    [Fact]
    public void Two_encryptions_differ()
        => Assert.NotEqual(FleetBundle.Encrypt(Sample(), "p"), FleetBundle.Encrypt(Sample(), "p"));

    [Theory]
    [InlineData("не json")]
    [InlineData("{}")]
    public void Garbage_bundle_is_malformed(string json)
    {
        var ex = Assert.Throws<FleetBundleException>(() => FleetBundle.Decrypt(json, "p"));
        Assert.Equal(FleetBundleError.Malformed, ex.Error);
    }

    [Fact]
    public void Empty_password_cannot_encrypt()
        => Assert.Throws<ArgumentException>(() => FleetBundle.Encrypt(Sample(), ""));

    [Fact]
    public void Store_write_then_read_roundtrips_and_keeps_ssh_key_names()
    {
        var dir = Path.Combine(Path.GetTempPath(), "szfleet-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = Sample();
            FleetSecretsStore.Write(dir, s);

            Assert.True(File.Exists(Path.Combine(dir, "svc_diag_key")));
            Assert.True(File.Exists(Path.Combine(dir, "fleet-sign.key")));
            Assert.True(FleetSecretsStore.Exists(dir));
            // Приватный ssh-ключ — без BOM, иначе OpenSSH его не прочтёт.
            Assert.NotEqual(0xEF, File.ReadAllBytes(Path.Combine(dir, "svc_diag_key"))[0]);

            var back = FleetSecretsStore.Read(dir);
            Assert.Equal(s.ManagementToken, back.ManagementToken);
            Assert.Equal(s.SignPublicKey, back.SignPublicKey);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
