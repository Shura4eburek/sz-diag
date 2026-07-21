namespace SzDiag.Updater;

/// <summary>Конфиг апдейтера. Читается из appsettings.json рядом с exe — те же поля,
/// что у агента (общий файл на клиенте).</summary>
public sealed class UpdaterOptions
{
    /// <summary>Адрес hub. Пусто — автообнаружение по UDP (HubDiscovery).</summary>
    public string HubUrl { get; set; } = "";
    public string AgentToken { get; set; } = "";
}
