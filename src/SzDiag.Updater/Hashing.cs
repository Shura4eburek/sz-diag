using System.Security.Cryptography;

namespace SzDiag.Updater;

public static class Hashing
{
    /// <summary>sha256 файла в нижнем регистре hex.</summary>
    public static string Sha256File(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }
}
