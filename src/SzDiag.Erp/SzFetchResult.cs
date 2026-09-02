namespace SzDiag.Erp;

/// <summary>Строка комплектации, состава сборки либо заказа.</summary>
public sealed record ErpComponent(string Code, string Name, string Serial, string Quantity);

/// <summary>
/// Поля заявки — словарь «имя → значение» как пришло. Имена заданы чужим интерфейсом и
/// украинские; заводить под них C#-свойства значит ломаться при каждом переименовании на
/// той стороне. Типизировано только то, на чём стоит логика.
/// </summary>
public sealed record ErpRequest(
    string Number,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<ErpComponent> Components,
    string? OrderNumber);

public sealed record ErpOrder(
    string Number,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<IReadOnlyDictionary<string, string>> Products)
{
    /// <summary>
    /// Фоллбэк для `пристрій`, когда в заявке пустое поле «Назва». Работает только на
    /// заказе из одной строки: в живых заказах рядом с машиной лежат услуги, монитор и
    /// термопаста (видели 3 и 12 позиций), и по строкам устройство не выводится.
    /// </summary>
    public string? SingleProductName =>
        Products.Count == 1
        && Products[0].TryGetValue("Товар", out var name)
        && !string.IsNullOrWhiteSpace(name)
            ? name
            : null;
}

public sealed record ErpAssembly(
    string Number,
    string Summary,
    IReadOnlyList<ErpComponent> Components);

public sealed record SzFetchResult(ErpRequest Request, ErpOrder? Order, ErpAssembly? Assembly)
{
    /// <summary>
    /// Что стоит в машине: збірка → комплектация заявки → строки заказа. Последнее звено
    /// не запасное, а основное: на обеих живых заявках комплектация и збірка пришли
    /// пустыми, и весь состав лежал в заказе. Именно этот список потом сверяется с тем,
    /// что видит `diag run`.
    /// </summary>
    public IReadOnlyList<ErpComponent> Configuration =>
        Assembly is { Components.Count: > 0 } ? Assembly.Components
        : Request.Components.Count > 0 ? Request.Components
        : OrderAsComponents();

    /// <summary>Строки заказа как компоненты. Серийников там нет — заказ их не несёт.</summary>
    private IReadOnlyList<ErpComponent> OrderAsComponents() => Order is null
        ? Array.Empty<ErpComponent>()
        : Order.Products
            .Select(row => new ErpComponent(
                Value(row, "Код"), Value(row, "Товар"), "", Value(row, "Шт.")))
            .ToList();

    private static string Value(IReadOnlyDictionary<string, string> row, string key)
        => row.TryGetValue(key, out var value) ? value : "";
}
