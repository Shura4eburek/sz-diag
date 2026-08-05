using System.Collections.Concurrent;
using System.Security.Cryptography;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Каталог инструментов, которые hub раздаёт агентам (`client-tools`: occt, tm5,
/// furmark…). Считает манифест (пути + размеры + sha256) и резолвит запрошенный файл.
///
/// sha256 кэшируется по (путь, размер, время правки): OCCT — почти 300 МБ, пересчитывать
/// хеш на каждый запрос манифеста значило бы читать сотни мегабайт впустую.</summary>
public sealed class ToolCatalog
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime, string Sha)> _shaCache = new();

    public ToolCatalog(string root) => _root = root;

    /// <summary>Абсолютный путь корня раздачи.</summary>
    public string Root => Path.IsPathRooted(_root) ? _root : Path.Combine(AppContext.BaseDirectory, _root);

    /// <summary>Список доступных инструментов (папки первого уровня).</summary>
    public IReadOnlyList<ToolInfo> List()
    {
        if (!Directory.Exists(Root)) return Array.Empty<ToolInfo>();
        var tools = new List<ToolInfo>();
        foreach (var dir in Directory.GetDirectories(Root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
            tools.Add(new ToolInfo(Path.GetFileName(dir), files.Length, files.Sum(f => new FileInfo(f).Length)));
        }
        return tools;
    }

    /// <summary>Манифест инструмента. null — такого инструмента нет.</summary>
    public ToolManifest? Manifest(string tool)
    {
        var dir = ResolveToolDir(tool);
        if (dir is null) return null;

        var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new ToolFile(Relative(dir, f), new FileInfo(f).Length, Sha256Of(f)))
            .ToList();
        return new ToolManifest(tool, files);
    }

    /// <summary>Полный путь к файлу инструмента. null — инструмента/файла нет либо путь
    /// пытается выйти за пределы папки инструмента.</summary>
    public string? ResolveFile(string tool, string relativePath)
    {
        var dir = ResolveToolDir(tool);
        if (dir is null || string.IsNullOrWhiteSpace(relativePath)) return null;

        var combined = Path.GetFullPath(Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        // Защита от `..\..\secrets\svc_diag_key`: раздаём только то, что лежит внутри инструмента.
        var prefix = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(combined) ? combined : null;
    }

    /// <summary>Папка инструмента с той же защитой от выхода наружу (имя — один сегмент).</summary>
    private string? ResolveToolDir(string tool)
    {
        if (string.IsNullOrWhiteSpace(tool)) return null;
        if (tool.Contains('/') || tool.Contains('\\') || tool.Contains("..")) return null;
        var dir = Path.Combine(Root, tool);
        return Directory.Exists(dir) ? Path.GetFullPath(dir) : null;
    }

    private static string Relative(string dir, string file)
        => Path.GetRelativePath(dir, file).Replace('\\', '/');

    private string Sha256Of(string path)
    {
        var info = new FileInfo(path);
        if (_shaCache.TryGetValue(path, out var cached)
            && cached.Size == info.Length && cached.Mtime == info.LastWriteTimeUtc)
            return cached.Sha;

        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var value = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        _shaCache[path] = (info.Length, info.LastWriteTimeUtc, value);
        return value;
    }
}
