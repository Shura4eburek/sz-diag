namespace SzDiag.Contracts;

/// <summary>Именованные профили расписания OCCT — <c>szcli test run --schedule &lt;имя&gt;</c>
/// (бэклог п.124/#60): раньше длину прогона выбирали, ПОДМЕНИВ файл на клиенте руками
/// (рецепт <c>set-occt-schedule.ps1</c> с сохранением <c>.orig</c>), а раздача (`Hub.ToolsRoot`)
/// молча расходилась с тем, что лежит в репозитории (5+5 минут вместо заявленных 90+90 на
/// СЗ 161346 — 26 минут ожидания и «дефект не воспроизводится» по машине, которую грузили
/// 10 минут). Файлы — те же, что кладёт <c>build-dist</c> в <c>deploy/occt/*.json</c>.</summary>
public static class OcctScheduleProfiles
{
    /// <summary>Имя файла по умолчанию — то, что зашито в <c>testsuite.json</c> исторически.</summary>
    public const string Default = "schedule.json";

    /// <summary>Профиль → имя файла в <c>deploy/occt/</c> и в <c>Hub.ToolsRoot/occt/</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Files =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["smoke"] = "schedule-smoke.json",
            ["long"] = "schedule-long.json",
            ["infinite"] = "schedule-infinite.json",
        };

    /// <summary>Известные имена профилей плюс синонимы дефолта, для usage/валидации в CLI.</summary>
    public static IEnumerable<string> KnownProfiles => new[] { "default" }.Concat(Files.Keys);

    /// <summary>Имя файла расписания по профилю. null/пусто/"default" → <see cref="Default"/>;
    /// незнакомое имя → null (вызывающий решает — упасть на CLI при валидации или откатиться
    /// на дефолт молча на агенте, если профиль всё же долетел незнакомым).</summary>
    public static string? ResolveFileName(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) || profile.Equals("default", StringComparison.OrdinalIgnoreCase))
            return Default;
        return Files.TryGetValue(profile, out var file) ? file : null;
    }
}
