namespace SzDiag.Kb;

/// <summary>Пути к заметкам Obsidian-vault базы знаний. Все имена папок — здесь.</summary>
public sealed class KbPaths
{
    public string Root { get; }
    public KbPaths(string root) => Root = root;

    public string SzRoot => Path.Combine(Root, "СЗ");
    public string SzDir(string sz) => Path.Combine(SzRoot, sz);
    public string HomeNote(string sz) => Path.Combine(SzDir(sz), $"{sz}.md");
    public string Request(string sz) => Path.Combine(SzDir(sz), "request.md");
    public string Findings(string sz) => Path.Combine(SzDir(sz), "findings.md");
    public string Actions(string sz) => Path.Combine(SzDir(sz), "actions.md");
    public string LogsDir(string sz) => Path.Combine(SzDir(sz), "logs");

    public string OrderNote(string order) => Path.Combine(Root, "Заказы", $"{order}.md");
    public string DefectNote(string defect) => Path.Combine(Root, "Дефекты", $"{defect}.md");
    public string ComponentNote(string comp) => Path.Combine(Root, "Компоненты", $"{comp}.md");
    public string DeviceNote(string device) => Path.Combine(Root, "Устройства", $"{device}.md");
    public string Moc => Path.Combine(Root, "MOC.md");
}
