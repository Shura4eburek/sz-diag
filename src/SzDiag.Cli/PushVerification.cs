using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using SzDiag.Contracts;

namespace SzDiag.Cli;

/// <summary>Проверка инструмента ДО доставки на клиента: размер и подпись (Authenticode CN).
///
/// Регрессия (бэклог, СЗ 163013): прошивка LED-контроллера собиралась вручную из-за
/// недоверенного источника — `www.gigabyte.com` под Cloudflare отдавал 403, файл достали
/// через архивный снапшот, и сверять пришлось руками (размер с сайта против `Get-Item.Length`,
/// `Get-AuthenticodeSignature` на CN издателя). `szcli push` ни того, ни другого не делал —
/// битый/подменённый файл ушёл бы на клиента незамеченным.</summary>
public static class PushVerification
{
    public sealed record Result(bool Ok, string? Error)
    {
        public static Result Success { get; } = new(true, null);
        public static Result Fail(string error) => new(false, error);
    }

    /// <summary>Сверяет ожидаемый размер инструмента (в байтах) с тем, что реально лежит в
    /// каталоге раздачи hub (`szcli push --list` / `/api/tools`).</summary>
    public static Result CheckSize(ToolCatalogInfo? catalog, string tool, long expectedBytes)
    {
        if (catalog is null) return Result.Fail("hub не ответил на запрос каталога инструментов");
        var info = catalog.Tools.FirstOrDefault(t => t.Name.Equals(tool, StringComparison.OrdinalIgnoreCase));
        if (info is null) return Result.Fail($"инструмент '{tool}' не найден в каталоге раздачи ({catalog.Root})");
        if (info.Bytes != expectedBytes)
            return Result.Fail($"размер не совпал: ожидали {expectedBytes} байт, в каталоге {info.Bytes} байт");
        return Result.Success;
    }

    /// <summary>Сверяет Authenticode CN всех .exe/.dll в папке инструмента с ожидаемым издателем.
    /// <paramref name="getSignerCn"/> — точка внедрения ради тестируемости (реальная реализация —
    /// <see cref="GetSignerCn"/>, читает подпись с диска и требует настоящего подписанного файла,
    /// которого в юнит-тестах нет).</summary>
    public static Result CheckSigner(string toolDir, string expectedCn, Func<string, string?> getSignerCn)
    {
        if (!Directory.Exists(toolDir)) return Result.Fail($"каталог инструмента не найден: {toolDir}");

        var binaries = Directory.EnumerateFiles(toolDir, "*.exe", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(toolDir, "*.dll", SearchOption.AllDirectories))
            .ToList();
        if (binaries.Count == 0) return Result.Fail($"в {toolDir} нет .exe/.dll для проверки подписи");

        var bad = new List<string>();
        foreach (var file in binaries)
        {
            var cn = getSignerCn(file);
            if (cn is null || !cn.Contains(expectedCn, StringComparison.OrdinalIgnoreCase))
                bad.Add($"{Path.GetFileName(file)}: {(cn is null ? "не подписан" : $"подписант '{cn}'")}");
        }
        return bad.Count == 0
            ? Result.Success
            : Result.Fail($"подпись не совпала с ожидаемой '{expectedCn}': {string.Join("; ", bad)}");
    }

    /// <summary>Реальное извлечение CN из Authenticode-подписи файла на диске.</summary>
    public static string? GetSignerCn(string filePath)
    {
        try
        {
            using var cert = X509Certificate.CreateFromSignedFile(filePath);
            var subject = cert.Subject;
            var match = Regex.Match(subject, "CN=([^,]+)");
            return match.Success ? match.Groups[1].Value.Trim() : subject;
        }
        catch { return null; }
    }
}
