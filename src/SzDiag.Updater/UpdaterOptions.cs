namespace SzDiag.Updater;

/// <summary>Конфиг апдейтера. Читается из appsettings.json рядом с exe — те же поля,
/// что у агента (общий файл на клиенте).</summary>
public sealed class UpdaterOptions
{
    /// <summary>Адрес hub. Пусто — автообнаружение по UDP (HubDiscovery).</summary>
    public string HubUrl { get; set; } = "";
    public string AgentToken { get; set; } = "";

    /// <summary>Свой указатель оператора (Railway): адрес hub и подписанный релиз, когда hub не найден.</summary>
    public string SignpostUrl { get; set; } = "";

    /// <summary>Публичный ключ флота (SPKI PEM). Пусто — берётся fleet-sign.pub рядом с exe.
    /// Только с диска: ключ из проверяемого пакета проверкой не был бы.</summary>
    public string SignPublicKey { get; set; } = "";

    /// <summary>Ключ флота: из конфига или из fleet-sign.pub, лежащего рядом с exe до обновления.</summary>
    public IReadOnlyList<string> ResolveSignKeys(string baseDir)
        => SzDiag.Contracts.Signing.FleetPublicKey.Resolve(SignPublicKey, baseDir);
}
