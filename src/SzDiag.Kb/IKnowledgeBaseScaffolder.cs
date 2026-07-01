namespace SzDiag.Kb;

/// <summary>Создаёт каркас папки базы знаний для СЗ в Obsidian-форме.</summary>
public interface IKnowledgeBaseScaffolder
{
    /// <summary>Создаёт kb/СЗ/&lt;sz&gt;/ если её ещё нет. Возвращает путь к папке СЗ.</summary>
    string EnsureSkeleton(string sz);
}
