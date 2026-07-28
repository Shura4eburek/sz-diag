namespace SzDiag.Contracts;

/// <summary>Hub → агент: выполнить PowerShell-скрипт локально на клиенте и вернуть вывод.
/// Замена SSH для сбора данных: агент работает под своими правами, без ConPTY и без
/// network-токена, и остаётся доступен даже когда sshd задушен нагрузкой.</summary>
/// <param name="RequestId">Идентификатор для сопоставления ответа с ожидающим запросом.</param>
/// <param name="TimeoutSeconds">Сколько ждать скрипт; по истечении процесс убивается.</param>
public sealed record ExecRequest(string Sz, string RequestId, string Script, int TimeoutSeconds);

/// <summary>Агент → hub: результат выполнения <see cref="ExecRequest"/>.</summary>
/// <param name="TimedOut">Скрипт не уложился в таймаут и был убит.</param>
/// <param name="Truncated">Вывод превысил лимит и обрезан (см. ExecLimits.MaxOutputChars).</param>
public sealed record ExecResult(
    string RequestId,
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut = false,
    bool Truncated = false);

/// <summary>Тело HTTP-запроса CLI → hub: что выполнить на агенте.</summary>
public sealed record ExecCommandRequest(string Script, int? TimeoutSeconds = null);

/// <summary>Общие лимиты exec — одинаковые на агенте и hub, чтобы ожидания совпадали.</summary>
public static class ExecLimits
{
    /// <summary>Потолок вывода в одном ответе. Выше — обрезаем: SignalR-сообщение ограничено
    /// (10 МБ), а тащить в консоль мегабайты смысла нет — для больших выгрузок есть отчёты.</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>Таймаут скрипта по умолчанию.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Запас поверх таймаута скрипта, в течение которого hub ещё ждёт ответ агента
    /// (сеть + запуск процесса). Без него hub сдавался бы ровно тогда, когда агент только-только
    /// убил скрипт и собирается прислать результат.</summary>
    public const int HubGraceSeconds = 20;
}
