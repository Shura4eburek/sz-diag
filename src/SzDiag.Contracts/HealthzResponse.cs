namespace SzDiag.Contracts;

/// <summary>Снимок ThreadPool в момент запроса — то, чем на живой заявке (СЗ 160306, бэклог
/// п.50) пришлось считать вручную через `Get-Process` (3674 потока при здоровых 26 сразу
/// после рестарта).</summary>
public sealed record HealthzResponse(
    int ThreadCount,
    int AvailableWorkerThreads,
    int MaxWorkerThreads,
    int AvailableCompletionPortThreads,
    int MaxCompletionPortThreads,
    long PendingWorkItemCount,
    DateTimeOffset At);
