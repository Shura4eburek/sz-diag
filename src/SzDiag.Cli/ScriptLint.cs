using System.Linq;
using System.Text.RegularExpressions;

namespace SzDiag.Cli;

/// <summary>Проверка скрипта перед отправкой на агента: ловим конструкции, которые PowerShell
/// поймёт не так, как задумал автор.
///
/// Боль (бэклог п.77, СЗ 161312): скрипт собирал .bat через массив строк, и элемент
/// <c>'cd /d "' + $fm + '"'</c> приехал на клиента **тремя отдельными строками** — `cmd` на
/// таком .bat падал, Task Scheduler показывал `LastTaskResult: 1`, и сорок минут искали
/// причину в Defender, правах и `/it`.
///
/// Транспорт тут ни при чём: в PowerShell **запятая связывает сильнее `+`**, поэтому
/// <c>@( 'a', 'b' + $x + 'c' )</c> разбирается как <c>('a','b') + $x + 'c'</c> — то есть
/// массив из четырёх элементов. Проверено вживую:
/// <code>@('@echo off', 'cd /d "' + $fm + '"', 'furmark.exe') → 5 элементов</code>
/// Лечится скобками вокруг выражения или here-string.</summary>
public static class ScriptLint
{
    /// <summary>Конкатенация строкового литерала с чем-то: `'…' + $x` или `$x + '…'`.</summary>
    private static readonly Regex Concat = new(
        @"(['""]\s*\+)|(\+\s*['""])", RegexOptions.Compiled);

    /// <summary>Использование `$PSScriptRoot` в теле скрипта.</summary>
    private static readonly Regex ScriptRoot = new(@"\$PSScriptRoot\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Содержимое одинарных/двойных строковых литералов — чтобы не путать запятую
    /// или `+`, которые оказались ВНУТРИ строки (напр. `-join ', '` или текст с запятой),
    /// с настоящим разделителем списка/конкатенацией снаружи строк (бэклог п.185, СЗ ловила
    /// ложные срабатывания на собственных рецептах: `'снято: ' + ($killed -join ', ')` и
    /// `"жив ($($kid.Name), pid=...)"` — запятая была частью текста, а не элементом массива).</summary>
    private static readonly Regex StringLiteral = new(
        @"'[^'\n]*'|""[^""\n]*""", RegexOptions.Compiled);

    private static readonly Regex FunctionDecl = new(
        @"function\s+([A-Za-z0-9_.-]+)\s*(\([^)]*\))?\s*\{", RegexOptions.Compiled);

    /// <summary>Прячет содержимое строковых литералов (оставляя пустые кавычки), чтобы
    /// эвристики ниже не путали пунктуацию ВНУТРИ текста с реальным синтаксисом скрипта.</summary>
    private static string MaskStringLiterals(string line) =>
        StringLiteral.Replace(line, m => m.Value.Length > 0 ? m.Value[0].ToString() + m.Value[^1] : m.Value);

    /// <summary>Глубина вложенности скобок `(){}[]` в префиксе строки до <paramref name="index"/>
    /// (не включая символ на этой позиции).</summary>
    private static int DepthAt(string line, int index)
    {
        var depth = 0;
        for (var i = 0; i < index && i < line.Length; i++)
        {
            var c = line[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
        }
        return depth;
    }

    /// <summary>Индекс ближайшей неЗакрытой открывающей скобки, охватывающей позицию
    /// <paramref name="index"/>, либо -1, если позиция на верхнем уровне (скобок нет вовсе).</summary>
    private static int FindEnclosingOpenIndex(string line, int index)
    {
        var stack = new Stack<int>();
        for (var i = 0; i < index && i < line.Length; i++)
        {
            var c = line[i];
            if (c is '(' or '{' or '[') stack.Push(i);
            else if (c is ')' or '}' or ']' && stack.Count > 0) stack.Pop();
        }
        return stack.Count > 0 ? stack.Peek() : -1;
    }

    /// <summary>`(` сразу после имени (без пробела) — синтаксис вызова метода/функции
    /// (`.Insert(`, `::Round(`, `CreateFileW(`), а не PowerShell-конструктор списка. У вызова
    /// каждый аргумент между запятыми — самостоятельное выражение с обычным приоритетом
    /// операторов, туда правило «запятая сильнее +» не относится (иначе `$out.Insert($idx,
    /// $k + '=' + $v)` и Win32-обёртки NVMe (`CreateFileW(@"…" + n, 0, …)`) ловились ложно —
    /// бэклог п.185).</summary>
    private static bool IsCallParen(string line, int openParenIndex) =>
        openParenIndex > 0 && line[openParenIndex] == '('
        && (char.IsLetterOrDigit(line[openParenIndex - 1]) || line[openParenIndex - 1] == '_');

    /// <summary>Есть ли на строке запятая на той же глубине вложенности скобок, что и найденная
    /// конкатенация (настоящий сосед по одному и тому же списку/массиву), при условии, что эта
    /// глубина не находится внутри безопасного вызова метода/функции.</summary>
    private static bool HasSiblingCommaAtSameDepth(string line, int matchIndex)
    {
        var enclosing = FindEnclosingOpenIndex(line, matchIndex);
        if (enclosing >= 0 && IsCallParen(line, enclosing)) return false;

        var matchDepth = DepthAt(line, matchIndex);
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == ',' && DepthAt(line, i) == matchDepth) return true;
        }
        return false;
    }

