using SzDiag.Contracts;

namespace SzDiag.Desk.Services;

public enum PollKind { Sessions, Transfers, Health }

/// <summary>Последнее, что известно от hub. При ошибке опроса прежние данные НЕ стираются —
/// окно показывает их с пометкой «данные на ЧЧ:ММ» (спека: «список СЗ заморожен»).</summary>
public sealed record HubSnapshot(
    IReadOnlyList<SessionInfo> Sessions,
    IReadOnlyList<TransferInfo> Transfers,
    HealthzResponse? Health,
    string? HubVersion,
    DateTimeOffset? SessionsOkAt,
    string? Error,
    int Failures)
{
    public static HubSnapshot Empty { get; } =
        new(Array.Empty<SessionInfo>(), Array.Empty<TransferInfo>(), null, null, null, null, 0);

    public bool IsStale => Error is not null;
}
