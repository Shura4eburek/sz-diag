namespace SzDiag.Contracts;

/// <summary>Зафиксированный hub'ом вырубон/перезагрузка клиента.
///
/// Появилось после случая, когда машина на нашем же стенде **вырубилась сама 30.07 в 16:15**
/// (аптайм 53 часа, нагрузки не было) — и это заметили через неделю, случайно. В kb всё это
/// время висело «на стенді дефект НЕ відтворено», прогоны планировались от ложной посылки,
/// а клиенту чуть не ушло «не выявлено» (бэклог п.55).
///
/// Единственный надёжный признак — **смена boot-time**: пропажа heartbeat под нагрузкой
/// ребутом не является, а ICMP у типовой клиентской винды закрыт из коробки и как признак
/// живости не годится вообще (п.42).</summary>
/// <param name="At">Когда hub увидел новый boot-time (не сам момент отключения питания).</param>
/// <param name="PreviousBootTime">Boot-time до ребута — null, если агент старой сборки.</param>
/// <param name="NewBootTime">Boot-time после ребута.</param>
/// <param name="UptimeBeforeSeconds">Сколько машина продержалась до вырубона.</param>
/// <param name="ActivityBefore">Чем была занята (шёл ли стресс-прогон) — важнее всего,
/// потому что «продержалась N минут под тестом» и есть измерение времени до отказа.</param>
/// <param name="Kind">Чем оказался ребут по журналу клиента (<see cref="ShutdownKind"/>):
/// обрыв питания, кнопка, BSOD или штатное выключение. Смена boot-time сама по себе
/// вырубоном не является — на 161312 два «аварийных выключения» из пяти были кнопкой
/// (бэклог п.93).</param>
/// <param name="Source">Откуда узнали: <see cref="RebootSource.Heartbeat"/> — hub сам увидел
/// смену boot-time; <see cref="RebootSource.Journal"/> — агент принёс запись из журнала
/// клиента. Всё, что случилось до подключения агента, hub не видел вовсе, и `reboots` уверенно
/// печатал «вырубонов не зафиксировано» там, где они были (бэклог п.97).</param>
public sealed record RebootEvent(
    string Sz,
    DateTimeOffset At,
    DateTimeOffset? PreviousBootTime,
    DateTimeOffset? NewBootTime,
    long? UptimeBeforeSeconds,
    string? ActivityBefore,
    string? Kind = null,
    string Source = RebootSource.Heartbeat)
{
    public TimeSpan? UptimeBefore =>
        UptimeBeforeSeconds is { } s ? TimeSpan.FromSeconds(s) : null;

    /// <summary>Считается ли этот ребут отказом (для счётчика и сводки).</summary>
    public bool IsFailure => ShutdownKind.CountsAsFailure(Kind);
}

/// <summary>Откуда событие известно.</summary>
public static class RebootSource
{
    /// <summary>Hub сам увидел смену boot-time при живом агенте.</summary>
    public const string Heartbeat = "heartbeat";

    /// <summary>Запись из журнала клиента (Kernel-Power 41 / EventLog 6008), принесённая
    /// агентом при подключении. Покрывает время, когда агента не было.</summary>
    public const string Journal = "journal";
}

/// <summary>Окно, в котором с машиной работали руками: гасили рубильником, перетыкали стенд,
/// выключали на ночь. Событие питания внутри такого окна — не дефект.
///
/// Боль (бэклог п.100, СЗ 160636): hard-off 05.08 16:00:58 в простое переворачивал тактику
/// («машина падает без нагрузки»), но в той же заявке питание дважды снимали руками, и было
/// ли это третьим разом — нигде не записано. Событие так и осталось неопределённым: либо
/// ключевая улика, либо шум.</summary>
public sealed record MaintenanceWindow(
    string Sz,
    DateTimeOffset From,
    DateTimeOffset Until,
    string Reason)
{
    public bool Covers(DateTimeOffset at) => at >= From && at <= Until;

    public bool IsActive(DateTimeOffset now) => Covers(now);
}

/// <summary>Событие питания из журнала клиента — то, что агент приносит hub при регистрации.</summary>
/// <param name="Kind">Классификация по полям события (<see cref="ShutdownKind"/>).</param>
public sealed record PowerEvent(DateTimeOffset At, string Kind);

/// <summary>Пачка событий из журнала клиента.</summary>
public sealed record PowerEventsReport(string Sz, IReadOnlyList<PowerEvent> Events);

/// <summary>Таймлайн вырубонов по СЗ + сводка, которую нельзя не заметить при закрытии.</summary>
/// <param name="MaxUptimeSeconds">Максимальный зафиксированный аптайм между вырубонами —
/// ответ на вопрос «сколько машина вообще выдерживает».</param>
/// <param name="WatchingSince">С какого момента СЗ вообще под наблюдением hub. Без этой даты
/// «вырубонов не зафиксировано» читается как «их не было», хотя может значить «мы не
/// смотрели» (бэклог п.97).</param>
public sealed record RebootTimeline(
    string Sz,
    IReadOnlyList<RebootEvent> Events,
    long? MaxUptimeSeconds,
    DateTimeOffset? WatchingSince = null)
{
    public int Count => Events.Count;

    public TimeSpan? MaxUptime =>
        MaxUptimeSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
}
