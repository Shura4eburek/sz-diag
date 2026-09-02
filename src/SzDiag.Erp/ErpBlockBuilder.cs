using System.Text;

namespace SzDiag.Erp;

/// <summary>
/// Собирает тело блока для `запит.md`. Чистая функция без файлов и сети — чтобы формат
/// заметки проверялся тестом, а не глазами в vault.
/// Контент базы знаний украинский, поэтому и заголовки секций тоже.
/// </summary>
public static class ErpBlockBuilder
{
    public static string Build(SzFetchResult data, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine($"## Дані з обліку — оновлено {now:yyyy-MM-dd HH:mm}");
        text.AppendLine();

        // В живом ответе 37 полей заявки, больше половины пустые: печатать их значит
        // утопить дефект в шуме.
        foreach (var (key, value) in data.Request.Fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            text.AppendLine($"**{key}:** {Inline(value)}");
        }

        AppendConfiguration(text, data);
        return text.ToString().TrimEnd();
    }

    private static void AppendConfiguration(StringBuilder text, SzFetchResult data)
    {
        text.AppendLine();
        text.AppendLine($"### Склад {Origin(data)}");
        text.AppendLine();

        if (data.Source == ConfigurationSource.None)
        {
            text.AppendLine("_склад невідомий: ні збірки, ні комплектації, ні позицій замовлення_");
            return;
        }

        if (data.Source == ConfigurationSource.Order)
        {
            // В строках заказа серийников нет — колонка была бы пустой на всю таблицу.
            // Зато есть цена и гарантия, а «гарантийный ли ремонт» — рабочий вопрос сервиса.
            AppendRows(text, "| Товар | Шт. | Ціна | Гарантія |", "|---|---|---|---|",
                data.Order!.Products.Select(row => new[]
                {
                    Value(row, "Товар"), Value(row, "Шт."), Value(row, "Ціна"), Value(row, "Гарантія"),
                }));
            return;
        }

        AppendRows(text, "| Компонент | Серійний номер | К-ть |", "|---|---|---|",
            data.Configuration.Select(c => new[] { c.Name, c.Serial, c.Quantity }));
    }

    /// <summary>Заголовок называет источник: иначе непонятно, почему нет серийников.</summary>
    private static string Origin(SzFetchResult data) => data.Source switch
    {
        ConfigurationSource.Assembly => $"зі збірки {Inline(data.Assembly!.Number)}",
        ConfigurationSource.Request => "з комплектації заявки",
        ConfigurationSource.Order => $"із замовлення {Inline(data.Order!.Number)}",
        _ => "машини",
    };

    private static void AppendRows(
        StringBuilder text, string header, string separator, IEnumerable<string[]> rows)
    {
        text.AppendLine(header);
        text.AppendLine(separator);
        foreach (var row in rows)
            text.AppendLine($"| {string.Join(" | ", row.Select(Cell))} |");
    }

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value : "";

    /// <summary>Схлопывает переносы: значение поля должно остаться одной строкой.</summary>
    private static string Inline(string value)
        => value.Replace("\r", "").Replace("\n", " ").Trim();

    /// <summary>Труба в значении разъезжает markdown-таблицу — экранируем.</summary>
    private static string Cell(string value)
        => Inline(value).Replace("|", @"\|");
}
