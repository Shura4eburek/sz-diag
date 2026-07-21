using System.IO.Compression;

namespace SzDiag.Updater;

/// <summary>Распаковка пакета агента поверх рабочей папки. Не перетирает локальные
/// файлы клиента: appsettings.json (конфиг) и всё в tools/ (стресс-проги).
/// Атомарность: сперва распаковка во временную папку, потом копирование поверх —
/// битый zip не оставит папку полу-обновлённой.</summary>
public static class PackageApplier
{
    private static readonly string[] SkipTopLevel = { "appsettings.json" };
    private static readonly string[] SkipDirs = { "tools" };

    public static void Apply(string zipPath, string targetDir)
    {
        var staging = Path.Combine(Path.GetTempPath(), $"szupd-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);

            foreach (var src in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staging, src);
                if (IsSkipped(rel)) continue;

                var dest = Path.Combine(targetDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(src, dest, overwrite: true);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* temp — не критично */ }
        }
    }

    private static bool IsSkipped(string relativePath)
    {
        var norm = relativePath.Replace('\\', '/');
        if (SkipTopLevel.Contains(norm, StringComparer.OrdinalIgnoreCase)) return true;
        var firstSegment = norm.Split('/')[0];
        return SkipDirs.Contains(firstSegment, StringComparer.OrdinalIgnoreCase);
    }
}
