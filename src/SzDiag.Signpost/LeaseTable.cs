using SzDiag.Contracts.Signing;
using SzDiag.Contracts.Signpost;

namespace SzDiag.Signpost;

public enum LeaseOutcome
{
    Accepted,
    /// <summary>Живая аренда другого hub, перехват не просили → 409.</summary>
    Conflict,
    /// <summary>seq не вырос для этого hub → 412 (replay или часы издателя ушли назад).</summary>
    Stale,
}

public sealed record LeaseResult(LeaseOutcome Outcome, LeaseInfo? Lease);

/// <summary>Аренда указателя в памяти (research R7): кто сейчас активный hub. Теряется при
/// редеплое — не страшно, живой hub переопубликует себя через 30 с. Срок считается по часам
/// сервиса, а не по issuedAt издателя: часы машины-hub могут врать.</summary>
public sealed class LeaseTable(TimeProvider time, TimeSpan? ttl = null)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(90);

    private readonly TimeSpan _ttl = ttl ?? DefaultTtl;
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _maxSeqByHub = new(StringComparer.Ordinal);
    private HubRecord? _holder;
    private DateTimeOffset _expiresAt;

    public SignedEnvelope? CurrentEnvelope { get; private set; }

    public LeaseInfo? Current
    {
        get { lock (_gate) return Info(); }
    }

    public LeaseResult Publish(HubRecord record, SignedEnvelope envelope)
    {
        lock (_gate)
        {
            if (_maxSeqByHub.TryGetValue(record.HubId, out var max) && record.Seq <= max)
                return new LeaseResult(LeaseOutcome.Stale, Info());

            var now = time.GetUtcNow();
            var alive = _holder is not null && now < _expiresAt;
            if (alive && _holder!.HubId != record.HubId && !record.Takeover)
                return new LeaseResult(LeaseOutcome.Conflict, Info());

            _maxSeqByHub[record.HubId] = record.Seq;
            _holder = record;
            _expiresAt = now + _ttl;
            CurrentEnvelope = envelope;
            return new LeaseResult(LeaseOutcome.Accepted, Info());
        }
    }

    private LeaseInfo? Info()
        => _holder is null ? null : new LeaseInfo(_holder.HubId, _holder.HubName, _expiresAt);
}
