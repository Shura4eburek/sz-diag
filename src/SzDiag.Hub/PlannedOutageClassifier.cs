namespace SzDiag.Hub;

/// <summary>Отличает плановое обесточивание сервиса (рубильник на ночь, свет пропал у всех
/// разом) от настоящего дефекта одной машины (бэклог п.130, СЗ 161346): утренний ребут вне
/// рабочих часов на 8,5 часа нагрузки подмешивал в счётчик ⚡ рубильник, хотя счётчик вырубонов
/// был главным измеряемым результатом заявки (25 событий у клиента против 0 у нас).</summary>
public static class PlannedOutageClassifier
{
    /// <summary>Признак 1: событие случилось вне рабочих часов сервиса. Оба конца окна
    /// должны быть заданы — иначе рубильник считается выключенным (не мешаем старым
    /// конфигам без `Hub.ServiceHours*`).</summary>
    public static bool IsOutsideServiceHours(TimeOnly at, TimeOnly? start, TimeOnly? end)
    {
        if (start is null || end is null) return false;
        var s = start.Value;
        var e = end.Value;
        var within = s <= e ? (at >= s && at <= e) : (at >= s || at <= e); // окно может идти через полночь
        return !within;
    }

    /// <summary>Итоговое решение: вне рабочих часов ИЛИ пропажа heartbeat случилась массово
    /// (сразу у нескольких СЗ) — признак 2, не зависящий от расписания (свет пропал реально,
    /// а не «просто наша машина сдохла»).</summary>
    public static bool IsPlanned(TimeOnly at, TimeOnly? start, TimeOnly? end, bool massOffline)
        => IsOutsideServiceHours(at, start, end) || massOffline;

    public static TimeOnly? ParseTimeOfDay(string? text)
        => TimeOnly.TryParse(text, out var t) ? t : null;
}
