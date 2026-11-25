using System.Security.Cryptography;

namespace SzDiag.Contracts.Signing;

/// <summary>Ключ подписи флота: ECDSA P-256 из BCL (Ed25519 в .NET 8 нет, а сторонняя
/// криптография в Contracts тянулась бы во все процессы, включая клиентские).</summary>
public static class FleetKeys
{
    /// <summary>Новая пара: приватный PKCS#8 PEM и публичный SPKI PEM.</summary>
    public static (string PrivatePem, string PublicPem) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Идентификатор ключа: первые 16 hex SHA-256 от SPKI. Одинаков для приватной и
    /// публичной части — по нему проверяющий выбирает ключ и отличает «чужой ключ» от «битой подписи».</summary>
    public static string KeyId(ECDsa key)
        => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();

    public static ECDsa ImportPem(string pem)
    {
        var key = ECDsa.Create();
        try { key.ImportFromPem(pem); }
        catch { key.Dispose(); throw; }
        if (key.KeySize != 256)
        {
            key.Dispose();
            throw new CryptographicException($"ожидался ключ P-256, получен {key.KeySize} бит");
        }
        return key;
    }
}
