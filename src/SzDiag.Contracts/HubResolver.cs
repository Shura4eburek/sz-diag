namespace SzDiag.Contracts;

/// <summary>Порядок поиска hub: сначала постоянный домен из конфига, при неудаче —
/// UDP-broadcast по локалке.
///
/// Broadcast не выбрасывается вместе с переездом на домен: он остаётся рабочим путём, когда
/// интернета нет, а машина стоит в одной сети с боксом. Он же и признак прямого режима —
/// нашлись broadcast'ом, значит подключаться к клиенту надо напрямую, без туннеля.</summary>
public static class HubResolver
{
    /// <param name="configuredUrl">`HubUrl` из конфига: постоянный домен hub. Пусто — сразу
    /// в broadcast (прежнее поведение).</param>
    /// <param name="broadcast">Поиск по локалке (<see cref="HubDiscovery.FindHubAsync"/>).</param>
    /// <param name="probe">Проверка доступности адреса. По умолчанию считаем доступным:
    /// реальную проверку делает уже само подключение, лишний round-trip на старте агента
    /// не нужен.</param>
    /// <returns>Адрес hub и признак того, что он найден broadcast'ом.</returns>
    public static async Task<(string Url, bool FoundByBroadcast)> ResolveAsync(
        string? configuredUrl, Func<Task<string>> broadcast,
        Func<string, Task<bool>>? probe = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredUrl))
        {
            var alive = probe is null || await probe(configuredUrl);
            if (alive) return (configuredUrl, false);
        }

        return (await broadcast(), true);
    }
}
