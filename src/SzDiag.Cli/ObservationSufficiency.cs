namespace SzDiag.Cli;

/// <summary>Сравнивает, сколько СЗ реально была под наблюдением, с характерным интервалом
/// между отказами по её же истории (бэклог п.159, СЗ 160306): закрыли через 18 минут при
/// характерном интервале ~53 часа, и `close` промолчал, хотя все данные для сравнения уже
/// лежали в SQLite. Разница между «машина выстояла» и «машину не успели поспостерігати» —
/// и есть вердикт заявки.</summary>
public static class ObservationSufficiency
{
    /// <summary>null — наблюдения достаточно (или сравнивать не с чем — истории отказов ещё
    /// нет). Иначе — готовое предупреждение для печати.</summary>
    public static string? Warn(TimeSpan observed, TimeSpan? characteristicInterval)
    {
        if (characteristicInterval is not { } characteristic || characteristic <= TimeSpan.Zero) return null;
        if (observed >= characteristic) return null;

        // Консольный вывод CLI — по-русски (CLAUDE.md; украинский только в kb/журнале СЗ),
        // review W2 I-5: строка раньше уезжала на украинский вместе с остальным блоком close.
        return $"⚠ наблюдение {Format(observed)} при характерном интервале {Format(characteristic)}: " +
               "замена НЕ подтверждена — это закрытие по решению мастера, а не по результату.";
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} ч" : $"{t.TotalMinutes:0} мин";
}
