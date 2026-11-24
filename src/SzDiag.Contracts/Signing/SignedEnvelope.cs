using System.Text.Json;
using System.Text.Json.Serialization;

namespace SzDiag.Contracts.Signing;

/// <summary>Подписанный конверт: сырые байты нагрузки + подпись над ними. Подпись проверяется
/// над <see cref="Payload"/> как есть — JSON нагрузки перед проверкой не пересериализуется,
/// иначе любой расхождение в форматировании (порядок полей, пробелы) ломало бы подпись.</summary>
/// <param name="Payload">base64 сырых UTF-8 байтов JSON нагрузки.</param>
/// <param name="Sig">base64 подписи ECDSA P-256/SHA-256 в формате IEEE P1363.</param>
/// <param name="Kid">Идентификатор ключа: первые 16 hex SHA-256 от SPKI публичного ключа.</param>
public sealed record SignedEnvelope(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("sig")] string Sig,
    [property: JsonPropertyName("kid")] string Kid)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>Разобрать конверт; мусор → null (вызывающий трактует как Malformed).</summary>
    public static SignedEnvelope? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var env = JsonSerializer.Deserialize<SignedEnvelope>(json);
            return env is { Payload.Length: > 0, Sig.Length: > 0, Kid.Length: > 0 } ? env : null;
        }
        catch (JsonException) { return null; }
    }
}
