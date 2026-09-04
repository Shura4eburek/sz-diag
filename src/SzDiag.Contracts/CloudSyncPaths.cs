namespace SzDiag.Contracts;

/// <summary>Похоже ли, что путь лежит внутри синхронизируемой облачной папки клиента
/// (OneDrive/Dropbox/Google Drive/Яндекс.Диск/iCloud).
///
/// Родилось из <c>SzDiag.Agent.ToolsDirectory</c> (бэклог п.63: 250 МБ доставленных
/// инструментов уехали бы в личное облако клиента насовсем). Вынесено в Contracts, чтобы
/// той же проверкой мог пользоваться и <c>SzDiag.Updater</c> — точка входа на клиенте
/// сама попадала в облако (СЗ 160636, бэклог п.41): <c>state.json</c> и логи текущей сессии
/// синхронизировались наружу, а откат «без следов» на это рассчитан не был.</summary>
public static class CloudSyncPaths
{
    /// <summary>Проверяем и по имени каталога в пути (у клиента папка может называться
    /// локализованно, напр. «OneDrive - Личное»), и по переменным среды OneDrive.</summary>
    public static bool IsSynced(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var full = Path.GetFullPath(path);

        string[] markers = { "onedrive", "dropbox", "google drive", "googledrive", "yandexdisk", "яндекс.диск", "icloud" };
        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => markers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase))))
            return true;

        foreach (var name in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var root = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(root)
                && full.StartsWith(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
