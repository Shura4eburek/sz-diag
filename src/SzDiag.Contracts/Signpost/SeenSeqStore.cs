using System.Text.Json;

namespace SzDiag.Contracts.Signpost;

/// <summary>Последний принятый <c>seq</c> по виду записи — защита от replay старой, но честно
/// подписанной записи (research R6). Хранится рядом с exe клиента: часы клиента бывают сбиты
/// на месяцы, а seq сравнивается только с самим собой. Сбой чтения/записи — не повод не работать:
/// хуже всего будет принять запись не новее прошлой.</summary>
public sealed class SeenSeqStore(string path)
{
    public const string FileName = "signpost-seen.json";

    private readonly object _gate = new();

    public static SeenSeqStore NextToExe() => new(Path.Combine(AppContext.BaseDirectory, FileName));

    public long Get(string kind)
    {
        lock (_gate) return Load().TryGetValue(kind, out var seq) ? seq : 0;
    }

    /// <summary>Запомнить, только если больше сохранённого.</summary>
    public void Remember(string kind, long seq)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.TryGetValue(kind, out var prev) && prev >= seq) return;
            all[kind] = seq;
            try { File.WriteAllText(path, JsonSerializer.Serialize(all)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private Dictionary<string, long> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
}
