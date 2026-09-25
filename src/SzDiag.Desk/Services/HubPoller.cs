using SzDiag.Contracts;
using SzDiag.HubClient;

namespace SzDiag.Desk.Services;

/// <summary>Опрос `/api` вместо push-канала (спека 2026-09-25: hub почти не трогаем).
/// Три независимых цикла с разной частотой; при недоступном hub — откат до 30 с.</summary>
public sealed class HubPoller(IHubApiClient api, TimeProvider time)
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();

    public HubSnapshot Current { get; private set; } = HubSnapshot.Empty;

    public event Action<HubSnapshot>? Changed;

    public async Task PollOnceAsync(PollKind kind, CancellationToken ct)
    {
        try
        {
            switch (kind)
            {
                case PollKind.Sessions:
                    var sessions = await api.GetSessionsAsync(ct);
                    Update(s => s with { Sessions = sessions, SessionsOkAt = time.GetUtcNow(), Error = null, Failures = 0 });
                    break;
                case PollKind.Transfers:
                    var transfers = await api.GetTransfersAsync(ct);
                    Update(s => s with { Transfers = transfers });
                    break;
                case PollKind.Health:
                    var health = await api.GetHealthAsync(ct);
                    var version = health is null ? Current.HubVersion : await api.GetHubVersionAsync(ct);
                    Update(s => s with { Health = health, HubVersion = version });
                    break;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DeskLog.Write($"опрос {kind}: {ex.Message}");
            // Ошибку считаем только по списку СЗ: он главный признак «hub жив». Передачи и
            // здоровье падают вместе с ним и сами по себе статус не переключают.
            if (kind == PollKind.Sessions)
                Update(s => s with { Error = $"hub не отвечает: {ex.Message}", Failures = s.Failures + 1 });
        }
    }

    public TimeSpan NextDelay(PollKind kind)
    {
        var snap = Current;
        if (snap.Failures > 0)
        {
            var backoff = TimeSpan.FromSeconds(Math.Pow(2, Math.Min(snap.Failures, 5)));
            return backoff > MaxBackoff ? MaxBackoff : backoff;
        }
        return kind switch
        {
            PollKind.Sessions => TimeSpan.FromSeconds(2),
            PollKind.Transfers => snap.Transfers.Any(t => t.State == TransferState.Running)
                ? TimeSpan.FromSeconds(1)
                : TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(10),
        };
    }

    public Task RunAsync(CancellationToken ct)
        => Task.WhenAll(Loop(PollKind.Sessions, ct), Loop(PollKind.Transfers, ct), Loop(PollKind.Health, ct));

    private async Task Loop(PollKind kind, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(kind, ct);
            try { await Task.Delay(NextDelay(kind), time, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Update(Func<HubSnapshot, HubSnapshot> change)
    {
        HubSnapshot next;
        lock (_gate) Current = next = change(Current);
        Changed?.Invoke(next);
    }
}
