namespace SzDiag.Kb;

/// <summary>Чем закончился прогон бэкапа vault'а.</summary>
public enum KbBackupOutcome
{
    /// <summary>Vault не менялся — коммита не было.</summary>
    NoChanges,

    /// <summary>Закоммичено и выгружено в remote.</summary>
    Pushed,

    /// <summary>Коммит лёг локально, push не прошёл (сеть/креды). Данные не потеряны.</summary>
    CommittedNotPushed,

    /// <summary>Прогон не удался: не git-репозиторий, упал add/commit, таймаут.</summary>
    Failed,
}

/// <param name="ChangedFiles">Сколько файлов попало в коммит (0, если коммита не было).</param>
/// <param name="Message">Человекочитаемая причина/итог — уходит в лог хаба.</param>
public sealed record KbBackupResult(KbBackupOutcome Outcome, int ChangedFiles, string Message);

/// <summary>Оффсайт-бэкап базы знаний. Отдельный интерфейс — чтобы hub тестировался без git.</summary>
public interface IKbBackup
{
    Task<KbBackupResult> RunAsync(CancellationToken ct);
}
