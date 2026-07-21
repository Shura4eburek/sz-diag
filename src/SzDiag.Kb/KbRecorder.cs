namespace SzDiag.Kb;

/// <summary>Merge-запись данных СЗ в базу знаний: frontmatter, связуемые заметки, проза.</summary>
public sealed class KbRecorder
{
    private readonly KbPaths _paths;
    private readonly IKnowledgeBaseScaffolder _scaffolder;
    private readonly EntityNoteWriter _entities;
    private readonly Func<DateTimeOffset> _now;

    public KbRecorder(KbPaths paths, IKnowledgeBaseScaffolder scaffolder,
        EntityNoteWriter entities, Func<DateTimeOffset>? now = null)
    {
        _paths = paths;
        _scaffolder = scaffolder;
        _entities = entities;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public void Record(RecordRequest req)
    {
        _scaffolder.EnsureSkeleton(req.Sz);
        _entities.EnsureMoc();

        var homePath = _paths.HomeNote(req.Sz);
        var fm = FrontmatterEditor.Load(File.ReadAllText(homePath));

        if (req.Order is not null)
        {
            fm.SetScalar("замовлення", Quoted(req.Order));
            _entities.EnsureOrder(req.Order);
        }
        if (req.Device is not null)
        {
            fm.SetScalar("пристрій", Quoted(req.Device));
            _entities.EnsureDevice(req.Device);
        }
        foreach (var d in req.Defects)
        {
            fm.AddToList("дефект", Quoted(d));
            _entities.EnsureDefect(d);
        }
        foreach (var c in req.Replaced)
        {
            fm.AddToList("замінено", Quoted(c));
            _entities.EnsureComponent(c);
        }
        foreach (var spt in req.Symptoms)
        {
            fm.AddToList("симптом", Quoted(spt));
            _entities.EnsureSymptom(spt);
        }
        if (req.Status is not null) fm.SetScalar("статус", req.Status);
        if (req.Verdict is not null) fm.SetScalar("вердикт", req.Verdict);
        File.WriteAllText(homePath, fm.Serialize());

        var date = _now().ToString("yyyy-MM-dd");
        foreach (var f in req.Findings) AppendLineIfMissing(_paths.Findings(req.Sz), $"- {date}: {f}");
        foreach (var a in req.Actions) AppendLineIfMissing(_paths.Actions(req.Sz), $"- {date}: {a}");
    }

    // Линк ссылается на ИМЯ ФАЙЛА заметки (санитизированное) — иначе слэш/двоеточие в
    // сыром тексте дали бы битый [[link]], не совпадающий с реальным файлом сущности.
    private static string Quoted(string name) => $"\"[[{KbPaths.SafeEntityName(name)}]]\"";

    private static void AppendLineIfMissing(string path, string line)
    {
        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        if (existing.Contains(line)) return;
        var prefix = existing.Length > 0 && !existing.EndsWith("\n") ? "\n" : "";
        File.AppendAllText(path, prefix + line + "\n");
    }
}
