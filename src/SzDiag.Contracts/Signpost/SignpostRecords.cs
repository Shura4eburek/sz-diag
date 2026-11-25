namespace SzDiag.Contracts.Signpost;

/// <summary>Режимы адреса hub в записи указателя.</summary>
public static class HubEndpointMode
{
    public const string Zt = "zt";
    public const string Lan = "lan";
    public const string Tunnel = "tunnel";
}

/// <summary>Один способ достучаться до hub. Порядок в списке = предпочтение.</summary>
public sealed record HubEndpoint(string Mode, string Url);

/// <summary>«Где сейчас hub». Публикует сам hub при старте и каждые 30 с (аренда).</summary>
/// <param name="HubId">Стабильный id машины-hub.</param>
/// <param name="HubName">Для людей: «ПК», «ноут».</param>
/// <param name="ZtNetwork">16 hex, id сервисной сети; пусто — ZT не настроен.</param>
/// <param name="Seq">unix-мс издателя, строго растёт.</param>
/// <param name="IssuedAt">Справочно; аренду считает сервис по своим часам.</param>
/// <param name="Takeover">Разрешить перехват живой аренды другого hub.</param>
public sealed record HubRecord(
    string HubId,
    string HubName,
    IReadOnlyList<HubEndpoint> Endpoints,
    string? ZtNetwork,
    long Seq,
    DateTimeOffset IssuedAt,
    bool Takeover = false)
{
    public string Kind { get; init; } = SignpostKinds.Hub;
}

/// <summary>«Какой релиз агента актуален». Подписывает build-dist -Publish.</summary>
/// <param name="Version">Как version.txt (git sha / -dirty).</param>
/// <param name="BuildTime">Порядок релизов: клиент не ставит релиз старее установленного.</param>
/// <param name="PackageSha256">hex sha256 package.zip.</param>
/// <param name="PackageUrls">Адреса пакета; относительные резолвятся от адреса указателя.</param>
public sealed record ReleaseRecord(
    string Version,
    DateTimeOffset BuildTime,
    string PackageSha256,
    long PackageSize,
    IReadOnlyList<string> PackageUrls,
    long Seq)
{
    public string Kind { get; init; } = SignpostKinds.Release;
}

public static class SignpostKinds
{
    public const string Hub = "hub";
    public const string Release = "release";
}

/// <summary>Маршруты сервиса указателя — единый источник для сервиса, hub и клиентов.</summary>
public static class SignpostRoutes
{
    public const string Signpost = "/signpost";
    public const string Hub = "/signpost/hub";
    public const string Release = "/signpost/release";
    public const string Package = "/package/{sha256}";
    public const string Health = "/healthz";

    /// <summary>Заголовок загрузки пакета: base64 подписанного конверта релиза на этот же sha256.
    /// Без него сервис пакет не примет — иначе любой забил бы volume и вытеснил текущий релиз.</summary>
    public const string ReleaseHeader = "X-SzDiag-Release";

    public static string PackageFor(string sha256) => $"/package/{sha256}";
}
