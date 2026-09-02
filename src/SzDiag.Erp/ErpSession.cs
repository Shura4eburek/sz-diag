namespace SzDiag.Erp;

/// <summary>
/// Монопольный захват учётной программы на время работы. Захват обязан отпускаться:
/// иначе следующий вызов упрётся в занятость, а у человека останутся свёрнутые окна
/// и чужой фильтр в панели поиска. Поэтому освобождение висит и на Dispose, и на Ctrl+C.
/// </summary>
public sealed class ErpSession : IAsyncDisposable
{
    public const string BeginTool = "session.begin";
    public const string EndTool = "session.end";

    private readonly ErpApiClient _client;
    private readonly ConsoleCancelEventHandler _onCancel;
    private int _ended;

    private ErpSession(ErpApiClient client)
    {
        _client = client;
        // Ctrl+C посреди многоминутного вызова не должен оставить программу захваченной.
        // Процесс завершится штатно после обработчика — e.Cancel не трогаем.
        _onCancel = (_, _) => Release();
        Console.CancelKeyPress += _onCancel;
    }

    /// <summary>
    /// Захват не взят — сессии нет: `busy` летит наружу, а освобождение НЕ шлётся,
    /// иначе отпустили бы чужую сессию, которая захват и заняла.
    /// </summary>
    public static async Task<ErpSession> BeginAsync(ErpApiClient client, CancellationToken ct = default)
    {
        await client.CallAsync(BeginTool, ct: ct);
        return new ErpSession(client);
    }

    public async ValueTask DisposeAsync()
    {
        Console.CancelKeyPress -= _onCancel;
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;

        try { await _client.CallAsync(EndTool); }
        catch (ErpApiException) { /* сервис уже мёртв: настоящую причину сбоя не затмеваем */ }
    }

    /// <summary>Синхронное освобождение для обработчика Ctrl+C — ждать там нечем.</summary>
    private void Release()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0) return;
        try { _client.CallAsync(EndTool).GetAwaiter().GetResult(); }
        catch (ErpApiException) { /* см. выше */ }
    }
}
