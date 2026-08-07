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

    /// <summary>Предупреждения по тексту скрипта. Пустой список — подозрительного не нашли.</summary>
    public static IReadOnlyList<string> Check(string script)
    {
        var warnings = new List<string>();
        var text = script ?? "";
        var dangerous = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            var match = Concat.Match(line);
            if (!match.Success) continue;
            // Запятая рядом с конкатенацией = элемент списка. Без неё «+» ничего не ломает.
            if (!line.Contains(',')) continue;
            // Скобка перед конкатенацией фиксирует приоритет — это правильный способ записи.
            var head = line[..match.Index];
            if (head.Count(c => c == '(') > head.Count(c => c == ')')) continue;

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

        return warnings;
    }
}
