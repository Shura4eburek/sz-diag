namespace SzDiag.Contracts;

public enum TransferDirection { Push, Pull }

public enum TransferState { Running, Done, Failed }

/// <summary>Передача файлов hub ↔ клиент для прогресс-баров (Desk, спека 2026-09-25).
/// Раньше `push`/`pull` были долгим синхронным запросом без единого признака жизни —
/// 300 МБ OCCT под плохой сетью выглядели как зависание.</summary>
/// <param name="What">Инструмент (push) или путь/маска на клиенте (pull).</param>
/// <param name="TotalBytes">null — объём заранее неизвестен (pull узнаёт его только в конце).</param>
/// <param name="Note">Для Done — итог («скачано 2, пропущено 5»), для Failed — причина.</param>
public sealed record TransferInfo(
    string Id,
    string Sz,
    TransferDirection Direction,
    string What,
    long? TotalBytes,
    long DoneBytes,
    double BytesPerSecond,
    DateTimeOffset StartedAt,
    TransferState State,
    string? Note = null,
    DateTimeOffset? FinishedAt = null);

public static class TransferRoutes
{
    public const string List = "/api/transfers";
}
