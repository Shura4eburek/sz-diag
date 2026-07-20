namespace SzDiag.Kb;

/// <summary>Создаёт каркас папки базы знаний для СЗ в Obsidian-форме.</summary>
public interface IKnowledgeBaseScaffolder
{
    /// <summary>Создаёт kb/СЗ/&lt;sz&gt;/ если её ещё нет. Возвращает путь к папке СЗ.</summary>
    string EnsureSkeleton(string sz);

    /// <summary>Создаёт вывод.md со скелетом (клиентский блок + техразбор), если его ещё
    /// нет. Гарантирует наличие каркаса СЗ. Возвращает путь к вывод.md.</summary>
    string EnsureSummarySkeleton(string sz);
}
