namespace SzDiag.Agent;

/// <summary>Windows-операции открытия/отката доступа. Реализация меняет систему.</summary>
public interface ISystemAccessManager
{
    /// <summary>Открыть доступ по spec. Возвращает состояние для последующего отката.</summary>
    RevertState Open(AccessSpec spec);

    /// <summary>Откатить только применённые шаги. Обязана быть идемпотентной.</summary>
    void Revert(RevertState state);
}
