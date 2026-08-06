namespace SzDiag.Contracts;

/// <summary>Hub → агент: забрать файл(ы) с клиента на хост.
///
/// Пять заявок подряд дампы и CSV тащились одной и той же самодельной конструкцией
/// (`ssh -EncodedCommand` → `ToBase64String` → сборка строки на хосте → `WriteAllBytes`),
/// либо через SMB с кредами в скрипте. Через `exec` это невозможно в принципе: лимит вывода
/// 200k символов против ~3.8 млн base64 на дамп 2.8 МБ (бэклог п.22/п.36a).</summary>
/// <param name="Path">Путь на клиенте; последний сегмент может быть маской (<c>*.dmp</c>).</param>
/// <param name="MaxBytes">Потолок на файл. Больше — файл пропускается с явной причиной,
/// а не тянется молча: рядом с минидампами лежат live-дампы на 8.3 ГБ.</param>
/// <param name="Recurse">Обходить подпапки. <c>C:\Windows\LiveKernelReports</c> держит всё
/// интересное в подпапках <c>WATCHDOG*</c>, и без рекурсии `pull` по папке видел ровно один
/// трёхгиговый файл в корне (бэклог п.75).</param>
public sealed record PullRequest(string Sz, string RequestId, string Path, long MaxBytes,
    bool Recurse = false);

/// <summary>Метаданные одного найденного файла.</summary>
/// <param name="Skipped">Файл не передавался (обычно — больше <c>MaxBytes</c>).</param>
/// <param name="SkipReason">Почему пропущен — печатается пользователю.</param>
/// <param name="OverLimit">Пропущен именно по лимиту — это штатное поведение, ради которого
/// лимит и задавался, и на код возврата влиять не должно (бэклог п.75).</param>
public sealed record PullFileInfo(
    string Name,
    string FullPath,
    long Size,
    string Sha256,
    bool Skipped = false,
    string? SkipReason = null,
    bool OverLimit = false);

/// <summary>Агент → hub: очередной кусок файла. Файл режется на части, потому что
/// SignalR-сообщение ограничено (10 МБ), а забирать надо и многомегабайтные дампы.</summary>
public sealed record PullChunk(string RequestId, string FullPath, int Index, byte[] Data, bool Last);

/// <summary>Агент → hub: перечень файлов и итог операции. Приходит ПОСЛЕ всех чанков.</summary>
/// <param name="Error">Ошибка целиком по запросу (путь не найден, нет прав) — не пустая
/// строка означает, что файлов нет вообще.</param>
public sealed record PullResult(
    string RequestId,
    IReadOnlyList<PullFileInfo> Files,
    string? Error = null);

/// <summary>Тело HTTP-запроса CLI → hub.</summary>
public sealed record PullCommandRequest(string Path, long? MaxBytes = null, bool Recurse = false);

/// <summary>Hub → CLI: что и куда реально забрали.</summary>
/// <param name="SavedPath">Путь на хосте, куда лёг файл (null — файл пропущен).</param>
/// <param name="OverLimit">Пропущен по лимиту — штатно, не ошибка.</param>
public sealed record PullSavedFile(
    string Name,
    string SourcePath,
    long Size,
    string Sha256,
    string? SavedPath,
    bool Skipped = false,
    string? SkipReason = null,
    bool OverLimit = false);

/// <summary>Ответ CLI на `szcli pull`.</summary>
public sealed record PullResponse(IReadOnlyList<PullSavedFile> Files, string? Error = null);

/// <summary>Лимиты забора — одинаковые на агенте и hub.</summary>
public static class PullLimits
{
    /// <summary>Размер куска. 1 МБ с запасом влезает в лимит SignalR-сообщения (10 МБ)
    /// даже после base64-раздувания в JSON-протоколе (×1.33).</summary>
    public const int ChunkBytes = 1024 * 1024;

    /// <summary>Потолок на файл по умолчанию — 256 МБ. Минидампы (2–5 МБ) и CSV проходят,
    /// полный live-дамп на 8 ГБ отсекается с явным сообщением.</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>Сколько ждать агента: забор упирается в диск клиента и в сеть, а под
    /// нагрузкой ещё и в планировщик — короткий таймаут дал бы ложный «агент не ответил».</summary>
    public const int TimeoutSeconds = 600;
}
