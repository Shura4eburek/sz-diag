namespace SzDiag.Kb;

/// <summary>Автосоздание связуемых заметок (Заказ/Дефект/Компонент/Устройство) и MOC.
/// Идемпотентно: существующие заметки не перетираются. В шаблоны встроены Dataview-блоки.</summary>
public sealed class EntityNoteWriter
{
    private readonly KbPaths _paths;
    public EntityNoteWriter(KbPaths paths) => _paths = paths;

    public void EnsureOrder(string order) => Ensure(_paths.OrderNote(order),
        $"""
        # Заказ {order}

        Все СЗ по этому заказу:

        ```dataview
        table устройство as "Устройство", дефект as "Дефект", заменено as "Заменено"
        from "СЗ"
        where заказ = this.file.link
        sort дата desc
        ```
        """);

    public void EnsureDefect(string defect) => Ensure(_paths.DefectNote(defect),
        $"""
        # Дефект: {defect}

        Похожие случаи и что помогло:

        ```dataview
        table заказ as "Заказ", заменено as "Заменено"
        from "СЗ"
        where contains(дефект, this.file.link)
        sort дата desc
        ```
        """);

    public void EnsureComponent(string comp) => Ensure(_paths.ComponentNote(comp),
        $"""
        # Компонент: {comp}

        По каким СЗ менялся:

        ```dataview
        table заказ as "Заказ", дефект as "Дефект"
        from "СЗ"
        where contains(заменено, this.file.link)
        sort дата desc
        ```
        """);

    public void EnsureDevice(string device) => Ensure(_paths.DeviceNote(device),
        $"""
        # Устройство: {device}

        СЗ по этой модели:

        ```dataview
        table заказ as "Заказ", дефект as "Дефект", заменено as "Заменено"
        from "СЗ"
        where устройство = this.file.link
        sort дата desc
        ```
        """);

    public void EnsureMoc() => Ensure(_paths.Moc,
        """
        # База знаний — карта

        ## Последние СЗ
        ```dataview
        table заказ, дефект, заменено
        from "СЗ"
        sort дата desc
        limit 20
        ```

        ## Топ дефектов
        ```dataview
        table length(rows) as "Кол-во СЗ"
        from "СЗ"
        flatten дефект as d
        group by d
        sort length(rows) desc
        ```
        """);

    private static void Ensure(string path, string content)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
