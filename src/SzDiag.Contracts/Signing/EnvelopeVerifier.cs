using System.Security.Cryptography;
using System.Text.Json;

namespace SzDiag.Contracts.Signing;

/// <summary>Почему конверт отвергнут. Причина уходит в лог резолвера — «подпись не сошлась»
/// и «ключ не наш» означают разное (подмена против устаревшего клиента после ротации).</summary>
public enum EnvelopeFailure
{
    None,
    Malformed,
    UnknownKid,
    BadSignature,
}

/// <summary>Проверяет конверты публичными ключами флота (зашиты в клиента и в env указателя).</summary>
public sealed class EnvelopeVerifier : IDisposable
{
    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);

    public EnvelopeVerifier(IEnumerable<string> publicPems)
    {
        foreach (var pem in publicPems.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var key = FleetKeys.ImportPem(pem);
            _keys[FleetKeys.KeyId(key)] = key;
        }
    }

    public EnvelopeVerifier(string publicPem) : this([publicPem]) { }

    public bool HasKeys => _keys.Count > 0;

    /// <summary>Проверить подпись. Успех → сырые байты нагрузки, иначе причина отказа.</summary>
    public EnvelopeFailure Verify(SignedEnvelope? envelope, out byte[] payload)
    {
        payload = [];
        if (envelope is null) return EnvelopeFailure.Malformed;
        if (!_keys.TryGetValue(envelope.Kid, out var key)) return EnvelopeFailure.UnknownKid;

        byte[] data, sig;
        try
        {
            data = Convert.FromBase64String(envelope.Payload);
            sig = Convert.FromBase64String(envelope.Sig);
        }
        catch (FormatException) { return EnvelopeFailure.Malformed; }

        if (!key.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            return EnvelopeFailure.BadSignature;

        payload = data;
        return EnvelopeFailure.None;
    }

    /// <summary>Проверить подпись и разобрать нагрузку. Подпись сошлась, а JSON не тот → Malformed.</summary>
    public EnvelopeFailure Open<T>(SignedEnvelope? envelope, out T? record) where T : class
    {
        record = null;
        var failure = Verify(envelope, out var payload);
        if (failure != EnvelopeFailure.None) return failure;
        try { record = JsonSerializer.Deserialize<T>(payload, SignpostJson.Options); }
        catch (JsonException) { return EnvelopeFailure.Malformed; }
        return record is null ? EnvelopeFailure.Malformed : EnvelopeFailure.None;
    }

    public void Dispose()
    {
        foreach (var key in _keys.Values) key.Dispose();
        _keys.Clear();
    }
}
