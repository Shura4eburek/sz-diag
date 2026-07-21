namespace SzDiag.Kb;

/// <summary>Пути к заметкам Obsidian-vault базы знаний. Все имена папок — здесь.
/// База знаний ведётся на украинском (сервис/колл-центр украиноязычные).</summary>
public sealed class KbPaths
{
    public string Root { get; }
    public KbPaths(string root) => Root = root;

    public string SzRoot => Path.Combine(Root, "СЗ");
    public string SzDir(string sz) => Path.Combine(SzRoot, sz);
    public string HomeNote(string sz) => Path.Combine(SzDir(sz), $"{sz}.md");
    public string Request(string sz) => Path.Combine(SzDir(sz), "запит.md");
    public string Findings(string sz) => Path.Combine(SzDir(sz), "діагностика.md");
    public string Actions(string sz) => Path.Combine(SzDir(sz), "дії.md");
    public string LogsDir(string sz) => Path.Combine(SzDir(sz), "logs");
    public string ReportsDir(string sz) => Path.Combine(SzDir(sz), "reports");
    public string ReportDir(string sz, string timestamp) => Path.Combine(ReportsDir(sz), timestamp);
    public string Summary(string sz) => Path.Combine(SzDir(sz), "висновок.md");

    public string SymptomsRoot => Path.Combine(Root, "Симптоми");
    public string SymptomNote(string symptom) => Path.Combine(SymptomsRoot, $"{SafeEntityName(symptom)}.md");

    public string OrderNote(string order) => Path.Combine(Root, "Замовлення", $"{SafeEntityName(order)}.md");
    public string DefectNote(string defect) => Path.Combine(Root, "Дефекти", $"{SafeEntityName(defect)}.md");
    public string ComponentNote(string comp) => Path.Combine(Root, "Компоненти", $"{SafeEntityName(comp)}.md");
    public string DeviceNote(string device) => Path.Combine(Root, "Пристрої", $"{SafeEntityName(device)}.md");
    public string Moc => Path.Combine(Root, "MOC.md");

    /// <summary>Безопасное имя файла заметки из свободного текста сущности (устройство,
    /// дефект и т.п.). Символы, недопустимые в имени файла Windows (в т.ч. <c>/ \ : * ? " &lt; &gt; |</c>),
    /// заменяются на «-», иначе слэш увёл бы файл в подпапку и уронил запись. То же значение
    /// используется в тексте Obsidian-линка (<c>KbRecorder</c>), чтобы <c>[[link]]</c> совпадал с
    /// именем файла. Пустой результат → «unnamed».</summary>
    public static string SafeEntityName(string raw)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = raw.Select(c => Array.IndexOf(invalid, c) >= 0 ? '-' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.').Trim();
        return name.Length == 0 ? "unnamed" : name;
    }
}
