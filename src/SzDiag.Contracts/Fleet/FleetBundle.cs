using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SzDiag.Contracts.Fleet;

public enum FleetBundleError
{
    Malformed,
    UnsupportedFormat,
    /// <summary>Пароль неверный ИЛИ шифротекст испорчен — AES-GCM их не различает (и не должен).</summary>
    WrongPassword,
}

public sealed class FleetBundleException(FleetBundleError error, string message) : Exception(message)
{
    public FleetBundleError Error { get; } = error;
}

/// <summary>Шифрованная обёртка секретов флота. Лежит переменной <c>SZDIAG_FLEET_BUNDLE</c>
/// сервиса Railway: сервис получает её в env, но расшифровать не может — пароль флота знает
/// только оператор. Поэтому сервис указателя не способен подписать запись сам (FR-004).</summary>
public static class FleetBundle
{
    public const string Kdf = "pbkdf2-sha256";
    public const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Wrapper(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("kdf")] string Kdf,
        [property: JsonPropertyName("iter")] int Iter,
        [property: JsonPropertyName("salt")] string Salt,
        [property: JsonPropertyName("nonce")] string Nonce,
        [property: JsonPropertyName("ct")] string Ct,
        [property: JsonPropertyName("tag")] string Tag);

    public static string Encrypt(FleetSecrets secrets, string password)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("пароль флота пуст", nameof(password));

        var plain = JsonSerializer.SerializeToUtf8Bytes(secrets, Json);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt, Iterations);
        var ct = new byte[plain.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, ct, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }

        return JsonSerializer.Serialize(new Wrapper(1, Kdf, Iterations,
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
            Convert.ToBase64String(ct), Convert.ToBase64String(tag)));
    }

    public static FleetSecrets Decrypt(string bundleJson, string password)
    {
        Wrapper? w;
        try { w = JsonSerializer.Deserialize<Wrapper>(bundleJson); }
        catch (JsonException) { w = null; }
        if (w is null || w.Salt is null || w.Nonce is null || w.Ct is null || w.Tag is null)
            throw new FleetBundleException(FleetBundleError.Malformed, "бандл флота не разобран");
        if (w.V != 1 || w.Kdf != Kdf || w.Iter < 100_000)
            throw new FleetBundleException(FleetBundleError.UnsupportedFormat,
                $"неподдерживаемый формат бандла (v={w.V}, kdf={w.Kdf}, iter={w.Iter})");

        byte[] salt, nonce, ct, tag;
        try
        {
            salt = Convert.FromBase64String(w.Salt);
            nonce = Convert.FromBase64String(w.Nonce);
            ct = Convert.FromBase64String(w.Ct);
            tag = Convert.FromBase64String(w.Tag);
        }
        catch (FormatException) { throw new FleetBundleException(FleetBundleError.Malformed, "бандл флота: битый base64"); }
        if (nonce.Length != NonceSize || tag.Length != TagSize)
            throw new FleetBundleException(FleetBundleError.Malformed, "бандл флота: неверная длина nonce/tag");

        var key = DeriveKey(password ?? "", salt, w.Iter);
        var plain = new byte[ct.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ct, tag, plain);
        }
        catch (CryptographicException)
        {
            throw new FleetBundleException(FleetBundleError.WrongPassword, "неверный пароль флота (или бандл испорчен)");
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        try
        {
            return JsonSerializer.Deserialize<FleetSecrets>(plain, Json)
                   ?? throw new FleetBundleException(FleetBundleError.Malformed, "бандл флота пуст");
        }
        catch (JsonException ex)
        {
            throw new FleetBundleException(FleetBundleError.Malformed, $"содержимое бандла не разобрано: {ex.Message}");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, KeySize);
}
