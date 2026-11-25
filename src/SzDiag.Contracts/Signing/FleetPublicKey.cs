namespace SzDiag.Contracts.Signing;

/// <summary>Где клиент берёт публичный ключ флота: из конфига (`SignPublicKey`), иначе из
/// `fleet-sign.pub` рядом с exe. Только с диска, до обновления: ключ из проверяемого пакета
/// проверкой не был бы.</summary>
public static class FleetPublicKey
{
    public const string FileName = "fleet-sign.pub";

    public static IReadOnlyList<string> Resolve(string? configured, string baseDir)
    {
        // В appsettings.json PEM лежит одной строкой с «\n» — build-dist так его пишет.
        if (!string.IsNullOrWhiteSpace(configured)) return [configured.Replace("\\n", "\n")];
        var file = Path.Combine(baseDir, FileName);
        try { return File.Exists(file) ? [File.ReadAllText(file)] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
