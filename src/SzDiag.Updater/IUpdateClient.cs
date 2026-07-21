namespace SzDiag.Updater;

/// <summary>Доступ к раздаче пакета на hub. Отдельный интерфейс — чтобы Program
/// тестировать с фейком, а сеть жила только в HttpUpdateClient.</summary>
public interface IUpdateClient
{
    /// <summary>Версия пакета на хосте (GET /agent/version).</summary>
    Task<string> GetVersionAsync(CancellationToken ct = default);

    /// <summary>Ожидаемый sha256 пакета (GET /agent/package.sha256).</summary>
    Task<string> GetPackageSha256Async(CancellationToken ct = default);

    /// <summary>Скачать package.zip в destZipPath (GET /agent/package).</summary>
    Task DownloadPackageAsync(string destZipPath, CancellationToken ct = default);
}
