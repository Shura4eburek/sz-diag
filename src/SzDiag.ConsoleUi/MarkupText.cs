using System.Text;
using System.Text.RegularExpressions;

namespace SzDiag.ConsoleUi;

/// <summary>
/// Работа с длиной и обрезкой строк со Spectre-разметкой. Панель рисует строку как есть,
/// без переноса, поэтому поставщики статуса обязаны укладываться в заданную ширину —
/// считать её надо по видимому тексту, а не по длине строки с тегами.
/// </summary>
public static class MarkupText
{
    private static readonly Regex TagPattern = new(@"\[/?[^\]]*\]", RegexOptions.Compiled);

    // Плейсхолдеры под экранированные скобки: снимаем их до разбора тегов, иначе
    // «[[C]]» будет разобрано как тег и превратится в «]».
    private const char OpenPlaceholder = '\u0001';
    private const char ClosePlaceholder = '\u0002';

    /// <summary>Видимый текст без разметки (экранированные скобки развёрнуты).</summary>
    public static string Plain(string markup)
    {
        var masked = markup.Replace("[[", OpenPlaceholder.ToString())
                           .Replace("]]", ClosePlaceholder.ToString());
        var stripped = TagPattern.Replace(masked, "");
        return stripped.Replace(OpenPlaceholder, '[').Replace(ClosePlaceholder, ']');
    }

    /// <summary>Длина видимого текста.</summary>
    public static int PlainLength(string markup) => Plain(markup).Length;

    /// <summary>
    /// Режет видимый текст до width, не разрывая теги. Обрезка посреди размеченного
    /// куска оставляет теги открытыми, а Spectre на незакрытом теге бросает исключение
    /// (проверено тестом) — поэтому недостающие «[/]» дописываются в конец.
    /// </summary>
    public static string Fit(string markup, int width)
    {
        if (width <= 0) return "";
        if (PlainLength(markup) <= width) return markup;

        var result = new StringBuilder();
        var visible = 0;
        var openTags = 0;
        var i = 0;
        while (i < markup.Length)
        {
            // Экранированная скобка — один видимый символ, двигаемся на два.
            if (i + 1 < markup.Length &&
                ((markup[i] == '[' && markup[i + 1] == '[') || (markup[i] == ']' && markup[i + 1] == ']')))
            {
                if (visible >= width) break;
                result.Append(markup[i]).Append(markup[i + 1]);
                visible++;
                i += 2;
                continue;
            }

            // Тег — копируем целиком, ширину не тратит.
            if (markup[i] == '[')
            {
                var close = markup.IndexOf(']', i);
                if (close < 0) break;
                var inner = markup.AsSpan(i + 1, close - i - 1);
                if (inner.Length > 0 && inner[0] == '/') openTags = Math.Max(0, openTags - 1);
                else openTags++;
                result.Append(markup, i, close - i + 1);
                i = close + 1;
                continue;
            }

            if (visible >= width) break;
            result.Append(markup[i]);
            visible++;
            i++;
        }

        for (var t = 0; t < openTags; t++) result.Append("[/]");
        return result.ToString();
    }
}
