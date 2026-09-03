using System.Reflection;

namespace SzDiag.Hub;

/// <summary>Версия/дата сборки hub — тем же способом, что и `szcli --version`
/// (<see cref="SzDiag.Cli"/> недоступен отсюда, логика короткая — дублировать дешевле, чем
/// тянуть межпроектную зависимость ради одной строки). Протухший hub на боксе (пакет
/// пересобрали, а сам hub не перезапустили) иначе не отличить от свежего — CLI показывает
/// только собственную дату сборки (бэклог п.211/165).</summary>
public static class HubBuildInfo
{
    public static string Describe()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        string built;
        try
        {
            var exe = Environment.ProcessPath;
            built = exe is null ? "?" : File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
        }
        catch { built = "?"; }
        return $"hub {version}, сборка {built}";
    }
}
