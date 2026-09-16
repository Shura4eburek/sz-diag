using Microsoft.AspNetCore.SignalR.Client;

namespace SzDiag.Agent;

/// <summary>
/// Политика переподключения агента к hub: попытки **не кончаются никогда**.
///
/// Дефолтная `WithAutomaticReconnect()` даёт ровно четыре попытки (0/2/10/30 с) и после них
/// закрывает соединение навсегда. На 162003 (бэклог п.280) этого хватило, чтобы обычный
/// перезапуск hub (остановка → пересборка → старт, несколько минут) насовсем увёл живую
/// машину из сессий: клиент был жив, но больше не стучался, и вернуть его можно было только
/// ребутом машины или руками на месте. Простой хаба — штатное событие, поэтому агент обязан
/// его пересиживать, сколько бы тот ни лежал.
/// </summary>
public sealed class InfiniteRetryPolicy : IRetryPolicy
{
    /// <summary>Потолок паузы: дальше растить смысла нет, а возврат сессии задержит.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    private static readonly int[] StepsSeconds = { 0, 2, 5, 10, 20 };

    private readonly Random _jitter = new();

    /// <summary>Пауза перед попыткой номер <paramref name="previousRetryCount"/> (с нуля):
    /// 0 → 2 → 5 → 10 → 20 → 30 → 30 → … Чистая функция — джиттер накладывается отдельно,
    /// чтобы шаги можно было проверить тестом.</summary>
    public static TimeSpan DelayFor(int previousRetryCount)
    {
        if (previousRetryCount < 0) return TimeSpan.Zero;
        return previousRetryCount < StepsSeconds.Length
            ? TimeSpan.FromSeconds(StepsSeconds[previousRetryCount])
            : MaxDelay;
    }

    /// <summary>±20% к паузе: после рестарта hub несколько агентов иначе ломятся в одну
    /// секунду — ровно тогда, когда он ещё поднимается.</summary>
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        var baseDelay = DelayFor((int)retryContext.PreviousRetryCount);
        if (baseDelay == TimeSpan.Zero) return baseDelay;

        var factor = 0.8 + _jitter.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * factor);
    }
}
