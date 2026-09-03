namespace SzDiag.Contracts;

/// <summary>Hub → агент: выполнить PowerShell-скрипт локально на клиенте и вернуть вывод.
/// Замена SSH для сбора данных: агент работает под своими правами, без ConPTY и без
/// network-токена, и остаётся доступен даже когда sshd задушен нагрузкой.</summary>
/// <param name="RequestId">Идентификатор для сопоставления ответа с ожидающим запросом.</param>
/// <param name="TimeoutSeconds">Сколько ждать скрипт; по истечении процесс убивается.</param>
/// <param name="Detached">Запустить в фоне и сразу вернуть JobId: под полной нагрузкой
/// синхронный exec не проходит вообще (на 160636 три попытки подряд в таймаут при живом
/// heartbeat), а долгий exec держит канал и не даёт подсмотреть прогресс — бэклог п.43/п.46.</param>
/// <param name="Isolated">Только вместе с <paramref name="Detached"/>: обернуть задачу в
/// транзиентную scheduled task под SYSTEM (как sshd), а не в дочерний процесс агента. Дерево
/// процессов (TM5, OCCT) переживает падение/закрытие агента — на живой заявке TM5 пропал
/// вместе с упавшим агентом, не досчитав ни одного цикла (бэклог п.53).</param>
public sealed record ExecRequest(string Sz, string RequestId, string Script, int TimeoutSeconds,
    bool Detached = false, bool Isolated = false);

/// <summary>Агент → hub: «команду принял, выполняю». Отправляется СРАЗУ по получении, до
/// запуска скрипта. Без этого «агент не принял команду» и «принял, но не успел ответить»
/// выглядят одинаково — глухим таймаутом (бэклог п.35/п.43).</summary>
public sealed record ExecAck(string RequestId, DateTimeOffset AcceptedAt);

/// <summary>Hub → агент: как там фоновая задача (и отдай хвост вывода).
/// Этот же короткий канал везёт отмену и список задач: он единственный, который
/// проверенно проходит под полной нагрузкой (бэклог п.134/172/176).</summary>
/// <param name="JobId">Идентификатор задачи; «*» — вернуть список всех задач.</param>
/// <param name="Cancel">Снять задачу (убить дерево процессов) перед ответом.</param>
public sealed record ExecStatusRequest(string Sz, string RequestId, string JobId, int TailLines = 50,
    bool Cancel = false);

/// <summary>Агент → hub: состояние фоновой задачи.</summary>
/// <param name="Running">Ещё выполняется.</param>
/// <param name="Tail">Последние строки вывода — «шо там» во время часового прогона.</param>
/// <param name="Cancelled">Задача была снята по запросу (Cancel в ExecStatusRequest).</param>
public sealed record ExecJobStatus(
    string RequestId,
    string JobId,
    bool Running,
    int? ExitCode,
    string Tail,
    DateTimeOffset StartedAt,
    long OutputBytes,
    string? Error = null,
    bool Cancelled = false);

/// <summary>Агент → hub: результат выполнения <see cref="ExecRequest"/>.</summary>
/// <param name="TimedOut">Скрипт не уложился в таймаут и был убит.</param>
/// <param name="Truncated">Вывод превысил лимит и обрезан (см. ExecLimits.MaxOutputChars).</param>
public sealed record ExecResult(
    string RequestId,
    int ExitCode,
    string StdOut,
    string StdErr,
    bool TimedOut = false,
    bool Truncated = false,
    /// <summary>Идентификатор фоновой задачи — заполнен только для Detached-запуска.</summary>
    string? JobId = null);

/// <summary>Тело HTTP-запроса CLI → hub: что выполнить на агенте.</summary>
public sealed record ExecCommandRequest(string Script, int? TimeoutSeconds = null,
    bool Detached = false, bool Isolated = false);

/// <summary>Общие лимиты exec — одинаковые на агенте и hub, чтобы ожидания совпадали.</summary>
public static class ExecLimits
{
    /// <summary>Потолок вывода в одном ответе. Выше — обрезаем: SignalR-сообщение ограничено
    /// (10 МБ), а тащить в консоль мегабайты смысла нет — для больших выгрузок есть отчёты.</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>Таймаут скрипта по умолчанию.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Дефолтный таймаут, когда hub видит по `Activity` СЗ, что на клиенте прямо
    /// сейчас идёт стресс-прогон: под OCCT/TM5 запуск дочернего powershell.exe сам по себе
    /// занимает десятки секунд (бэклог п.35a, СЗ 161288) — 120с дефолта не хватает и на
    /// честный «жив, но медленный» ответ.</summary>
    public const int StressDefaultTimeoutSeconds = 300;

    /// <summary>Запас поверх таймаута скрипта, в течение которого hub ещё ждёт ответ агента
    /// (сеть + запуск процесса). Без него hub сдавался бы ровно тогда, когда агент только-только
    /// убил скрипт и собирается прислать результат.</summary>
    public const int HubGraceSeconds = 20;

    /// <summary>Сколько ждать ack приёма команды. Не пришёл — агент до команды не добрался
    /// (канал/сеть), а не «скрипт долго идёт»: это разные диагнозы.</summary>
    public const int AckSeconds = 30;

    /// <summary>Строк хвоста фоновой задачи по умолчанию.</summary>
    public const int DefaultTailLines = 50;
}
