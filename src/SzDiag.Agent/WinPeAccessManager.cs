namespace SzDiag.Agent;

/// <summary>Доступ в WinPE: открывать нечего и откатывать нечего.
///
/// В PE нет SAM (учётку <c>svc-diag</c> не создать, publickey-логин без неё невозможен),
/// нет Task Scheduler (watchdog-задачу не поставить) и нет службы фаервола (нечего
/// открывать). Поэтому sshd в PE не поднимаем — диагностика идёт через exec/diag
/// поверх исходящего SignalR.
///
/// Инвариант «каждый шаг Open парен шагу Revert» соблюдается тривиально: ни один флаг
/// в <see cref="RevertState"/> не выставлен, откатывать нечего. Следов не остаётся и
/// физически — <c>X:</c> это RAM-диск, ребут стирает его целиком.</summary>
public sealed class WinPeAccessManager : ISystemAccessManager
{
    public RevertState Open(AccessSpec spec) => new() { Sz = spec.Sz };

    public RevertOutcome Revert(RevertState state)
        => new(Array.Empty<string>(), Array.Empty<RevertStepFailure>());   // нечего откатывать

    public void Resume(RevertState state, AccessSpec spec) { /* PE не переживает ребут */ }
}
