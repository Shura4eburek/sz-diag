using System.Text.Json;
using SzDiag.Contracts;

namespace SzDiag.Updater;

/// <summary>`build-info.json` в пакете агента: когда собран. Порядок релизов — по нему, а не по
/// version (git sha не упорядочен): апдейтер не ставит релиз старее установленного (защита от replay).</summary>
public sealed record BuildInfo(string Version, DateTimeOffset BuildTime)
{
    public const string FileName = "build-info.json";

    public static BuildInfo? Read(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<BuildInfo>(File.ReadAllText(path), SignpostJson.Options)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }
}

public enum ReleaseOutcome
{
    UpToDate,
    Applied,
    /// <summary>Подписанный релиз старее установленного — не ставим.</summary>
    Older,
    /// <summary>Ни один адрес не дал пакет с подписанным sha256.</summary>
    DownloadFailed,
}

public sealed record ReleaseResult(ReleaseOutcome Outcome, IReadOnlyList<string> Notes);

/// <summary>Обновление по подписанному релизу из указателя, когда hub не найден ни в
/// конфиге, ни в LAN. Доверие — только подписи: sha256 пакета берётся из проверенного релиза,
/// и пакет с любого адреса обязан с ним совпасть.</summary>
public sealed class SignedReleaseUpdater(HttpClient http, string baseDir)
{
    public async Task<ReleaseResult> ApplyAsync(ReleaseLocation location, string? localVersion, CancellationToken ct = default)
    {
        var rel = location.Release;
        var notes = new List<string>();
        if (localVersion == rel.Version) return new ReleaseResult(ReleaseOutcome.UpToDate, notes);

        var local = BuildInfo.Read(baseDir);
        if (local is not null && rel.BuildTime < local.BuildTime)
        {
            notes.Add($"релиз {rel.Version} ({rel.BuildTime:yyyy-MM-dd HH:mm}) старее установленного " +
                      $"{local.Version} ({local.BuildTime:yyyy-MM-dd HH:mm}) — не ставлю");
            return new ReleaseResult(ReleaseOutcome.Older, notes);
        }

        foreach (var url in location.PackageUrls)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"szpkg-{Guid.NewGuid():N}.zip");
            try
            {
                using (var r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!r.IsSuccessStatusCode)
                    {
                        notes.Add($"{url}: HTTP {(int)r.StatusCode}");
                        continue;
                    }
                    await using var fs = File.Create(tmp);
                    await r.Content.CopyToAsync(fs, ct);
                }
                var actual = Hashing.Sha256File(tmp);
                if (actual != rel.PackageSha256)
                {
                    notes.Add($"{url}: sha256 {actual} не совпал с подписанным {rel.PackageSha256} — пакет подменён или битый");
                    continue;
                }
                PackageApplier.Apply(tmp, baseDir);
                notes.Add($"пакет {rel.Version} взят с {url}");
                return new ReleaseResult(ReleaseOutcome.Applied, notes);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                notes.Add($"{url}: {ex.Message}");
            }
            finally { try { File.Delete(tmp); } catch { } }
        }
        return new ReleaseResult(ReleaseOutcome.DownloadFailed, notes);
    }
}
