namespace SzDiag.Agent;

/// <summary>Windows-операции открытия/отката доступа. Реализация меняет систему.</summary>
public interface ISystemAccessManager
{
    /// <summary>Открыть доступ по spec. Возвращает состояние для последующего отката.</summary>
    RevertState Open(AccessSpec spec);

    /// <summary>Откатить только применённые шаги. Обязана быть идемпотентной и **не бросать**:
    /// упавший шаг попадает в <see cref="RevertOutcome.Failed"/>, но остальные всё равно
    /// откатываются — иначе одно исключение оставляет следы на клиентской машине (п.59).</summary>
    RevertOutcome Revert(RevertState state);

    /// <summary>Переподнять доступ после ребута из сохранённого state (только sshd +
    /// сдвиг watchdog); user/firewall/token policy переживают ребут и не трогаются.</summary>
    void Resume(RevertState state, AccessSpec spec);
}
