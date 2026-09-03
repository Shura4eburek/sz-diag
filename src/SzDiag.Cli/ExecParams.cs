using System.Text.RegularExpressions;

namespace SzDiag.Cli;

/// <summary>`szcli exec <СЗ> -f <файл> --param Key=Value` — параметризация рецептов без правки
/// файла в рабочем дереве.
///
/// Почти каждый рецепт начинается с плейсхолдера вида `$Sz = '000000'` (комментарий рядом
/// поясняет, что это номер живой заявки). На 161716 это выливалось в правку файла репозитория
/// перед каждым прогоном (`start-ycruncher.ps1`, `stress-transient.ps1`) — а после прогона
/// про откат плейсхолдера легко забыть, и в гите временно лежит рецепт с номером живой СЗ
/// (бэклог п.155).
///
/// `param()` тут не годится: скрипт уезжает на агента текстом (см. `PowerShellRunner`), а не
/// файлом, поэтому позиционные/именованные параметры функции недоступны. Вместо этого —
/// текстовая подстановка: строка вида `$Key = <что угодно>` в начале скрипта комментируется, а
/// сверху скрипта добавляется `$Key = 'Value'` с нужным значением. Оригинальный файл в
/// репозитории не трогаем — подстановка идёт только над копией текста, отправляемой на
/// агента.</summary>
public static class ExecParams
{
    /// <summary>Разбор повторяющихся `--param Key=Value` из аргументов командной строки.
    /// Чистая функция — тестируется без сети/файлов.</summary>
    public static IReadOnlyList<(string Key, string Value)> ParseArgs(IReadOnlyList<string> args)
    {
        var result = new List<(string, string)>();
        for (var i = 0; i < args.Count; i++)
        {
            if (!args[i].Equals("--param", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Count) continue;
            var raw = args[i + 1];
            var eq = raw.IndexOf('=');
            if (eq <= 0) continue;   // без имени параметра подставлять нечего
            result.Add((raw[..eq], raw[(eq + 1)..]));
        }
        return result;
    }

    /// <summary>Накладывает параметры на текст скрипта: гасит существующее присваивание
    /// `$Key = ...` (если есть) и подставляет своё значение сверху. Порядок в
    /// <paramref name="parameters"/> не важен — все применяются к одному и тому же тексту.</summary>
    public static string Apply(string script, IReadOnlyList<(string Key, string Value)> parameters)
    {
        if (parameters.Count == 0) return script;

        var text = script ?? "";
        var overrideLines = new List<string>();

        foreach (var (key, value) in parameters)
        {
            // Только простое присваивание `$Key = ...` в начале строки — не трогаем
            // `$KeyOther`, обращения к свойствам (`$Key.Foo =`) или использование внутри строки.
            var pattern = new Regex(@"^(\s*)\$" + Regex.Escape(key) + @"\s*=(?!=).*$",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            text = pattern.Replace(text, m => $"{m.Groups[1].Value}# --param перекрыл значение по умолчанию: {m.Value.Trim()}");

            var escaped = value.Replace("'", "''");
            overrideLines.Add($"${key} = '{escaped}'");
        }

        var preamble = "# --- szcli exec --param: подставлено поверх значений по умолчанию из рецепта ---\n"
                       + string.Join('\n', overrideLines) + "\n"
                       + "# --- конец подстановки ---\n";
        return preamble + text;
    }

    /// <summary>`szcli exec <СЗ> ...` знает номер СЗ и так — подставляет его в <c>$Sz</c>
    /// сам, если пользователь не задал `--param Sz=` явно (тогда явное значение важнее).</summary>
    public static IReadOnlyList<(string Key, string Value)> WithAutoSz(
        IReadOnlyList<(string Key, string Value)> parameters, string sz)
    {
        if (parameters.Any(p => p.Key.Equals("Sz", StringComparison.OrdinalIgnoreCase)))
            return parameters;
        return parameters.Append(("Sz", sz)).ToList();
    }
}
