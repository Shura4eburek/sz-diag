namespace SzDiag.Erp;

/// <summary>
/// Настройки доступа к локальному API учётной системы. Токен здесь НЕ хранится: он
/// генерируется заново при каждом запуске сервиса и читается из файла.
/// </summary>
public sealed class ErpOptions
{
    /// <summary>Слушает только localhost; порт задаётся конфигом, дефолта в коде нет.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Путь к файлу токена. Пусто — искать файл рядом с исполняемым.</summary>
    public string TokenFile { get; set; } = "";

    /// <summary>
    /// Минута из описания API — оптимизм: на живом прогоне обход дерева стоит 10+ с на
    /// раздел, и вызов не уложился в 180 с. Цена промаха — потерянный прогон и висящий
    /// захват, поэтому запас большой.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl);

    public string ResolveTokenFile() => string.IsNullOrWhiteSpace(TokenFile)
        ? Path.Combine(AppContext.BaseDirectory, "api_token")
        : TokenFile;
}
