namespace SzDiag.Contracts.Fleet;

/// <summary>Единый набор секретов флота — один на все машины-hub. Генерируется один раз
/// (<c>tools\fleet.ps1 init</c>), хранится только зашифрованным паролем флота
/// (<see cref="FleetBundle"/>). Благодаря ему апдейтер из любой сборки принимается любым hub.</summary>
public sealed record FleetSecrets
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>PKCS#8 PEM ECDSA P-256 — подписывает записи указателя. Только hub/build-dist.</summary>
    public required string SignPrivateKey { get; init; }

    /// <summary>SPKI PEM — зашивается в апдейтер/агент и в env указателя.</summary>
    public required string SignPublicKey { get; init; }

    /// <summary>Токен агента (`X-SzDiag-Token`). Лежит и на клиентах — не секрет от заражённой машины.</summary>
    public required string AgentToken { get; init; }

    /// <summary>Токен management-API (CLI/Desk). На клиента не попадает.</summary>
    public required string ManagementToken { get; init; }

    /// <summary>OpenSSH ed25519, приватная часть — ключ, с которым hub ходит на клиента.</summary>
    public required string SvcDiagKey { get; init; }

    public required string SvcDiagKeyPub { get; init; }

    /// <summary>HTTPS-адрес прокси своего ZT-контроллера (сервис на Railway оператора, US3).
    /// Пусто, пока контроллер не заведён.</summary>
    public string ZtControllerUrl { get; init; } = "";

    /// <summary>Bearer прокси контроллера (он же `ZT_PROXY_TOKEN` сервиса). Только hub — на клиента
    /// не попадает никогда (FR-023).</summary>
    public string ZtControllerToken { get; init; } = "";

    /// <summary>Бэкап `identity.secret` контроллера: адрес контроллера = префикс ID сети, поэтому
    /// потеря volume без бэкапа = новая сеть и клиенты со старым указателем в неё не войдут.</summary>
    public string ZtIdentitySecret { get; init; } = "";

    /// <summary>Id сервисной ZT-сети, 16 hex. Пусто, пока сеть не заведена.</summary>
    public string ZtNetwork { get; init; } = "";

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
