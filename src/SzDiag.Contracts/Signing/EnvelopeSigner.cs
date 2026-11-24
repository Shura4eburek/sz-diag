using System.Security.Cryptography;
using System.Text.Json;

namespace SzDiag.Contracts.Signing;

/// <summary>Подписывает нагрузку приватным ключом флота. Живёт только там, где лежит
/// <c>secrets\fleet-sign.key</c>: hub и build-dist. На клиента не попадает никогда.</summary>
public sealed class EnvelopeSigner : IDisposable
{
    private readonly ECDsa _key;

    public string Kid { get; }

    private EnvelopeSigner(ECDsa key)
    {
        _key = key;
        Kid = FleetKeys.KeyId(key);
    }

    /// <summary>Из PKCS#8 PEM. Публичный ключ здесь не годится — подписать им нельзя.</summary>
    public static EnvelopeSigner FromPrivatePem(string pkcs8Pem)
    {
        var key = FleetKeys.ImportPem(pkcs8Pem);
        try
        {
            // ImportFromPem молча принимает и публичный ключ; подпись тогда упала бы позже
            // с невнятной ошибкой — проверяем сразу.
            key.ExportParameters(includePrivateParameters: true);
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new CryptographicException("для подписи нужен приватный ключ флота (fleet-sign.key), а не публичный");
        }
        return new EnvelopeSigner(key);
    }

    public SignedEnvelope Sign(ReadOnlySpan<byte> payload)
    {
        var sig = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new SignedEnvelope(Convert.ToBase64String(payload), Convert.ToBase64String(sig), Kid);
    }

    public SignedEnvelope SignRecord<T>(T record)
        => Sign(JsonSerializer.SerializeToUtf8Bytes(record, SignpostJson.Options).AsSpan());

    public void Dispose() => _key.Dispose();
}
