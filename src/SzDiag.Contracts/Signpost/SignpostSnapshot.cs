using SzDiag.Contracts.Signing;

namespace SzDiag.Contracts.Signpost;

/// <summary>Ответ `GET /signpost`. Клиент его не принимает на веру — каждый конверт
/// перепроверяет сам: сервис указателя — недоверенный транспорт, доверие держится только на ключе флота.</summary>
public sealed record SignpostSnapshot(SignedEnvelope? Hub, SignedEnvelope? Release, LeaseInfo? Lease);

/// <summary>Кто держит указатель и до какого момента (по часам сервиса).</summary>
public sealed record LeaseInfo(string HolderHubId, string HubName, DateTimeOffset ExpiresAt);

/// <summary>Состояние публикации hub в указателе — для `/api/status` и статусбара Desk (FR-025).</summary>
/// <param name="State">Одно из <see cref="SignpostStates"/>.</param>
/// <param name="LastPublishedAt">Когда указатель последний раз принял запись этого hub.</param>
/// <param name="Holder">Держатель аренды, если это не мы.</param>
/// <param name="Message">Причина последней ошибки — для людей.</param>
public sealed record SignpostStatus(string State, DateTimeOffset? LastPublishedAt, LeaseInfo? Holder, string? Message);

public static class SignpostStates
{
    public const string Off = "off";
    public const string Published = "published";
    /// <summary>Указатель держит другой hub, перехват — только `--takeover`.</summary>
    public const string OtherHolder = "other-holder";
    public const string Unreachable = "unreachable";
    /// <summary>Нет ключа подписи или указатель отверг подпись — настройка, а не сеть.</summary>
    public const string Misconfigured = "misconfigured";
}
