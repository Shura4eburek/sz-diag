namespace SzDiag.Hub;

/// <summary>Пути, присланные агентом, всегда виндовые, а hub может работать и на маке: там
/// `Path.GetFileName(@"C:\jobs\out.txt")` вернул бы строку целиком, и файл лёг бы под таким именем.</summary>
public static class ClientPath
{
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
