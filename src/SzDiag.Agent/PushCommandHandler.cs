using System.Net.Http.Json;
using System.Security.Cryptography;
using SzDiag.Contracts;

namespace SzDiag.Agent;

/// <summary>Скачивает инструмент с hub и раскладывает его на клиенте. Направление ровно
/// как в модели угроз: **клиент тянет с хоста**, хост на клиента не ходит.
///
/// Заменяет SMB, который на живых заявках не поднимался ничем (`error 67` при открытом 445 —
/// виноват kill switch VPN на хосте), и ручную HTTP-раздачу питоном, поднятую в тот раз
/// руками (бэклог п.1). Наружу не бросает: ошибка уходит в <see cref="PushResult"/>.</summary>
public sealed class PushCommandHandler
{
    private readonly HttpClient _http;
    private readonly string _toolsDir;
    private readonly bool _movedOutOfCloud;

    /// <param name="http">Клиент с BaseAddress = адрес hub и заголовком AgentToken.</param>
    /// <param name="toolsDir">Куда класть инструменты (см. <see cref="ToolsDirectory"/>).</param>
    /// <param name="movedOutOfCloud">Папка агента оказалась в облаке, и мы её обошли.</param>
    public PushCommandHandler(HttpClient http, string toolsDir, bool movedOutOfCloud = false)
    {
        _http = http;
        _toolsDir = toolsDir;
        _movedOutOfCloud = movedOutOfCloud;
    }

    /// <summary>Куда лягут инструменты.</summary>
    public string ToolsDir => _toolsDir;

    /// <summary>Признак, что папку пришлось уводить из синхронизируемого каталога.</summary>
    public bool MovedOutOfCloud => _movedOutOfCloud;

    public async Task<PushResult> HandleAsync(PushRequest request, CancellationToken ct = default)
    {
        var target = Path.Combine(_toolsDir, request.Tool);
        try
        {
            var manifest = await _http.GetFromJsonAsync<ToolManifest>(
                ToolRoutes.Manifest(request.Tool), ct);
            if (manifest is null || manifest.Files.Count == 0)
                return new PushResult(request.RequestId, target, 0, 0, 0,
                    $"hub не отдал состав инструмента '{request.Tool}'");

            Directory.CreateDirectory(target);
            var downloaded = 0;
            var skipped = 0;
            long bytes = 0;

            foreach (var file in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(target, file.Path.Replace('/', Path.DirectorySeparatorChar));

                // Уже лежит такой же — не качаем: повторный push после обрыва должен
                // дотягивать остаток, а не 300 МБ заново.
                if (File.Exists(path) && new FileInfo(path).Length == file.Size
                    && Sha256Of(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await DownloadAsync(request.Tool, file, path, ct);

                var actual = Sha256Of(path);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Битый файл не оставляем: молча испорченный тул хуже отсутствующего.
                    try { File.Delete(path); } catch { }
                    return new PushResult(request.RequestId, target, downloaded, skipped, bytes,
                        $"файл {file.Path} побился при скачивании (sha256 не сошёлся)");
                }

                downloaded++;
                bytes += file.Size;
            }

            return new PushResult(request.RequestId, target, downloaded, skipped, bytes);
        }
        catch (Exception ex)
        {
            return new PushResult(request.RequestId, target, 0, 0, 0, ex.Message);
        }
    }

    /// <summary>Качает файл в целевой путь потоком — OCCT почти 300 МБ, в память такое не берём.</summary>
    private async Task DownloadAsync(string tool, ToolFile file, string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(ToolRoutes.File(tool, file.Path),
            HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        await src.CopyToAsync(dst, ct);
    }

    private static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
