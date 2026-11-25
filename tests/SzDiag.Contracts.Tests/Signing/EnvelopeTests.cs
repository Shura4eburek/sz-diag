using System.Security.Cryptography;
using System.Text;
using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Contracts.Tests.Signing;

public class EnvelopeTests
{
    private static readonly (string Priv, string Pub) Keys = FleetKeys.Generate();

    private static HubRecord SampleHub() => new("hub-pc", "ПК",
        [new HubEndpoint(HubEndpointMode.Lan, "http://192.168.1.102:5099")],
        null, Seq: 1_700_000_000_000, IssuedAt: DateTimeOffset.UnixEpoch);

    [Fact]
    public void Sign_then_open_roundtrips_record()
    {
        using var signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        using var verifier = new EnvelopeVerifier(Keys.Pub);

        var env = signer.SignRecord(SampleHub());

        Assert.Equal(EnvelopeFailure.None, verifier.Open<HubRecord>(env, out var rec));
        Assert.Equal("hub-pc", rec!.HubId);
        Assert.Equal(SignpostKinds.Hub, rec.Kind);
        Assert.Equal("http://192.168.1.102:5099", rec.Endpoints[0].Url);
    }

    [Fact]
    public void Envelope_survives_json_roundtrip()
    {
        using var signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        using var verifier = new EnvelopeVerifier(Keys.Pub);

        var parsed = SignedEnvelope.TryParse(signer.SignRecord(SampleHub()).ToJson());

        Assert.Equal(EnvelopeFailure.None, verifier.Verify(parsed, out _));
    }

    [Fact]
    public void Corrupted_signature_is_rejected()
    {
        using var signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        using var verifier = new EnvelopeVerifier(Keys.Pub);
        var env = signer.SignRecord(SampleHub());
        var sig = Convert.FromBase64String(env.Sig);
        sig[0] ^= 0xFF;

        Assert.Equal(EnvelopeFailure.BadSignature, verifier.Verify(env with { Sig = Convert.ToBase64String(sig) }, out _));
    }

    [Fact]
    public void One_changed_payload_byte_is_rejected()
    {
        using var signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        using var verifier = new EnvelopeVerifier(Keys.Pub);
        var env = signer.Sign(Encoding.UTF8.GetBytes("""{"hubId":"hub-pc"}"""));
        var data = Convert.FromBase64String(env.Payload);
        data[^2] ^= 0x01;

        Assert.Equal(EnvelopeFailure.BadSignature, verifier.Verify(env with { Payload = Convert.ToBase64String(data) }, out _));
    }

    [Fact]
    public void Foreign_key_is_unknown_kid()
    {
        var other = FleetKeys.Generate();
        using var signer = EnvelopeSigner.FromPrivatePem(other.PrivatePem);
        using var verifier = new EnvelopeVerifier(Keys.Pub);

        Assert.Equal(EnvelopeFailure.UnknownKid, verifier.Verify(signer.SignRecord(SampleHub()), out _));
    }

    [Fact]
    public void Forged_kid_with_foreign_signature_is_bad_signature()
    {
        var other = FleetKeys.Generate();
        using var foreign = EnvelopeSigner.FromPrivatePem(other.PrivatePem);
        using var ours = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        using var verifier = new EnvelopeVerifier(Keys.Pub);

        var forged = foreign.SignRecord(SampleHub()) with { Kid = ours.Kid };

        Assert.Equal(EnvelopeFailure.BadSignature, verifier.Verify(forged, out _));
    }

    [Fact]
    public void Kid_is_first_16_hex_of_spki_sha256_and_same_for_both_halves()
    {
        using var signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        using var pub = FleetKeys.ImportPem(Keys.Pub);
        var expected = Convert.ToHexString(SHA256.HashData(pub.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

        Assert.Equal(expected, signer.Kid);
        Assert.Equal(expected, FleetKeys.KeyId(pub));
        Assert.Equal(16, signer.Kid.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("не json")]
    [InlineData("""{"payload":"","sig":"x","kid":"y"}""")]
    public void Garbage_envelope_does_not_parse(string? json)
        => Assert.Null(SignedEnvelope.TryParse(json));

    [Fact]
    public void Bad_base64_is_malformed()
    {
        using var verifier = new EnvelopeVerifier(Keys.Pub);
        using var signer = EnvelopeSigner.FromPrivatePem(Keys.Priv);
        var env = signer.SignRecord(SampleHub()) with { Payload = "%%%не base64%%%" };

        Assert.Equal(EnvelopeFailure.Malformed, verifier.Verify(env, out _));
    }

    [Fact]
    public void Public_key_cannot_be_used_for_signing()
        => Assert.Throws<CryptographicException>(() => EnvelopeSigner.FromPrivatePem(Keys.Pub));
}
