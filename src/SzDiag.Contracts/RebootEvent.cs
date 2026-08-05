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
public sealed record RebootEvent(
    string Sz,
    DateTimeOffset At,
    DateTimeOffset? PreviousBootTime,
    DateTimeOffset? NewBootTime,
    long? UptimeBeforeSeconds,
    string? ActivityBefore)
{
    public TimeSpan? UptimeBefore =>
        UptimeBeforeSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
}

/// <summary>Таймлайн вырубонов по СЗ + сводка, которую нельзя не заметить при закрытии.</summary>
/// <param name="MaxUptimeSeconds">Максимальный зафиксированный аптайм между вырубонами —
/// ответ на вопрос «сколько машина вообще выдерживает».</param>
public sealed record RebootTimeline(
    string Sz,
    IReadOnlyList<RebootEvent> Events,
    long? MaxUptimeSeconds)
{
    public int Count => Events.Count;

    public TimeSpan? MaxUptime =>
        MaxUptimeSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
}
