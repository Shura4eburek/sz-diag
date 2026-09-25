using System.Text.Json;

namespace SzDiag.Claude;

internal sealed record TokenDay(DateOnly Date, TokenUsage Usage, decimal CostUsd);

/// <summary>Токены и стоимость за текущие сутки (по локальным часам бокса) по всем сессиям —
/// для статусбара. Файл `desk-tokens.json`: переживает перезапуск Desk в течение дня.</summary>
public sealed class TokenLedger
{
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private TokenDay _day;

    public TokenLedger(string path, TimeProvider time)
    {
        _path = path;
        _time = time;
        _day = Load() ?? new TokenDay(TodayDate(), TokenUsage.Zero, 0m);
    }

    public event Action? Changed;

    public TokenUsage Today
    {
        get { lock (_gate) { Roll(); return _day.Usage; } }
    }

    public decimal CostToday
    {
        get { lock (_gate) { Roll(); return _day.CostUsd; } }
    }

    public void Add(TokenUsage usage, decimal costUsd)
    {
        lock (_gate)
        {
            Roll();
            _day = _day with { Usage = _day.Usage.Add(usage), CostUsd = _day.CostUsd + costUsd };
            try { File.WriteAllText(_path, JsonSerializer.Serialize(_day)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }

    private DateOnly TodayDate() => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    private void Roll()
    {
        var today = TodayDate();
        if (_day.Date != today) _day = new TokenDay(today, TokenUsage.Zero, 0m);
    }

    private TokenDay? Load()
    {
        try { return JsonSerializer.Deserialize<TokenDay>(File.ReadAllText(_path)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
