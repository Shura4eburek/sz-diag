namespace SzDiag.Contracts;

/// <summary>Номер сервисной заявки. Валидация нужна не из педантизма: CLI брал под номер
/// **любой** аргумент и заводил под него полный скелет заметок в базе знаний. Так в vault
/// появились призраки `СЗ/--help`, `СЗ/111111`, `СЗ/123123` — 3 мусорные СЗ из 18 (бэклог п.57).
/// На горизонте сотен машин каждая опечатка оседает отдельной папкой, и отличить её от живой
/// заявки можно только руками.</summary>
public static class SzNumber
{
    /// <summary>Длина номера в цифрах — формат сервисных заявок (напр. 160705).</summary>
    public const int Digits = 6;

    /// <summary>Номер СЗ — ровно 6 цифр. Ни флагов (<c>--help</c>), ни букв, ни коротких
    /// «123», ни пробелов.</summary>
    public static bool IsValid(string? value)
        => value is { Length: Digits } && value.All(char.IsAsciiDigit);

    /// <summary>Человеческое объяснение, почему ввод не годится (для сообщения CLI).</summary>
    public static string Explain(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "номер СЗ не указан";
        if (value.StartsWith('-')) return $"«{value}» — это флаг, а не номер СЗ";
        if (!value.All(char.IsAsciiDigit)) return $"«{value}» — номер СЗ состоит только из цифр";
        return $"«{value}» — в номере СЗ должно быть ровно {Digits} цифр, а не {value.Length}";
    }
}
