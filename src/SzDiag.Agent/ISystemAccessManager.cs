namespace SzDiag.Agent;

/// <summary>Windows-операции открытия/отката доступа. Реализация меняет систему.</summary>
public interface ISystemAccessManager
{
    /// <summary>Открыть доступ по spec. Возвращает состояние для последующего отката.</summary>
    RevertState Open(AccessSpec spec);

    /// <summary>Откатить только применённые шаги. Обязана быть идемпотентной.</summary>
    void Revert(RevertState state);

    /// <summary>Переподнять доступ после ребута из сохранённого state (только sshd +
    /// сдвиг watchdog); user/firewall/token policy переживают ребут и не трогаются.</summary>
    void Resume(RevertState state, AccessSpec spec);
}
