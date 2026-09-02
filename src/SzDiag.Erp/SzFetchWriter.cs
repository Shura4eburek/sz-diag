using SzDiag.Kb;

namespace SzDiag.Erp;

/// <param name="DeviceSet">Поле `пристрій` заполнено.</param>
/// <param name="DeviceSkipReason">Почему не заполнено — печатается человеку.</param>
public sealed record SzFetchWriteResult(
    string JsonPath,
    string RequestPath,
    bool DeviceSet,
    string? DeviceSkipReason);

/// <summary>
/// Раскладывает ответ API по базе знаний. Всё, что пишется, либо лежит в своём файле
/// (сырой json), либо в своём блоке (`запит.md`), либо заполняет пустое место
/// (frontmatter) — руками написанное не трогается.
/// </summary>
public sealed class SzFetchWriter
{
    private readonly string _kbRoot;
    private readonly KbPaths _paths;
    private readonly Func<DateTimeOffset> _now;

    public SzFetchWriter(string kbRoot, Func<DateTimeOffset>? now = null)
    {
        _kbRoot = kbRoot;
        _paths = new KbPaths(kbRoot);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public SzFetchWriteResult Write(string sz, SzFetchResult data, string rawJson, bool force)
    {
        new KnowledgeBaseScaffolder(_kbRoot, _now).EnsureSkeleton(sz);

        var jsonPath = Path.Combine(_paths.SzDir(sz), "erp.json");
        File.WriteAllText(jsonPath, rawJson);

        var requestPath = _paths.Request(sz);
        var existing = File.Exists(requestPath) ? File.ReadAllText(requestPath) : "";
        File.WriteAllText(requestPath, MarkedBlock.Upsert(existing, ErpBlockBuilder.Build(data, _now())));

        var (device, deviceSet, skipReason) = UpdateFrontmatter(sz, data, force);
        WriteEntities(data, device, deviceSet);

        new SzJournal(_paths).Append(sz, new JournalEntry(
            _now(), JournalSource.Command, "дані підтягнуто з обліку (sz fetch)"));

        return new SzFetchWriteResult(jsonPath, requestPath, deviceSet, skipReason);
    }

    private (string? Device, bool DeviceSet, string? SkipReason) UpdateFrontmatter(
        string sz, SzFetchResult data, bool force)
    {
        var homePath = _paths.HomeNote(sz);
        var home = FrontmatterEditor.Load(File.ReadAllText(homePath));

        var order = data.Request.OrderNumber ?? data.Order?.Number;
        if (!string.IsNullOrWhiteSpace(order) && (force || IsBlank(home.GetScalar("замовлення"))))
            home.SetScalar("замовлення", Quote(order));

        // `Назва` — предмет заявки, готовое название. Счёт строк заказа не годится:
        // рядом с машиной лежат услуги, монитор и термопаста (видели 3 и 12 позиций).
        var device = Blank(data.Request.Fields.GetValueOrDefault("Назва"))
                     ?? data.Order?.SingleProductName;

        var deviceSet = false;
        string? skipReason = null;
        if (device is null)
        {
            skipReason = "немає ні поля «Назва» в заявці, ні єдиної товарної позиції в замовленні";
        }
        else if (force || IsBlank(home.GetScalar("пристрій")))
        {
            home.SetScalar("пристрій", Quote(device));
            deviceSet = true;
        }
        else
        {
            skipReason = "поле вже заповнене (перебити — --force)";
        }

        File.WriteAllText(homePath, home.Serialize());
        return (device, deviceSet, skipReason);
    }

    private void WriteEntities(SzFetchResult data, string? device, bool deviceSet)
    {
        var entities = new EntityNoteWriter(_paths);

        var order = data.Request.OrderNumber ?? data.Order?.Number;
        if (!string.IsNullOrWhiteSpace(order)) entities.EnsureOrder(order);

        // Компоненты заметками не заводим: каждая сборка дала бы 8-12 однодневок,
        // и поиск по vault утонул бы в них.
        if (deviceSet && device is not null) entities.EnsureDevice(device);
    }

    /// <summary>Непустое значение либо null — чтобы склеивать через `??`.</summary>
    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Скаффолдер пишет пустые значения как `""` — это тоже «пусто».</summary>
    private static bool IsBlank(string? raw)
        => string.IsNullOrWhiteSpace(raw) || raw.Trim() is "\"\"" or "''";

    private static string Quote(string value) => $"\"{value.Replace("\"", "'")}\"";
}
