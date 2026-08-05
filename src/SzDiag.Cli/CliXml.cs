using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SzDiag.Cli;

/// <summary>Разбор CLIXML — формата, в котором PowerShell отдаёт stderr при перенаправлённом
/// выводе. На экране это выглядит как
/// <c>#&lt; CLIXML &lt;Objs Version="1.1.0.1"...&gt;&lt;S S="Error"&gt;…_x000D__x000A_…</c> —
/// каша, в которой сам факт ошибки виден, только если вглядываться в маркеры <c>~~~~</c>.
/// Три захода подряд на живой СЗ ушли на то, чтобы разобрать в этой простыне «переменная
/// пропала» (бэклог п.28).</summary>
public static class CliXml
{
    private static readonly Regex Escape = new(@"_x([0-9A-Fa-f]{4})_", RegexOptions.Compiled);

    /// <summary>Похоже ли на CLIXML-простыню.</summary>
    public static bool Looks(string? text)
        => text is not null && text.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal);

    /// <summary>Приводит вывод к человеческому виду. Не CLIXML или сломанный XML —
    /// возвращаем как есть: молча съесть текст ошибки хуже, чем показать сырой.</summary>
    public static string Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!Looks(text)) return text;

        var xmlStart = text.IndexOf('<', text.IndexOf("CLIXML", StringComparison.Ordinal));
        if (xmlStart < 0) return text;

        try
        {
            var doc = XDocument.Parse(text[xmlStart..]);
            var lines = doc.Root!.Descendants()
                .Where(e => e.Name.LocalName is "S" or "T")   // S — строка, T — текст свойства
                .Select(e => Unescape(e.Value));
            var joined = string.Concat(lines).TrimEnd();
            return joined.Length == 0 ? text : joined;
        }
        catch (System.Xml.XmlException)
        {
            return text;
        }
    }

    /// <summary>Разворачивает <c>_xHHHH_</c> обратно в символы (перенос строки и пр.).
    /// Литеральное подчёркивание PowerShell экранирует как <c>_x005F_</c> — именно из-за
    /// этого <c>gpushark_x64.exe</c> приезжал как <c>gpushark_x005F_x64.exe</c> (п.24).</summary>
    public static string Unescape(string value)
        => Escape.Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>Предупреждение для inline-скрипта, если он почти наверняка доедет искажённым.
    /// Возвращает null, если всё в порядке.</summary>
    public static string? WarnAboutInline(string script)
    {
        var reasons = new List<string>();
        // `$` в двойных кавычках разворачивает ВЫЗЫВАЮЩИЙ шелл: `if (Test-Path $p)` уезжает
        // на агента как `if (Test-Path )` — CommandNotFoundException на ровном месте.
        if (script.Contains('$')) reasons.Add("$-переменные разворачивает твой шелл");
        if (script.Contains("_x", StringComparison.Ordinal)) reasons.Add("последовательности _x искажаются XML-эскейпом");
        if (script.Contains('`')) reasons.Add("бэктики съедаются при разборе строки");
        if (script.Contains('\n')) reasons.Add("многострочный скрипт");
        if (reasons.Count == 0) return null;

        var sb = new StringBuilder("inline-скрипт может доехать искажённым (");
        sb.Append(string.Join("; ", reasons));
        sb.Append("). Надёжнее: szcli exec <СЗ> -f script.ps1");
        return sb.ToString();
    }
}
