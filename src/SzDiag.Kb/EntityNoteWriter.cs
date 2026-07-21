namespace SzDiag.Kb;

/// <summary>Автосоздание связуемых заметок (Замовлення/Дефект/Компонент/Пристрій) и MOC.
/// Идемпотентно: существующие заметки не перетираются. В шаблоны встроены Dataview-блоки.
/// Контент — на украинском (сервис/колл-центр украиноязычные).</summary>
public sealed class EntityNoteWriter
{
    private readonly KbPaths _paths;
    public EntityNoteWriter(KbPaths paths) => _paths = paths;

    public void EnsureOrder(string order) => Ensure(_paths.OrderNote(order),
        $"""
        # Замовлення {order}

        Всі СЗ по цьому замовленню:

        ```dataview
        table пристрій as "Пристрій", дефект as "Дефект", замінено as "Замінено"
        from "СЗ"
        where замовлення = this.file.link
        sort дата desc
        ```
        """);

    public void EnsureDefect(string defect) => Ensure(_paths.DefectNote(defect),
        $"""
        # Дефект: {defect}

        Схожі випадки і що допомогло:

        ```dataview
        table замовлення as "Замовлення", замінено as "Замінено"
        from "СЗ"
        where contains(дефект, this.file.link)
        sort дата desc
        ```
        """);

    public void EnsureComponent(string comp) => Ensure(_paths.ComponentNote(comp),
        $"""
        # Компонент: {comp}

        По яких СЗ мінявся:

        ```dataview
        table замовлення as "Замовлення", дефект as "Дефект"
        from "СЗ"
        where contains(замінено, this.file.link)
        sort дата desc
        ```
        """);

    public void EnsureDevice(string device) => Ensure(_paths.DeviceNote(device),
        $"""
        # Пристрій: {device}

        СЗ по цій моделі:

        ```dataview
        table замовлення as "Замовлення", дефект as "Дефект", замінено as "Замінено"
        from "СЗ"
        where пристрій = this.file.link
        sort дата desc
        ```
        """);

    public void EnsureSymptom(string symptom) => Ensure(_paths.SymptomNote(symptom),
        $"""
        ---
        тип: симптом
        симптом: {symptom}
        дата_оновлення: ""
        ---

        # Симптом: {symptom}

        ## Як розпізнати
        …

        ## Що перевіряти (по черзі)
        1. …

        ## Причини, що спостерігались
        - …

        ## Пов'язані СЗ (авто)
        ```dataview
        list from "СЗ" where contains(симптом, this.file.link) sort дата desc
        ```
        """);

    public void EnsureMoc() => Ensure(_paths.Moc,
        """
        # База знань — карта

        ## Останні СЗ
        ```dataview
        table замовлення, дефект, замінено
        from "СЗ"
        sort дата desc
        limit 20
        ```

        ## Топ дефектів
        ```dataview
        table length(rows) as "Кількість СЗ"
        from "СЗ"
        flatten дефект as d
        group by d
        sort length(rows) desc
        ```

        ## Симптоми
        ```dataview
        list from "Симптоми" where тип = "симптом" sort file.name asc
        ```
        """);

    private static void Ensure(string path, string content)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
