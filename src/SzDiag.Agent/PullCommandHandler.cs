using System.Security.Cryptography;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Забор файлов с клиента по запросу хоста: находит файлы, режет на куски и
/// отдаёт их через hub. Замена ручной конструкции «ssh + base64 + сборка на хосте»,
/// которая повторялась пять заявок подряд (бэклог п.22).
///
/// Как и <see cref="ExecCommandHandler"/>, наружу не бросает: любая ошибка уходит ответом,
/// потому что молчание агента вызывающий не отличит от потери связи.</summary>
public sealed class PullCommandHandler
{
    private readonly Func<PullChunk, CancellationToken, Task> _sendChunk;
    private readonly int _chunkBytes;

    public PullCommandHandler(Func<PullChunk, CancellationToken, Task> sendChunk,
        int chunkBytes = PullLimits.ChunkBytes)
    {
        _sendChunk = sendChunk;
        _chunkBytes = chunkBytes;
    }

    public async Task<PullResult> HandleAsync(PullRequest request, CancellationToken ct = default)
    {
        List<string> matches;
        try
        {
            matches = Resolve(request.Path, request.Recurse);
        }
        catch (Exception ex)
        {
            return new PullResult(request.RequestId, Array.Empty<PullFileInfo>(), ex.Message);
        }

        if (matches.Count == 0)
            return new PullResult(request.RequestId, Array.Empty<PullFileInfo>(),
                $"не найдено файлов по пути: {request.Path}");

        var files = new List<PullFileInfo>();
        foreach (var path in matches)
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch (Exception ex)
            {
                files.Add(new PullFileInfo(Path.GetFileName(path), path, 0, "", true, ex.Message));
                continue;
            }

            // Слишком большой файл — НЕ тянем, но показываем размер: рядом с минидампами
            // лежат live-дампы на гигабайты, и попытка утащить такой съест час впустую.
            if (info.Length > request.MaxBytes)
            {
                files.Add(new PullFileInfo(info.Name, path, info.Length, "", true,
                    $"больше лимита ({Mb(info.Length)} МБ > {Mb(request.MaxBytes)} МБ)", OverLimit: true));
                continue;
            }

            try
            {
                var sha = await SendFileAsync(request.RequestId, info, ct);
                files.Add(new PullFileInfo(info.Name, path, info.Length, sha));
            }
            catch (Exception ex)
            {
                files.Add(new PullFileInfo(info.Name, path, info.Length, "", true, ex.Message));
            }
        }

        return new PullResult(request.RequestId, files);
    }

    /// <summary>Читает файл кусками, считая sha256 на лету — второй проход по многомегабайтному
    /// дампу ради хеша не нужен.</summary>
    private async Task<string> SendFileAsync(string requestId, FileInfo info, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        // FileShare.ReadWrite: файл может быть открыт своим писателем (лог теста, CSV сенсоров),
        // и забор не должен требовать остановки прогона.
        using var fs = new FileStream(info.FullName, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var buffer = new byte[_chunkBytes];
        var index = 0;
        while (true)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;

            sha.TransformBlock(buffer, 0, read, null, 0);
            var data = new byte[read];
            Array.Copy(buffer, data, read);
            var last = fs.Position >= fs.Length;
            await _sendChunk(new PullChunk(requestId, info.FullName, index++, data, last), ct);
            if (last) break;
        }

        // Пустой файл — тоже валидный результат: шлём один пустой последний кусок,
        // иначе на хосте не появится файла вообще и это выглядело бы как сбой.
        if (index == 0)
            await _sendChunk(new PullChunk(requestId, info.FullName, 0, Array.Empty<byte>(), true), ct);

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    /// <summary>Разворачивает путь: конкретный файл, маска в последнем сегменте
    /// (<c>C:\Windows\Minidump\*.dmp</c>) или каталог. При <paramref name="recurse"/> обходит
    /// подпапки — на 161312 все 11 нужных live-дампов лежали в <c>WATCHDOG*</c>, и `pull` по
    /// папке видел ровно один трёхгиговый файл в корне (бэклог п.75).</summary>
    public static List<string> Resolve(string path, bool recurse = false)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("пустой путь");

        var scope = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        if (Directory.Exists(path))
            return Enumerate(path, "*", scope);

        if (File.Exists(path)) return new List<string> { path };

        var dir = Path.GetDirectoryName(path);
        var mask = Path.GetFileName(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(mask)) return new List<string>();
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"нет каталога: {dir}");
        if (!mask.Contains('*') && !mask.Contains('?')) return new List<string>();

        return Enumerate(dir, mask, scope);
    }

    /// <summary>Обход с пропуском недоступных подпапок: в <c>C:\Windows</c> хватает мест, куда
    /// нет прав даже под админом, и падать всем запросом из-за одной такой папки нельзя.</summary>
    private static List<string> Enumerate(string dir, string mask, SearchOption scope)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = scope == SearchOption.AllDirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        return Directory.GetFiles(dir, mask, opts)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("N1");
}