    /// <summary>Предупреждения по тексту скрипта. Пустой список — подозрительного не нашли.</summary>
    public static IReadOnlyList<string> Check(string script)
    {
        var warnings = new List<string>();
        var text = script ?? "";
        var dangerous = false;

        foreach (var raw in text.Split('\n'))
        {
            var rawLine = raw.Trim().TrimEnd('\r').Trim();
            if (rawLine.Length == 0) continue;

            // Маскируем текст ВНУТРИ строковых литералов — запятая или «+» там ничего
            // не ломает, это просто содержимое строки, а не синтаксис списка.
            var line = MaskStringLiterals(rawLine);

            var match = Concat.Match(line);
            if (!match.Success) continue;
            // Запятая рядом с конкатенацией = элемент списка. Без неё «+» ничего не ломает.
            if (!line.Contains(',')) continue;
            // Опасна только запятая на ТОМ ЖЕ уровне вложенности скобок, что и сама
            // конкатенация — это и значит «сосед по списку». Запятая внутри дополнительных
            // скобок — это уже аргумент оператора (-replace/-f/-join) или вызова функции,
            // а не элемент того же массива (бэклог п.185: `-replace 'a', 'b'`,
            // `-f $x, $y` и bare-аргументы внешней команды `--format=csv,noheader` ложно
            // ловились как «список развалится», хотя запятая жила в СВОЁМ, более глубоком
            // выражении).
            if (!HasSiblingCommaAtSameDepth(line, match.Index)) continue;

            dangerous = true;
            break;
        }

        if (dangerous)
        {
            warnings.Add(
                "в скрипте есть конкатенация строк внутри списка через запятую: в PowerShell "
                + "запятая связывает сильнее «+», поэтому 'a' + $x + 'b' в массиве развалится на "
                + "несколько элементов (бэклог п.77). Оберни выражение в скобки: ('a' + $x + 'b') "
                + "— или собери текст here-string @\"…\"@.");
        }

        CheckFunctionReturnPollution(text, warnings);

        // Агент гоняет скрипт временным файлом (бэклог п.231), поэтому $PSScriptRoot больше не
        // пустая строка и не роняет скрипт — но указывает на %TEMP%, а не на реальную папку
        // рецепта в репозитории. Обращение к соседним файлам через $PSScriptRoot молча найдёт
        // не то (или ничего) — нужен абсолютный путь или собственный фоллбэк, как в
        // cpuz-memory-snapshot.ps1.
        if (ScriptRoot.IsMatch(text))
        {
            warnings.Add(
                "скрипт использует $PSScriptRoot: на агенте это временная папка (%TEMP%), а НЕ "
                + "папка рецепта в репозитории — при exec скрипт уезжает файлом, но не на своём "
                + "исходном месте (бэклог п.231). Обращения к соседним файлам собирай абсолютным "
                + "путём или с явным фоллбэком, если $PSScriptRoot-путь не нашёлся.");
        }

        return warnings;
    }

    /// <summary>Бэклог п.195 (СЗ 161190): всё, что «голой» строкой попало в тело функции,
    /// входит в её возвращаемое значение вместе с $true/$false из if/return — непустой
    /// массив в булевом контексте истинен, и первый же промежуточный вывод объявляется
    /// вердиктом. Предупреждаем, только если результат функции реально где-то проверяется
    /// (`if (Имя …)`) или присваивается — иначе это неопасный побочный вывод.</summary>
    private static void CheckFunctionReturnPollution(string text, List<string> warnings)
    {
        foreach (Match decl in FunctionDecl.Matches(text))
        {
            var name = decl.Groups[1].Value;
            var braceIndex = decl.Index + decl.Length - 1; // позиция открывающей '{'
            var body = ExtractBalancedBody(text, braceIndex);
            if (body == null) continue;
            if (!HasBareStringStatement(body)) continue;

            var usage = new Regex(
                @"(if\s*\(\s*|=\s*)" + Regex.Escape(name) + @"\b", RegexOptions.Compiled);
            var usedAsValue = usage.Matches(text)
                .Cast<Match>()
                .Any(m => m.Index < decl.Index || m.Index >= decl.Index + decl.Length);
            if (!usedAsValue) continue;

            warnings.Add(
                $"функция «{name}» держит «голые» строки в теле — они попадут в её "
                + "возвращаемое значение вместе с $true/$false из if/return, и непустой "
                + "массив в булевом контексте всегда истинен (бэклог п.195). Замени вывод "
                + "статуса на $script:-переменную или Write-Host, а `return` оставь только "
                + "для итогового значения.");
            break;
        }
    }

    /// <summary>Вытаскивает тело `{ … }` начиная с открывающей скобки по индексу
    /// <paramref name="openBraceIndex"/> (наивный подсчёт глубины, без учёта строк/комментариев —
    /// для целей линтера этого достаточно).</summary>
    private static string? ExtractBalancedBody(string text, int openBraceIndex)
    {
        if (openBraceIndex < 0 || openBraceIndex >= text.Length || text[openBraceIndex] != '{')
            return null;

        var depth = 0;
        for (var i = openBraceIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
            }
        }

        return null;
    }

    /// <summary>Ищет в теле функции самостоятельный оператор, который является ЦЕЛИКОМ
    /// строковым литералом (не присвоен, не передан в Write-Host/return/throw и т.п.) —
    /// именно такие строки незаметно всасываются в возврат функции.</summary>
    private static bool HasBareStringStatement(string body)
    {
        foreach (var raw in body.Split(';', '\n'))
        {
            var stmt = raw.Trim().TrimEnd('\r').Trim();
            if (stmt.Length < 2) continue;

            var quote = stmt[0];
            if (quote != '\'' && quote != '"') continue;
            if (stmt[^1] != quote) continue;

            return true;
        }

        return false;
    }
}
