using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Выбор папки для доставляемых инструментов.
///
/// Правило «тулы кладём в `tools\` рядом с агентом» ломается, когда сам агент живёт в
/// синхронизируемой папке: на 160705 это был
/// <c>C:\Users\msi-pc\OneDrive\Desktop\Client-test\</c>, и OCCT + lhmmon (≈250 МБ) уехали бы
/// в личное облако клиента — насовсем, откатить это мы физически не можем (бэклог п.63).
/// Поэтому: папка агента, если она не в зоне синка, иначе — <c>%ProgramData%\szdiag\tools</c>.</summary>
public static class ToolsDirectory
{
    /// <summary>Куда класть инструменты + признак, что пришлось уводить из облака.</summary>
    /// <param name="baseDir">Папка агента (обычно AppContext.BaseDirectory).</param>
    public static (string Dir, bool MovedOutOfCloud) Resolve(string baseDir)
    {
        if (!IsCloudSynced(baseDir)) return (Path.Combine(baseDir, "tools"), false);

        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return (Path.Combine(programData, "szdiag", "tools"), true);
    }

    /// <summary>Похоже ли, что путь внутри синхронизируемого облака. Проверка вынесена в
    /// <see cref="CloudSyncPaths"/> (Contracts) — той же проверкой пользуется и апдейтер
    /// для собственного пути установки (бэклог п.41).</summary>
    public static bool IsCloudSynced(string path) => CloudSyncPaths.IsSynced(path);

    /// <summary>Резолвит путь из testsuite.json (`exe`/`resultFile`/`artifactFile`) с учётом
    /// того, что каталог тулов мог уехать из папки агента в ProgramData (см. <see cref="Resolve"/>).
    ///
    /// Регрессия (бэклог п.151, СЗ 161716): агент запущен из OneDrive-папки, `szcli push`
    /// честно увёл раздачу в <c>C:\ProgramData\szdiag\tools</c>, а <c>TestRunner</c> резолвил
    /// `exe` от <c>AppContext.BaseDirectory</c> — `szcli test run occt` не находил exe
    /// НИКОГДА, и это выглядело как «процесс: НЕ ЗАПУСТИЛСЯ», а не как промах по пути.</summary>
    /// <param name="baseDir">Папка агента (для путей без префикса `tools\`).</param>
    /// <param name="toolsDir">Фактический каталог тулов — результат <see cref="Resolve"/>.</param>
    /// <param name="relativePath">Путь из testsuite.json, обычно начинается с `tools\`.</param>
    public static string ResolveStepPath(string baseDir, string toolsDir, string relativePath)
    {
        if (Path.IsPathRooted(relativePath)) return relativePath;

        const string prefix = "tools";
        var isToolsPath = relativePath.Length > prefix.Length
            && relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && (relativePath[prefix.Length] == Path.DirectorySeparatorChar
                || relativePath[prefix.Length] == Path.AltDirectorySeparatorChar);
        if (!isToolsPath) return Path.Combine(baseDir, relativePath);

        var rest = relativePath[(prefix.Length + 1)..];
        return Path.Combine(toolsDir, rest);
    }
}
