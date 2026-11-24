using System.Security.Cryptography;
using SzDiag.Contracts.Signing;

namespace SzDiag.Signpost;

/// <summary>Пакеты агента и последний релиз на volume Railway (`DATA_DIR`). Пакет публичный и
/// без секретов; доверие к нему даёт подписанный релиз с его sha256, а не этот сервис.</summary>
public sealed class PackageStore
{
    public const long MaxPackageBytes = 100L * 1024 * 1024;
    public const int Keep = 3;

    private readonly string _packages;
    private readonly string _releasePath;
    private readonly object _gate = new();

    public PackageStore(string dataDir)
    {
        _packages = Path.Combine(dataDir, "packages");
        _releasePath = Path.Combine(dataDir, "release.json");
        Directory.CreateDirectory(_packages);
    }

    public string PathFor(string sha256) => Path.Combine(_packages, sha256 + ".zip");

    public bool Has(string sha256) => IsSha256(sha256) && File.Exists(PathFor(sha256));

    /// <summary>Принять тело и сверить sha256 с заявленным. Не сошлось/слишком большое — false.</summary>
    /// <param name="protectedSha">Пакет текущего релиза — не вытесняется никогда.</param>
    public async Task<(bool Ok, string? Error)> SaveAsync(string sha256, Stream body, CancellationToken ct,
        string? protectedSha = null)
    {
        if (!IsSha256(sha256)) return (false, "в пути не sha256");
        var tmp = Path.Combine(_packages, $".upload-{Guid.NewGuid():N}");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var fs = File.Create(tmp))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await body.ReadAsync(buf, ct)) > 0)
                {
                    total += n;
                    if (total > MaxPackageBytes) return (false, $"пакет больше {MaxPackageBytes / 1024 / 1024} МБ");
                    hash.AppendData(buf, 0, n);
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                }
            }
            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (actual != sha256) return (false, $"sha256 тела {actual} не совпадает с путём");

            lock (_gate)
            {
                File.Move(tmp, PathFor(sha256), overwrite: true);
                Prune(sha256, protectedSha);
            }
            return (true, null);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public SignedEnvelope? LoadRelease()
    {
        lock (_gate)
            return File.Exists(_releasePath) ? SignedEnvelope.TryParse(File.ReadAllText(_releasePath)) : null;
    }

    public void SaveRelease(SignedEnvelope envelope)
    {
        lock (_gate) File.WriteAllText(_releasePath, envelope.ToJson());
    }

    /// <summary>Хранить последние <see cref="Keep"/> пакетов; только что загруженный и пакет
    /// текущего релиза — всегда.</summary>
    private void Prune(string justSaved, string? protectedSha)
    {
        var old = new DirectoryInfo(_packages).GetFiles("*.zip")
            .Where(f => !f.Name.StartsWith(justSaved, StringComparison.Ordinal)
                        && (protectedSha is null || !f.Name.StartsWith(protectedSha, StringComparison.Ordinal)))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(Keep - 1);
        foreach (var f in old)
            try { f.Delete(); } catch (IOException) { }
    }

    public static bool IsSha256(string? s) => s is { Length: 64 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
