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
}
