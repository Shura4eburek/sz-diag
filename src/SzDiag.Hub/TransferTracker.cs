using System.Collections.Concurrent;
using SzDiag.Contracts;

namespace SzDiag.Hub;

/// <summary>Живой учёт передач push/pull для `GET /api/transfers` (прогресс-бары Desk, спека
/// 2026-09-25). In-memory: после рестарта hub незавершённые передачи всё равно мертвы.
/// Прогресс виден и для передач, запущенных Claude через `szcli`, — учёт на стороне hub,
/// а не в том, кто запустил.</summary>
public sealed class TransferTracker
{
    /// <summary>Сколько держать завершённую передачу в списке — чтобы итог успели увидеть.</summary>
    public static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    private sealed class Entry
    {
        public required string Id;
        public required string Sz;
        public required TransferDirection Direction;
        public required string What;
        public required DateTimeOffset StartedAt;
        public long? Total;
        public long Done;
        public TransferState State = TransferState.Running;
        public string? Note;
        public DateTimeOffset? FinishedAt;
    }

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Entry> _items = new();

    public TransferTracker(TimeProvider time) => _time = time;

    public void Start(string id, string sz, TransferDirection dir, string what, long? total = null)
        => _items[id] = new Entry
        {
            Id = id, Sz = sz, Direction = dir, What = what, Total = total, StartedAt = _time.GetUtcNow(),
        };

    public void SetTotal(string id, long total)
    {
        if (_items.TryGetValue(id, out var e)) lock (e) e.Total = total;
    }

    public void Add(string id, long bytes)
    {
        if (_items.TryGetValue(id, out var e)) lock (e) e.Done += bytes;
    }

    /// <param name="error">Не null — передача провалена, текст уходит в <see cref="TransferInfo.Note"/>.</param>
    public void Finish(string id, string? error, string? note = null)
    {
        if (!_items.TryGetValue(id, out var e)) return;
        lock (e)
        {
            e.State = error is null ? TransferState.Done : TransferState.Failed;
            e.Note = error ?? note;
            e.FinishedAt = _time.GetUtcNow();
        }
    }

    public IReadOnlyList<TransferInfo> Snapshot()
    {
        var now = _time.GetUtcNow();
        foreach (var (id, e) in _items)
            if (e.FinishedAt is { } f && now - f > KeepFinished) _items.TryRemove(id, out _);

        return _items.Values
            .Select(e => { lock (e) return ToInfo(e, now); })
            .OrderByDescending(i => i.StartedAt)
            .ToList();
    }

    private static TransferInfo ToInfo(Entry e, DateTimeOffset now)
    {
        // Средняя скорость с начала передачи: для полосы прогресса этого хватает, а скользящее
        // окно дало бы дёрганые цифры при чанках по 1 МБ.
        var end = e.FinishedAt ?? now;
        var seconds = (end - e.StartedAt).TotalSeconds;
        var speed = seconds > 0 ? e.Done / seconds : 0;
        return new TransferInfo(e.Id, e.Sz, e.Direction, e.What, e.Total, e.Done, speed,
            e.StartedAt, e.State, e.Note, e.FinishedAt);
    }
}
