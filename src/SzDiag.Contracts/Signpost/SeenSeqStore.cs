using System.Text.Json;

namespace SzDiag.Contracts.Signpost;

/// <summary>Последний принятый <c>seq</c> по виду записи — защита от replay старой, но честно
/// подписанной записи (research R6). Хранится рядом с exe клиента: часы клиента бывают сбиты
/// на месяцы, а seq сравнивается только с самим собой. Сбой чтения/записи — не повод не работать:
/// хуже всего будет принять запись не новее прошлой.
/// Ключ — не голый вид записи: seq записи hub — это unix-ms часов издателя, и после переезда hub
/// на другую машину (ПК → ноутбук) с отстающими часами общий на все hub seq отверг бы честную
/// запись нового hub как replay. Поэтому hub считается по своему <c>HubId</c> (<see cref="HubKey"/>),
/// а релиз — по виду (издатель один, build-dist). Старый файл с ключом <c>"hub"</c> читается
/// без ошибок, но этот ключ больше никем не спрашивается — безопасный выбор: не отвергать новый hub.</summary>
public sealed class SeenSeqStore(string path)
{
    public const string FileName = "signpost-seen.json";

    private readonly object _gate = new();

    public static SeenSeqStore NextToExe() => new(Path.Combine(AppContext.BaseDirectory, FileName));

    /// <summary>Ключ seq записи hub: сравнение на replay — только внутри одного hub.</summary>
    public static string HubKey(string hubId) => $"{SignpostKinds.Hub}:{hubId}";

    public long Get(string key)
    {
        lock (_gate) return Load().TryGetValue(key, out var seq) ? seq : 0;
    }

    /// <summary>Запомнить, только если больше сохранённого.</summary>
    public void Remember(string key, long seq)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.TryGetValue(key, out var prev) && prev >= seq) return;
            all[key] = seq;
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
