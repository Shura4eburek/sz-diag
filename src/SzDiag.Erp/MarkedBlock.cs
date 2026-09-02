namespace SzDiag.Erp;

/// <summary>
/// Вкладывает генерируемый блок в заметку между маркерами, не трогая остальной текст.
/// Заметку по заявке человек дополняет руками по ходу ремонта, поэтому повторный фетч
/// обязан переписывать только свой блок.
/// </summary>
public static class MarkedBlock
{
    public const string Begin = "<!-- erp:початок -->";
    public const string End = "<!-- erp:кінець -->";

    /// <summary>
    /// Маркеры найдены — содержимое между ними заменяется. Не найдены (или найден только
    /// открывающий: человек снёс половину) — блок дописывается в конец, ничего не съедая.
    /// </summary>
    public static string Upsert(string text, string block)
    {
        var body = $"{Begin}\n{block.Trim()}\n{End}";

        var start = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        if (start >= 0 && end > start)
            return text[..start] + body + text[(end + End.Length)..];

        var separator = text.Length == 0 || text.EndsWith('\n') ? "" : "\n";
        return $"{text}{separator}\n{body}\n";
    }
}
