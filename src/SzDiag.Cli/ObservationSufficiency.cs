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

        return $"⚠ спостереження {Format(observed)} при характерному інтервалі {Format(characteristic)}: " +
               "заміну НЕ підтверджено — це закриття за рішенням майстра, а не за результатом.";
    }

    private static string Format(TimeSpan t)
        => t.TotalHours >= 1 ? $"{t.TotalHours:0.#} год" : $"{t.TotalMinutes:0} хв";
}
