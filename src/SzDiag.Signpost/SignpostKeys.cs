using SzDiag.Contracts.Signing;

namespace SzDiag.Signpost;

/// <summary>Проверяющий ключ флота; null — переменная SZDIAG_SIGN_PUBKEY не задана, запись закрыта.</summary>
public sealed record SignpostKeys(EnvelopeVerifier? Verifier);
