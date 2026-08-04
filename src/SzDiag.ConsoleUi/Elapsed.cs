namespace SzDiag.ConsoleUi;

/// <summary>Человекочитаемая длительность: «44сек» / «5мин 44сек» / «1ч 05мин».</summary>
public static class Elapsed
{
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        var total = (int)t.TotalSeconds;
        if (total < 60) return $"{total}сек";
        if (total < 3600) return $"{total / 60}мин {total % 60:D2}сек";
        return $"{total / 3600}ч {(total % 3600) / 60:D2}мин";
    }
}
